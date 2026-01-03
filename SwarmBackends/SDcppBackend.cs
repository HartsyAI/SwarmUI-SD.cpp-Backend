using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.SDcppExtension.Utils;
using Hartsy.Extensions.SDcppExtension;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace Hartsy.Extensions.SDcppExtension.SwarmBackends;

/// <summary>
/// Backend implementation that integrates stable-diffusion.cpp with SwarmUI.
/// Provides text-to-image, image-to-image, and inpainting capabilities through SD.cpp CLI execution.
/// Manages model loading, generation requests, and process lifecycle for optimal performance.
/// </summary>
public class SDcppBackend : AbstractT2IBackend
{
    /// <summary>
    /// Configuration settings for the SD.cpp backend, required by SwarmUI's backend registration system.
    /// SD.cpp will be automatically downloaded and configured based on your device selection.
    /// </summary>
    public class SDcppBackendSettings : AutoConfiguration
    {
        [ConfigComment("Which compute device to use for image generation.\n'CPU' uses your processor (slower but works on any system).\n'GPU (CUDA)' uses NVIDIA graphics cards with CUDA support.\n'GPU (Vulkan)' uses any modern graphics card with Vulkan support.\nNote: Flux models may not work reliably with Vulkan - use CUDA or CPU instead.")]
        [ManualSettingsOptions(Impl = null, Vals = ["cpu", "cuda", "vulkan"], ManualNames = ["CPU (Universal)", "GPU (CUDA - NVIDIA)", "GPU (Vulkan - Any GPU)"])]
        public string Device = "cpu";

        [ConfigComment("Number of CPU threads to use during generation (0 for auto-detect based on your CPU).")]
        public int Threads = 0;

        [ConfigComment("Maximum time to wait for image generation before timing out (in seconds).")]
        public int ProcessTimeoutSeconds = 3600;

        [ConfigComment("Whether SD.cpp should automatically check for updates and download newer versions.\nUpdates are checked at most once per 24 hours to avoid excessive GitHub API usage.")]
        [ManualSettingsOptions(Impl = null, Vals = ["true", "false"], ManualNames = ["Auto-Update", "Don't Update"])]
        public string AutoUpdate = "true";

        [ConfigComment("Enable debug logging to help troubleshoot issues.")]
        public bool DebugMode = false;

        // Internal settings - not exposed to user
        [SettingHidden]
        internal string ExecutablePath = "";
        [SettingHidden]
        internal string WorkingDirectory = "";
    }
    /// <summary>Configuration settings controlling SD.cpp behavior, paths, and optimization flags</summary>
    public SDcppBackendSettings Settings => SettingsRaw as SDcppBackendSettings;

    /// <summary>
    /// Manages SD.cpp process lifecycle, command execution, and output capture.
    /// </summary>
    public SDcppProcessManager ProcessManager;

    /// <summary>
    /// Currently loaded model architecture type (SD15, SDXL, Flux, etc.)
    /// </summary>
    public string CurrentModelArchitecture { get; set; } = "unknown";

    /// <summary>
    /// Features supported by this backend - includes txt2img, img2img, inpainting, and various samplers.
    /// Note: Some features are model-dependent (e.g., LoRA requires q8_0 quantization for Flux).
    /// </summary>
    public override IEnumerable<string> SupportedFeatures
    {
        get
        {
            List<string> features =
            [
                "sdcpp",  // Unique backend identifier
                "txt2img",
                "img2img",
                "inpainting",
                "negative_prompt",
                "batch_generation",
                "vae_tiling"
            ];

            // Add GGUF support indicator
            features.Add("gguf");

            // Add architecture-specific features based on currently loaded model
            switch (CurrentModelArchitecture)
            {
                case "flux":
                    features.Add("flux");
                    features.Add("flux-dev");
                    // LoRA only works with q8_0 quantization for Flux
                    features.Add("lora");
                    // Flux supports ControlNet (experimental)
                    features.Add("controlnet");
                    break;

                case "sd3":
                    features.Add("sd3");
                    features.Add("sd3.5");
                    features.Add("lora");
                    break;

                case "sdxl":
                case "sdxl-turbo":
                    features.Add("sdxl");
                    features.Add("lora");
                    features.Add("controlnet");
                    if (CurrentModelArchitecture == "sdxl-turbo")
                        features.Add("turbo");
                    break;

                case "sd15":
                case "sd15-turbo":
                case "sd2":
                    features.Add("lora");
                    features.Add("controlnet");
                    if (CurrentModelArchitecture.Contains("turbo"))
                        features.Add("turbo");
                    break;

                case "lcm":
                    features.Add("lcm");
                    features.Add("lora");
                    features.Add("controlnet");
                    break;

                case "z-image":
                    features.Add("z-image");
                    features.Add("lora");
                    break;

                default:
                    // Standard models get LoRA and ControlNet support
                    features.Add("lora");
                    features.Add("controlnet");
                    break;
            }

            return features;
        }
    }

    /// <summary>
    /// Adds a status message during backend initialization
    /// </summary>
    public new void AddLoadStatus(string message)
    {
        Logs.Info($"[SDcpp] {message}");
        // TODO: If SwarmUI has a status reporting mechanism, use it here
    }

    /// <summary>Lock object for model downloads to prevent duplicate downloads.</summary>
    private static readonly FreneticUtilities.FreneticToolkit.LockObject ModelDownloadLock = new();

    /// <summary>
    /// Ensures a required model file exists, downloading it if necessary.
    /// Follows the same pattern as ComfyUI backend's RequireClipModel.
    /// </summary>
    /// <param name="modelType">Model type folder (e.g., "VAE", "Clip")</param>
    /// <param name="fileName">Target filename (e.g., "Flux/ae.safetensors")</param>
    /// <param name="url">Download URL</param>
    /// <param name="hash">SHA256 hash for verification (optional)</param>
    /// <returns>Full path to the model file, or null if download failed</returns>
    private static string EnsureModelExists(string modelType, string fileName, string url, string hash = null)
    {
        var modelSet = Program.T2IModelSets[modelType];
        
        // Check if model already exists in registry
        if (modelSet.Models.ContainsKey(fileName))
        {
            return modelSet.Models[fileName].RawFilePath;
        }

        // Also check without subfolder prefix
        string baseName = Path.GetFileName(fileName);
        var existing = modelSet.Models.Values.FirstOrDefault(m => 
            m.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
            m.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing.RawFilePath;
        }

        // Need to download - get the download folder
        string downloadFolder = modelSet.FolderPaths[0];
        string filePath = Path.Combine(downloadFolder, fileName);
        string fileDir = Path.GetDirectoryName(filePath);

        // Create subdirectory if needed
        if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
        {
            Directory.CreateDirectory(fileDir);
        }

        // Check if file already exists on disk but not in registry
        if (File.Exists(filePath))
        {
            Logs.Info($"[SDcpp] Found existing file at {filePath}, refreshing model list...");
            Program.RefreshAllModelSets();
            return filePath;
        }

        // Download the file
        lock (ModelDownloadLock)
        {
            // Double-check after acquiring lock
            if (File.Exists(filePath))
            {
                return filePath;
            }

            Logs.Info($"[SDcpp] Downloading {fileName}...");
            Logs.Info($"[SDcpp] URL: {url}");
            string tmpPath = $"{filePath}.tmp";

            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }

                double nextPerc = 0.1;
                Utilities.DownloadFile(url, tmpPath, (bytes, total, perSec) =>
                {
                    double perc = bytes / (double)total;
                    if (perc >= nextPerc)
                    {
                        double mbDownloaded = bytes / 1024.0 / 1024.0;
                        double mbTotal = total / 1024.0 / 1024.0;
                        double mbPerSec = perSec / 1024.0 / 1024.0;
                        Logs.Info($"[SDcpp] {fileName}: {perc * 100:0.0}% ({mbDownloaded:0.1}/{mbTotal:0.1} MB, {mbPerSec:0.1} MB/s)");
                        nextPerc = Math.Round(perc / 0.1) * 0.1 + 0.1;
                    }
                }, verifyHash: hash).Wait();

                File.Move(tmpPath, filePath);
                Logs.Info($"[SDcpp] Successfully downloaded {fileName}");

                // Refresh model list so the new model is recognized
                Program.RefreshAllModelSets();
                return filePath;
            }
            catch (Exception ex)
            {
                Logs.Error($"[SDcpp] Failed to download {fileName}: {ex.Message}");
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Validates the configured device against available hardware using NvidiaUtil and adjusts Settings.Device if needed.
    /// Helps fail fast when CUDA is requested but no NVIDIA GPU is present.
    /// </summary>
    private void ValidateAndConfigureDevice()
    {
        try
        {
            string configured = (Settings.Device ?? "cpu").ToLowerInvariant();
            var nvidiaInfo = NvidiaUtil.QueryNvidia();

            if (configured == "cuda")
            {
                if (nvidiaInfo == null || nvidiaInfo.Length == 0)
                {
                    Logs.Warning("[SDcpp] Device is set to CUDA but no NVIDIA GPU was detected via nvidia-smi. Falling back to CPU for SD.cpp backend.");
                    AddLoadStatus("No NVIDIA GPU detected by nvidia-smi. Falling back to CPU device for SD.cpp backend.");
                    Settings.Device = "cpu";
                }
                else
                {
                    var primary = nvidiaInfo[0];
                    Logs.Info($"[SDcpp] Using CUDA device on NVIDIA GPU: {primary.GPUName} (driver {primary.DriverVersion})");
                }
            }
            else if (configured == "cpu" && nvidiaInfo != null && nvidiaInfo.Length > 0)
            {
                Logs.Info("[SDcpp] NVIDIA GPU detected but SD.cpp backend device is set to CPU. For best performance, change the device to 'GPU (CUDA - NVIDIA)' in backend settings.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error while validating device configuration: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes the SD.cpp backend by loading configuration, validating the executable and runtime,
    /// and setting up the process manager. Called once during SwarmUI startup.
    /// </summary>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[SDcpp] Initializing SD.cpp backend");
            AddLoadStatus("Starting SD.cpp backend initialization...");

            // Validate configured device against available hardware
            ValidateAndConfigureDevice();

            // Ensure SD.cpp is available, download device-specific binary if needed
            AddLoadStatus($"Checking for SD.cpp binary (device: {Settings.Device})...");
            bool autoUpdate = Settings.AutoUpdate?.ToLowerInvariant() == "true";
            string updatedExecutablePath = await SDcppDownloadManager.EnsureSDcppAvailable(Settings.ExecutablePath, Settings.Device, autoUpdate);
            if (updatedExecutablePath != Settings.ExecutablePath)
            {
                // Update the internal settings with the new path
                Settings.ExecutablePath = updatedExecutablePath;
                AddLoadStatus($"SD.cpp binary configured: {Path.GetFileName(updatedExecutablePath)}");
                Logs.Info($"[SDcpp] Updated executable path to: {updatedExecutablePath}");
            }

            ProcessManager = new SDcppProcessManager(Settings);
            AddLoadStatus("Validating SD.cpp executable and runtime...");
            if (!ProcessManager.ValidateRuntime(out string runtimeError))
            {
                // If CUDA runtime validation failed, try falling back to CPU
                if (string.Equals(Settings.Device, "cuda", StringComparison.OrdinalIgnoreCase) && runtimeError.Contains("CUDA"))
                {
                    Logs.Warning($"[SDcpp] CUDA runtime validation failed. Attempting fallback to CPU device...");
                    AddLoadStatus("CUDA runtime not found. Falling back to CPU device...");

                    // Try CPU fallback - pass empty string to force download of CPU-specific binary
                    Settings.Device = "cpu";
                    bool enableAutoUpdate = Settings.AutoUpdate?.ToLowerInvariant() == "true";
                    string cpuExecutablePath = await SDcppDownloadManager.EnsureSDcppAvailable("", "cpu", enableAutoUpdate);
                    if (!string.IsNullOrEmpty(cpuExecutablePath) && File.Exists(cpuExecutablePath))
                    {
                        Settings.ExecutablePath = cpuExecutablePath;
                        Logs.Info($"[SDcpp] Updated executable path to CPU version: {cpuExecutablePath}");
                    }

                    ProcessManager = new SDcppProcessManager(Settings);
                    if (!ProcessManager.ValidateRuntime(out string cpuRuntimeError))
                    {
                        Status = BackendStatus.ERRORED;
                        Logs.Error($"[SDcpp] CPU fallback also failed: {cpuRuntimeError}");
                        AddLoadStatus("SD.cpp backend disabled: Could not initialize CUDA or CPU device.");
                        return;
                    }

                    Logs.Info("[SDcpp] Successfully fell back to CPU device");
                    AddLoadStatus("Using CPU device (CUDA runtime not available)");
                }
                else
                {
                    Status = BackendStatus.ERRORED;
                    Logs.Error($"[SDcpp] SD.cpp runtime validation failed: {runtimeError}");
                    AddLoadStatus("SD.cpp backend disabled: " + runtimeError);
                    return;
                }
            }

            // Populate models dictionary for SwarmUI's backend matching system
            PopulateModelsDict();

            Status = BackendStatus.RUNNING;
            Logs.Info("[SDcpp] Backend initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Failed to initialize backend: {ex.Message}");
            Status = BackendStatus.ERRORED;
            throw;
        }
    }

    /// <summary>
    /// Populates the Models dictionary with supported models from SwarmUI's registry.
    /// This helps SwarmUI's backend matcher determine which models this backend can handle.
    /// </summary>
    private void PopulateModelsDict()
    {
        try
        {
            // Ensure Models dictionary is initialized
            Models ??= new ConcurrentDictionary<string, List<string>>();

            // Check if model sets are initialized
            if (Program.T2IModelSets == null)
            {
                Logs.Debug("[SDcpp] Model sets not yet initialized, skipping model population");
                return;
            }

            // Populate Stable Diffusion models
            if (Program.T2IModelSets.TryGetValue("Stable-Diffusion", out var sdModelSet) && sdModelSet?.Models != null)
            {
                Models["Stable-Diffusion"] = sdModelSet.Models.Values
                    .Where(m => m != null && IsSupportedBySDcpp(m))
                    .Select(m => m.Name)
                    .ToList();

                Logs.Debug($"[SDcpp] Found {Models["Stable-Diffusion"].Count} compatible SD models");
            }

            // Populate LoRA models
            if (Program.T2IModelSets.TryGetValue("Lora", out var loraModelSet) && loraModelSet?.Models != null)
            {
                Models["LoRA"] = loraModelSet.Models.Values
                    .Where(m => m != null)
                    .Select(m => m.Name)
                    .ToList();

                Logs.Debug($"[SDcpp] Found {Models["LoRA"].Count} LoRA models");
            }

            // Populate VAE models
            if (Program.T2IModelSets.TryGetValue("VAE", out var vaeModelSet) && vaeModelSet?.Models != null)
            {
                Models["VAE"] = vaeModelSet.Models.Values
                    .Where(m => m != null && IsSupportedVAE(m))
                    .Select(m => m.Name)
                    .ToList();

                Logs.Debug($"[SDcpp] Found {Models["VAE"].Count} VAE models");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error populating models dictionary: {ex.Message}");
            Logs.Debug($"[SDcpp] Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Refreshes the models dictionary when SwarmUI detects model changes.
    /// Called by the extension when model refresh events are triggered.
    /// </summary>
    public void RefreshModels()
    {
        try
        {
            Logs.Debug("[SDcpp] Refreshing models list");
            PopulateModelsDict();
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error refreshing models: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines if a model is supported by SD.cpp based on file format and architecture.
    /// </summary>
    private bool IsSupportedBySDcpp(T2IModel model)
    {
        if (model == null) return false;

        // Check file extension
        var ext = Path.GetExtension(model.RawFilePath).ToLowerInvariant();
        if (ext != ".safetensors" && ext != ".gguf" && ext != ".ckpt" && ext != ".bin" && ext != ".sft")
            return false;

        // All supported formats are compatible with SD.cpp
        return true;
    }

    /// <summary>
    /// Determines if a VAE model is supported by SD.cpp.
    /// </summary>
    private bool IsSupportedVAE(T2IModel model)
    {
        if (model == null) return false;

        var ext = Path.GetExtension(model.RawFilePath).ToLowerInvariant();
        return ext == ".safetensors" || ext == ".sft" || ext == ".ckpt" || ext == ".bin";
    }

    /// <summary>
    /// Determines if the given model is a Flux architecture model.
    /// Checks model class, filename, and metadata to identify Flux models.
    /// </summary>
    /// <param name="model">Model to check</param>
    /// <returns>True if model is Flux architecture, false otherwise</returns>
    public static bool IsFluxModel(T2IModel model)
    {
        if (model == null) return false;

        // Check model class
        if (model.ModelClass != null && model.ModelClass.ID.ToLowerInvariant().Contains("flux"))
            return true;

        // Check filename
        string filename = Path.GetFileNameWithoutExtension(model.RawFilePath).ToLowerInvariant();
        if (filename.Contains("flux"))
            return true;

        // Check model name
        if (model.Name.ToLowerInvariant().Contains("flux"))
            return true;

        return false;
    }

    /// <summary>
    /// Determines if the given model is an SD3/SD3.5 architecture model.
    /// </summary>
    public static bool IsSD3Model(T2IModel model)
    {
        if (model == null) return false;

        // Check model class
        if (model.ModelClass != null && model.ModelClass.ID.ToLowerInvariant().Contains("sd3"))
            return true;

        // Check filename
        string filename = Path.GetFileNameWithoutExtension(model.RawFilePath).ToLowerInvariant();
        if (filename.Contains("sd3") || filename.Contains("sd3.5"))
            return true;

        // Check model name
        if (model.Name.ToLowerInvariant().Contains("sd3"))
            return true;

        return false;
    }

    /// <summary>
    /// Determines the model architecture type (Flux, SD3, SDXL, SD15, etc.)
    /// </summary>
    /// <param name="model">Model to analyze</param>
    /// <returns>Architecture identifier string</returns>
    public static string DetectModelArchitecture(T2IModel model)
    {
        if (model == null) return "unknown";

        string filename = Path.GetFileNameWithoutExtension(model.RawFilePath).ToLowerInvariant();
        string modelName = model.Name.ToLowerInvariant();
        string modelClass = model.ModelClass?.ID.ToLowerInvariant() ?? "";

        // Check for Flux first (most specific)
        if (IsFluxModel(model))
            return "flux";

        // Check for SD3/SD3.5
        if (IsSD3Model(model))
            return "sd3";

        // Check for Z-Image models
        if (filename.Contains("z_image") || filename.Contains("z-image") ||
            modelName.Contains("z_image") || modelName.Contains("z-image") ||
            modelClass.Contains("z-image"))
            return "z-image";

        // Check for video models (Wan 2.1/2.2, LTX-V, etc.)
        // Wan models
        if (filename.Contains("wan") || modelName.Contains("wan") || modelClass.Contains("wan"))
        {
            // Detect specific Wan variants
            if (filename.Contains("2.2") || modelName.Contains("2.2") || filename.Contains("2_2") || modelName.Contains("2_2"))
                return "wan-2.2";
            if (filename.Contains("2.1") || modelName.Contains("2.1") || filename.Contains("2_1") || modelName.Contains("2_1"))
                return "wan-2.1";
            return "wan";  // Generic Wan model
        }

        // Other video model types
        if (modelClass.Contains("-i2v") || modelClass.Contains("image2video") || modelClass.Contains("-ti2v") ||
            modelClass.Contains("-flf2v") || modelClass.Contains("video2world") ||
            filename.Contains("i2v") || filename.Contains("ti2v") || filename.Contains("flf2v"))
            return "video";

        // Check for SDXL variants
        if (modelClass.Contains("sdxl") || filename.Contains("sdxl"))
        {
            // Check for SDXL Turbo
            if (filename.Contains("turbo") || modelName.Contains("turbo"))
                return "sdxl-turbo";

            return "sdxl";
        }

        // Check for SD 2.x
        if (modelClass.Contains("stable-diffusion-v2") || modelClass.Contains("stable-diffusion-2"))
            return "sd2";

        // Check for SD 1.5
        if (modelClass.Contains("stable-diffusion-v1") || modelClass.Contains("stable-diffusion-1"))
        {
            // Check for SD Turbo
            if (filename.Contains("turbo") || modelName.Contains("turbo"))
                return "sd15-turbo";

            return "sd15";
        }

        // Check for LCM (Latent Consistency Models)
        if (filename.Contains("lcm") || modelName.Contains("lcm"))
            return "lcm";

        // Default assumption based on resolution
        if (model.StandardWidth == 1024 && model.StandardHeight == 1024)
            return "sdxl";
        if (model.StandardWidth == 512 && model.StandardHeight == 512)
            return "sd15";

        return "unknown";
    }

    /// <summary>
    /// Loads the specified model for use in generation. For SD.cpp, this sets the current model name
    /// as the model path will be passed to the CLI process during generation execution.
    /// SwarmUI manages all model components (VAE, CLIP, T5XXL) via its parameter system.
    /// </summary>
    /// <param name="model">The model to load from SwarmUI's model registry</param>
    /// <param name="input">Generation parameters that may influence model loading</param>
    /// <returns>True if model loading succeeded, false otherwise</returns>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Info($"[SDcpp] Loading model: {model.Name}");

            // Validate model file exists
            if (!File.Exists(model.RawFilePath))
            {
                Logs.Error($"[SDcpp] Model file not found: {model.RawFilePath}");
                return false;
            }

            // Check if model is compatible (basic file extension check)
            var extension = Path.GetExtension(model.RawFilePath).ToLowerInvariant();
            if (extension != ".safetensors" && extension != ".ckpt" && extension != ".bin" && extension != ".gguf" && extension != ".sft")
            {
                Logs.Warning($"[SDcpp] Model file may not be compatible: {model.RawFilePath}");
            }

            // Detect model architecture
            CurrentModelArchitecture = DetectModelArchitecture(model);
            CurrentModelName = model.Name;

            Logs.Info($"[SDcpp] Model loaded successfully: {model.Name} (Architecture: {CurrentModelArchitecture})");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error loading model {model.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates and adjusts parameters specifically for Flux models.
    /// Flux has specific requirements like CFG=1.0, euler sampler, and minimum steps.
    /// </summary>
    /// <param name="input">Generation parameters to validate</param>
    private void ValidateFluxParameters(T2IParamInput input)
    {
        try
        {
            // Flux doesn't use CFG scale effectively - always use 1.0
            if (input.TryGet(T2IParamTypes.CFGScale, out double cfgScale))
            {
                if (cfgScale != 1.0)
                {
                    Logs.Warning($"[SDcpp] Flux models work best with CFG scale 1.0 (current: {cfgScale})");
                }
            }

            // Warn if negative prompt is used (not effective with Flux)
            if (input.TryGet(T2IParamTypes.NegativePrompt, out string negPrompt))
            {
                if (!string.IsNullOrWhiteSpace(negPrompt))
                {
                    Logs.Warning($"[SDcpp] Flux models do not benefit from negative prompts");
                    Logs.Warning($"[SDcpp] Negative prompt will be ignored or may reduce quality");
                }
            }

            // Validate steps
            if (input.TryGet(T2IParamTypes.Steps, out int steps))
            {
                bool isSchnell = CurrentModelName.ToLowerInvariant().Contains("schnell");

                if (isSchnell && steps < 4)
                {
                    Logs.Warning($"[SDcpp] Flux-schnell requires at least 4 steps (current: {steps})");
                }
                else if (!isSchnell && steps < 20)
                {
                    Logs.Warning($"[SDcpp] Flux-dev works best with 20+ steps (current: {steps})");
                    Logs.Info($"[SDcpp] Results may be lower quality with fewer steps");
                }
            }

            // Validate dimensions are multiples of 64 (SD.cpp requirement)
            if (input.TryGet(T2IParamTypes.Width, out int width))
            {
                if (width % 64 != 0)
                {
                    Logs.Warning($"[SDcpp] Width must be a multiple of 64 for SD.cpp (current: {width})");
                }
            }

            if (input.TryGet(T2IParamTypes.Height, out int height))
            {
                if (height % 64 != 0)
                {
                    Logs.Warning($"[SDcpp] Height must be a multiple of 64 for SD.cpp (current: {height})");
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error during Flux parameter validation: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes image generation with live progress updates sent to the user.
    /// Overrides the default GenerateLive to provide real-time feedback during generation.
    /// </summary>
    /// <param name="user_input">Complete generation parameters</param>
    /// <param name="batchId">Unique identifier for this batch</param>
    /// <param name="takeOutput">Callback to send progress updates and final images</param>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        try
        {
            if (ProcessManager == null)
            {
                throw new InvalidOperationException("Process manager not initialized");
            }

            Logs.Info("[SDcpp] Starting live image generation");

            // Validate Flux-specific parameters if using Flux model
            bool isFluxModel = CurrentModelArchitecture == "flux";
            if (isFluxModel)
            {
                ValidateFluxParameters(user_input);
            }

            // Create temporary output directory
            string tempDir = Path.Combine(Path.GetTempPath(), "sdcpp_output", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Build parameters for SD.cpp
                Dictionary<string, object> parameters = BuildGenerationParameters(user_input, tempDir);

                if (Settings.DebugMode)
                {
                    Logs.Debug($"[SDcpp] Generation parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
                }

                long startTime = Environment.TickCount64;

                // Execute SD.cpp with live progress updates
                (bool success, string output, string error) = await ProcessManager.ExecuteWithProgressAsync(
                    parameters,
                    isFluxModel,
                    progress =>
                    {
                        // Send progress update to UI
                        takeOutput(new Newtonsoft.Json.Linq.JObject
                        {
                            ["preview_progress"] = progress
                        });
                    });

                long genTime = Environment.TickCount64 - startTime;

                if (!success)
                {
                    Logs.Error($"[SDcpp] Generation failed: {error}");
                    throw new Exception($"SD.cpp generation failed: {error}");
                }

                // Collect generated images
                Image[] images = await CollectGeneratedImages(tempDir, user_input);

                Logs.Info($"[SDcpp] Generated {images.Length} images successfully in {genTime}ms");

                // Send final images to user
                foreach (Image img in images)
                {
                    takeOutput(img);
                }
            }
            finally
            {
                // Cleanup temporary directory
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[SDcpp] Failed to cleanup temp directory: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Generation error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Executes image generation using SD.cpp with the provided parameters.
    /// Handles txt2img, img2img, and inpainting based on input parameters.
    /// Creates temporary directories, builds CLI arguments, executes the process, and collects generated images.
    /// </summary>
    /// <param name="input">Complete generation parameters including prompt, dimensions, model, etc.</param>
    /// <returns>Array of generated images, or empty array if generation failed</returns>
    public override async Task<Image[]> Generate(T2IParamInput input)
    {
        try
        {
            if (ProcessManager == null)
            {
                throw new InvalidOperationException("Process manager not initialized");
            }

            Logs.Info("[SDcpp] Starting image generation");

            // Validate Flux-specific parameters if using Flux model
            bool isFluxModel = CurrentModelArchitecture == "flux";
            if (isFluxModel)
            {
                ValidateFluxParameters(input);
            }

            // Create temporary output directory
            string tempDir = Path.Combine(Path.GetTempPath(), "sdcpp_output", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Build parameters for SD.cpp
                Dictionary<string, object> parameters = BuildGenerationParameters(input, tempDir);

                if (Settings.DebugMode)
                {
                    Logs.Debug($"[SDcpp] Generation parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
                }

                // Execute SD.cpp with Flux flag
                (bool success, string output, string error) = await ProcessManager.ExecuteAsync(parameters, isFluxModel);

                if (!success)
                {
                    Logs.Error($"[SDcpp] Generation failed: {error}");
                    throw new Exception($"SD.cpp generation failed: {error}");
                }

                // Collect generated images
                Image[] images = await CollectGeneratedImages(tempDir, input);

                Logs.Info($"[SDcpp] Generated {images.Length} images successfully");
                return images;
            }
            finally
            {
                // Cleanup temporary directory
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[SDcpp] Failed to cleanup temp directory: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Generation error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Converts SwarmUI generation parameters into SD.cpp CLI arguments format.
    /// Maps prompts, dimensions, sampling settings, and model paths to the parameter dictionary
    /// that will be used by the process manager to build command-line arguments.
    /// Handles both standard SD models and Flux multi-component architecture.
    /// </summary>
    /// <param name="input">SwarmUI generation parameters</param>
    /// <param name="outputDir">Directory where SD.cpp should save generated images</param>
    /// <returns>Dictionary of parameters formatted for SD.cpp CLI execution</returns>
    public Dictionary<string, object> BuildGenerationParameters(T2IParamInput input, string outputDir)
    {
        Dictionary<string, object> parameters = [];
        bool isFluxModel = CurrentModelArchitecture == "flux";

        if (SDcppExtension.VAETilingParam is not null)
        {
            parameters["vae_tiling"] = input.Get(SDcppExtension.VAETilingParam, true, autoFixDefault: true);
        }
        if (SDcppExtension.VAEOnCPUParam is not null)
        {
            parameters["vae_on_cpu"] = input.Get(SDcppExtension.VAEOnCPUParam, false, autoFixDefault: true);
        }
        if (SDcppExtension.CLIPOnCPUParam is not null)
        {
            parameters["clip_on_cpu"] = input.Get(SDcppExtension.CLIPOnCPUParam, false, autoFixDefault: true);
        }
        if (SDcppExtension.FlashAttentionParam is not null)
        {
            parameters["flash_attention"] = input.Get(SDcppExtension.FlashAttentionParam, false, autoFixDefault: true);
        }

        // Performance optimization flags
        if (SDcppExtension.MemoryMapParam is not null)
        {
            parameters["mmap"] = input.Get(SDcppExtension.MemoryMapParam, true, autoFixDefault: true);
        }
        if (SDcppExtension.VAEConvDirectParam is not null)
        {
            parameters["vae_conv_direct"] = input.Get(SDcppExtension.VAEConvDirectParam, true, autoFixDefault: true);
        }
        if (SDcppExtension.CacheModeParam is not null && input.TryGet(SDcppExtension.CacheModeParam, out string cacheMode) && cacheMode != "none")
        {
            // Auto-detect best cache mode based on architecture
            if (cacheMode == "auto")
            {
                cacheMode = isFluxModel || CurrentModelArchitecture == "sd3" ? "easycache" : "ucache";
            }
            parameters["cache_mode"] = cacheMode;
        }
        if (SDcppExtension.CachePresetParam is not null && input.TryGet(SDcppExtension.CachePresetParam, out string cachePreset))
        {
            parameters["cache_preset"] = cachePreset;
        }

        if (input.TryGet(T2IParamTypes.Prompt, out string prompt))
        {
            parameters["prompt"] = prompt;
            if (prompt.Contains("<lora:"))
            {
                string loraFolder = Program.ServerSettings.Paths.SDLoraFolder.Split(';')[0];
                string loraDir = Utilities.CombinePathWithAbsolute(Program.ServerSettings.Paths.ActualModelRoot, loraFolder);
                if (Directory.Exists(loraDir))
                {
                    parameters["lora_model_dir"] = loraDir;
                }
            }
        }

        if (input.TryGet(T2IParamTypes.NegativePrompt, out string negPrompt))
        {
            parameters["negative_prompt"] = negPrompt;
        }

        if (input.TryGet(T2IParamTypes.Width, out int width))
        {
            parameters["width"] = width;
        }

        if (input.TryGet(T2IParamTypes.Height, out int height))
        {
            parameters["height"] = height;
        }

        if (input.TryGet(T2IParamTypes.Steps, out int steps))
        {
            if (isFluxModel)
            {
                bool isSchnell = CurrentModelName.ToLowerInvariant().Contains("schnell");
                if (steps == 0)
                {
                    steps = isSchnell ? 4 : 20;
                    Logs.Info($"[SDcpp] Using default Flux steps: {steps}");
                }
                else if (isSchnell && steps < 4)
                {
                    Logs.Warning($"[SDcpp] Flux-schnell requires at least 4 steps (current: {steps})");
                }
            }
            parameters["steps"] = steps;
        }

        if (input.TryGet(T2IParamTypes.CFGScale, out double cfgScale))
        {
            if (isFluxModel && Math.Abs(cfgScale - 1.0) > 0.0001)
            {
                Logs.Warning($"[SDcpp] Flux models work best with CFG scale 1.0 (current: {cfgScale})");
            }
            parameters["cfg_scale"] = cfgScale;
        }

        if (input.TryGet(T2IParamTypes.Seed, out long seed))
        {
            parameters["seed"] = seed;
        }

        // Sampler
        string swarmSampler = isFluxModel ? "euler" : "euler_a";
        if (SDcppExtension.SamplerParam != null && input.TryGet(SDcppExtension.SamplerParam, out string userSampler))
        {
            swarmSampler = userSampler;
        }
        if (isFluxModel && swarmSampler != "euler")
        {
            Logs.Info($"[SDcpp] Flux works best with euler sampler (requested: {swarmSampler})");
            Logs.Info("[SDcpp] Using euler sampler for optimal results");
            swarmSampler = "euler";
        }
        parameters["sampling_method"] = swarmSampler;

        // Scheduler
        if (SDcppExtension.SchedulerParam != null && input.TryGet(SDcppExtension.SchedulerParam, out string scheduler))
        {
            parameters["scheduler"] = scheduler;
        }

        // CLIP Skip - SwarmUI uses ClipStopAtLayer which is negative, SD.cpp uses --clip-skip which is positive
        if (input.TryGet(T2IParamTypes.ClipStopAtLayer, out int clipStopLayer))
        {
            // Convert from SwarmUI's negative layer (e.g., -1, -2) to SD.cpp's positive skip count
            int clipSkip = Math.Abs(clipStopLayer);
            if (clipSkip > 1)
            {
                parameters["clip_skip"] = clipSkip;
            }
        }

        // Batch generation - SwarmUI's Images parameter maps to SD.cpp's batch-count
        if (input.TryGet(T2IParamTypes.Images, out int batchCount) && batchCount > 1)
        {
            parameters["batch_count"] = batchCount;
        }

        // Model path - Flux, SD3, and Z-Image use multi-component architecture with separate encoders
        bool isSD3Model = CurrentModelArchitecture == "sd3";
        bool isZImageModel = CurrentModelArchitecture == "z-image";
        bool isMultiComponentModel = isFluxModel || isSD3Model || isZImageModel;

        if (!string.IsNullOrEmpty(CurrentModelName))
        {
            T2IModel mainModel = Program.T2IModelSets["Stable-Diffusion"].Models.Values
                .FirstOrDefault(m => m.Name == CurrentModelName);

            if (isMultiComponentModel && mainModel != null)
            {
                // Multi-component parameters - use SwarmUI's parameter system
                parameters["diffusion_model"] = mainModel.RawFilePath;
                string archName = isFluxModel ? "Flux" : (isSD3Model ? "SD3" : (isZImageModel ? "Z-Image" : "Multi-Component"));
                Logs.Info($"[SDcpp] Multi-component model detected: {archName}");
                Logs.Info($"[SDcpp] Diffusion model path: {mainModel.RawFilePath}");

                // VAE - Required for Flux, optional for SD3 (has built-in VAE)
                if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel != null && vaeModel.Name != "(none)")
                {
                    parameters["vae"] = vaeModel.RawFilePath;
                    Logs.Debug($"[SDcpp] Using user-specified VAE: {vaeModel.Name}");
                }
                else if (isFluxModel)
                {
                    // Flux requires external VAE - try to find or auto-download
                    var vaeModelSet = Program.T2IModelSets["VAE"];
                    var defaultVae = vaeModelSet.Models.Values.FirstOrDefault(m => 
                        m.Name.Equals("ae.safetensors", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.EndsWith("ae.safetensors", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Contains("flux", StringComparison.OrdinalIgnoreCase) && m.Name.Contains("ae", StringComparison.OrdinalIgnoreCase));
                    
                    if (defaultVae != null)
                    {
                        parameters["vae"] = defaultVae.RawFilePath;
                        Logs.Debug($"[SDcpp] Using existing VAE: {defaultVae.Name}");
                    }
                    else
                    {
                        // Auto-download Flux VAE (using same URL as CommonModels.cs "flux-ae")
                        Logs.Info("[SDcpp] Flux VAE not found, auto-downloading...");
                        string vaePath = EnsureModelExists("VAE", "Flux/ae.safetensors",
                            "https://huggingface.co/mcmonkey/swarm-vaes/resolve/main/flux_ae.safetensors",
                            "afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38");
                        if (!string.IsNullOrEmpty(vaePath))
                        {
                            parameters["vae"] = vaePath;
                        }
                    }
                }
                // SD3 VAE is optional - uses built-in if not specified

                // CLIP-G - Required for SD3, not used by Flux
                if (isSD3Model)
                {
                    if (input.TryGet(T2IParamTypes.ClipGModel, out T2IModel clipGModel) && clipGModel != null)
                    {
                        parameters["clip_g"] = clipGModel.RawFilePath;
                        Logs.Debug($"[SDcpp] Using user-specified CLIP-G: {clipGModel.Name}");
                    }
                    else
                    {
                        var clipModelSet = Program.T2IModelSets["Clip"];
                        var defaultClipG = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("clip_g"));
                        if (defaultClipG != null)
                        {
                            parameters["clip_g"] = defaultClipG.RawFilePath;
                            Logs.Debug($"[SDcpp] Using existing CLIP-G: {defaultClipG.Name}");
                        }
                        else
                        {
                            // Auto-download CLIP-G for SD3
                            Logs.Info("[SDcpp] CLIP-G not found, auto-downloading...");
                            string clipGPath = EnsureModelExists("Clip", "clip_g.safetensors",
                                "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/text_encoder_2/model.fp16.safetensors",
                                "ec310df2af79c318e24d20511b601a591ca8cd4f1fce1d8dff822a356bcdb1f4");
                            if (!string.IsNullOrEmpty(clipGPath))
                            {
                                parameters["clip_g"] = clipGPath;
                            }
                        }
                    }
                }

                // CLIP-L - Required for both Flux and SD3
                if (input.TryGet(T2IParamTypes.ClipLModel, out T2IModel clipLModel) && clipLModel != null)
                {
                    parameters["clip_l"] = clipLModel.RawFilePath;
                    Logs.Debug($"[SDcpp] Using user-specified CLIP-L: {clipLModel.Name}");
                }
                else
                {
                    var clipModelSet = Program.T2IModelSets["Clip"];
                    var defaultClipL = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("clip_l"));
                    if (defaultClipL != null)
                    {
                        parameters["clip_l"] = defaultClipL.RawFilePath;
                        Logs.Debug($"[SDcpp] Using existing CLIP-L: {defaultClipL.Name}");
                    }
                    else
                    {
                        // Auto-download CLIP-L
                        Logs.Info("[SDcpp] CLIP-L not found, auto-downloading...");
                        string clipLPath = EnsureModelExists("Clip", "clip_l.safetensors",
                            "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/text_encoder/model.fp16.safetensors",
                            "660c6f5b1abae9dc498ac2d21e1347d2abdb0cf6c0c0c8576cd796491d9a6cdd");
                        if (!string.IsNullOrEmpty(clipLPath))
                        {
                            parameters["clip_l"] = clipLPath;
                        }
                    }
                }

                // T5-XXL - Required for Flux and SD3, NOT for Z-Image
                if (!isZImageModel)
                {
                    if (input.TryGet(T2IParamTypes.T5XXLModel, out T2IModel t5xxlModel) && t5xxlModel != null)
                    {
                        parameters["t5xxl"] = t5xxlModel.RawFilePath;
                        Logs.Debug($"[SDcpp] Using user-specified T5-XXL: {t5xxlModel.Name}");
                    }
                    else
                    {
                        var clipModelSet = Program.T2IModelSets["Clip"];
                        var defaultT5 = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("t5xxl"));
                        if (defaultT5 != null)
                        {
                            parameters["t5xxl"] = defaultT5.RawFilePath;
                            Logs.Debug($"[SDcpp] Using existing T5-XXL: {defaultT5.Name}");
                        }
                        else
                        {
                            // Auto-download T5-XXL (FP8 version to save space/VRAM)
                            Logs.Info("[SDcpp] T5-XXL not found, auto-downloading (FP8 version)...");
                            string t5Path = EnsureModelExists("Clip", "t5xxl_fp8_e4m3fn.safetensors",
                                "https://huggingface.co/mcmonkey/google_t5-v1_1-xxl_encoderonly/resolve/main/t5xxl_fp8_e4m3fn.safetensors",
                                "7d330da4816157540d6bb7838bf63a0f02f573fc48ca4d8de34bb0cbfd514f09");
                            if (!string.IsNullOrEmpty(t5Path))
                            {
                                parameters["t5xxl"] = t5Path;
                            }
                        }
                    }
                }

                // LLM - Required ONLY for Z-Image models
                if (isZImageModel)
                {
                    // Z-Image uses Qwen LLM instead of T5-XXL for text encoding
                    // First try user-specified Qwen model parameter
                    if (input.TryGet(T2IParamTypes.QwenModel, out T2IModel qwenModel) && qwenModel != null)
                    {
                        parameters["llm"] = qwenModel.RawFilePath;
                        Logs.Debug($"[SDcpp] Using user-specified Qwen model for Z-Image: {qwenModel.Name}");
                    }
                    else
                    {
                        // Check for existing Qwen model in Clip folder (ComfyUI stores them there)
                        var clipModelSet = Program.T2IModelSets["Clip"];
                        var existingQwen = clipModelSet.Models.Values.FirstOrDefault(m =>
                            m.Name.Contains("qwen_3_4b", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("qwen", StringComparison.OrdinalIgnoreCase));

                        if (existingQwen != null)
                        {
                            parameters["llm"] = existingQwen.RawFilePath;
                            Logs.Debug($"[SDcpp] Using existing Qwen model for Z-Image: {existingQwen.Name}");
                        }
                        else
                        {
                            // Auto-download Qwen 3 4B model (same as ComfyUI uses for Z-Image)
                            Logs.Info("[SDcpp] Z-Image requires Qwen model, auto-downloading...");
                            string qwenPath = EnsureModelExists("Clip", "qwen_3_4b.safetensors",
                                "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/text_encoders/qwen_3_4b.safetensors",
                                "6c671498573ac2f7a5501502ccce8d2b08ea6ca2f661c458e708f36b36edfc5a");
                            if (!string.IsNullOrEmpty(qwenPath))
                            {
                                parameters["llm"] = qwenPath;
                            }
                        }
                    }
                }

                // Validate required components - fail early with helpful error
                if (isFluxModel)
                {
                    List<string> missing = [];
                    if (!parameters.ContainsKey("vae")) missing.Add("VAE (ae.safetensors)");
                    if (!parameters.ContainsKey("clip_l")) missing.Add("CLIP-L (clip_l.safetensors)");
                    if (!parameters.ContainsKey("t5xxl")) missing.Add("T5-XXL (t5xxl_fp16.safetensors or t5xxl_fp8_e4m3fn.safetensors)");

                    if (missing.Count > 0)
                    {
                        string errorMsg = $"Flux models require additional component files that are not installed.\n\n" +
                            $"Missing components:\n  • {string.Join("\n  • ", missing)}\n\n" +
                            $"Download these files and place them in the appropriate folders:\n" +
                            $"  • VAE → Models/VAE/\n" +
                            $"  • CLIP-L, T5-XXL → Models/clip/\n\n" +
                            $"Download links (Hugging Face):\n" +
                            $"  • VAE: https://huggingface.co/black-forest-labs/FLUX.1-dev/blob/main/ae.safetensors\n" +
                            $"  • CLIP-L: https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/clip_l.safetensors\n" +
                            $"  • T5-XXL (FP16): https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/t5xxl_fp16.safetensors\n" +
                            $"  • T5-XXL (FP8, smaller): https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/t5xxl_fp8_e4m3fn.safetensors\n\n" +
                            $"After downloading, refresh models in SwarmUI and try again.";
                        Logs.Error($"[SDcpp] {errorMsg}");
                        throw new InvalidOperationException(errorMsg);
                    }
                    Logs.Info("[SDcpp] All Flux components found successfully");
                }
                else if (isSD3Model)
                {
                    List<string> missing = [];
                    if (!parameters.ContainsKey("clip_g")) missing.Add("CLIP-G (clip_g.safetensors)");
                    if (!parameters.ContainsKey("clip_l")) missing.Add("CLIP-L (clip_l.safetensors)");
                    if (!parameters.ContainsKey("t5xxl")) missing.Add("T5-XXL (t5xxl_fp16.safetensors)");

                    if (missing.Count > 0)
                    {
                        string errorMsg = $"SD3 models require additional component files that are not installed.\n\n" +
                            $"Missing components:\n  • {string.Join("\n  • ", missing)}\n\n" +
                            $"Download these files and place them in Models/clip/ folder.\n\n" +
                            $"After downloading, refresh models in SwarmUI and try again.";
                        Logs.Error($"[SDcpp] {errorMsg}");
                        throw new InvalidOperationException(errorMsg);
                    }
                    Logs.Info("[SDcpp] All SD3 components found successfully");
                }
                else if (isZImageModel)
                {
                    List<string> missing = [];
                    if (!parameters.ContainsKey("vae")) missing.Add("VAE (flux-ae.safetensors)");
                    if (!parameters.ContainsKey("llm")) missing.Add("Qwen LLM (qwen_3_4b.safetensors)");

                    if (missing.Count > 0)
                    {
                        string errorMsg = $"Z-Image models require additional component files that are not installed.\n\n" +
                            $"Missing components:\n  • {string.Join("\n  • ", missing)}\n\n" +
                            $"SwarmUI should auto-download these. If download failed, manually download and place in:\n" +
                            $"  • VAE → Models/VAE/\n  • Qwen LLM → Models/clip/\n\n" +
                            $"After downloading, refresh models in SwarmUI and try again.";
                        Logs.Error($"[SDcpp] {errorMsg}");
                        throw new InvalidOperationException(errorMsg);
                    }
                    Logs.Info("[SDcpp] All Z-Image components found successfully");
                }
            }
            else if (mainModel != null)
            {
                // Standard SD model
                parameters["model"] = mainModel.RawFilePath;
            }
        }

        // Output directory (SD.cpp will generate files in this directory)
        // Don't specify exact filename, let SD.cpp handle naming
        parameters["output"] = Path.Combine(outputDir, "generated_%03d.png");

        // Image-to-image parameters
        if (input.TryGet(T2IParamTypes.InitImage, out Image initImage))
        {
            string initImagePath = Path.Combine(outputDir, "init.png");
            File.WriteAllBytes(initImagePath, initImage.RawData);
            parameters["init_img"] = initImagePath;

            if (input.TryGet(T2IParamTypes.InitImageCreativity, out double strength))
                parameters["strength"] = strength;
        }

        // Inpainting/Mask parameters
        if (input.TryGet(T2IParamTypes.MaskImage, out Image maskImage))
        {
            string maskImagePath = Path.Combine(outputDir, "mask.png");
            File.WriteAllBytes(maskImagePath, maskImage.RawData);
            parameters["mask"] = maskImagePath;
        }

        // ControlNet support (up to 3 ControlNets)
        for (int i = 0; i < T2IParamTypes.Controlnets.Length; i++)
        {
            var cn = T2IParamTypes.Controlnets[i];

            // Check if this ControlNet is enabled and has a model
            if (input.TryGet(cn.Model, out T2IModel cnModel) && cnModel != null && cnModel.Name != "(None)")
            {
                // Get control image (use ControlNet input image, or fallback to init image)
                Image controlImage = null;
                if (input.TryGet(cn.Image, out Image cnImage))
                {
                    controlImage = cnImage;
                }
                else if (input.TryGet(T2IParamTypes.InitImage, out Image fallbackImage))
                {
                    controlImage = fallbackImage;
                    Logs.Info($"[SDcpp] ControlNet{cn.NameSuffix} using Init Image as control input");
                }

                if (controlImage != null)
                {
                    // Save control image
                    string controlImagePath = Path.Combine(outputDir, $"control{i + 1}.png");
                    File.WriteAllBytes(controlImagePath, controlImage.RawData);

                    // SD.cpp currently supports single ControlNet via --control-net and --control-image
                    // For first ControlNet, use standard parameters
                    if (i == 0)
                    {
                        parameters["control_net"] = cnModel.RawFilePath;
                        parameters["control_image"] = controlImagePath;

                        // Control strength
                        if (input.TryGet(cn.Strength, out double strength))
                        {
                            parameters["control_strength"] = strength;
                        }

                        Logs.Info($"[SDcpp] ControlNet enabled: {cnModel.Name}");
                        Logs.Debug($"[SDcpp] Control image: {controlImagePath}");
                    }
                    else
                    {
                        // SD.cpp may not support multiple ControlNets simultaneously via CLI
                        Logs.Warning($"[SDcpp] Multiple ControlNets are not fully supported. Only the first ControlNet will be used.");
                        break;
                    }
                }
            }
        }

        // Advanced guidance parameters
        // Flux guidance scale (for Flux models with guidance input)
        if (isFluxModel && input.TryGet(T2IParamTypes.FluxGuidanceScale, out double fluxGuidance))
        {
            parameters["guidance"] = fluxGuidance;
            Logs.Debug($"[SDcpp] Flux guidance scale: {fluxGuidance}");
        }

        // TAESD preview decoder (Tiny AutoEncoder for fast decoding)
        if (SDcppExtension.TAESDParam != null && input.TryGet(SDcppExtension.TAESDParam, out T2IModel taesdModel) && taesdModel != null && taesdModel.Name != "(None)")
        {
            parameters["taesd"] = taesdModel.RawFilePath;
            Logs.Info($"[SDcpp] Using TAESD preview decoder: {taesdModel.Name}");
        }

        // ESRGAN upscaling
        if (SDcppExtension.UpscaleModelParam != null && input.TryGet(SDcppExtension.UpscaleModelParam, out T2IModel upscaleModel) && upscaleModel != null && upscaleModel.Name != "(None)")
        {
            parameters["upscale_model"] = upscaleModel.RawFilePath;
            Logs.Info($"[SDcpp] Using ESRGAN upscale model: {upscaleModel.Name}");

            // Upscale repeats (only relevant if upscale model is set)
            if (SDcppExtension.UpscaleRepeatsParam != null && input.TryGet(SDcppExtension.UpscaleRepeatsParam, out int upscaleRepeats) && upscaleRepeats > 1)
            {
                parameters["upscale_repeats"] = upscaleRepeats;
                Logs.Debug($"[SDcpp] Upscale repeats: {upscaleRepeats}");
            }
        }

        // Color projection for init image consistency
        if (SDcppExtension.ColorProjectionParam != null && input.TryGet(SDcppExtension.ColorProjectionParam, out bool colorProjection) && colorProjection)
        {
            parameters["color"] = true;
            Logs.Debug("[SDcpp] Color projection enabled");
        }

        return parameters;
    }

    /// <summary>
    /// Scans the output directory for generated images and converts them to SwarmUI Image objects.
    /// Handles multiple image formats and applies metadata from the generation parameters.
    /// </summary>
    /// <param name="outputDir">Directory containing generated image files</param>
    /// <param name="input">Original generation parameters for metadata</param>
    /// <returns>Array of Image objects ready for SwarmUI display and processing</returns>
    public static async Task<Image[]> CollectGeneratedImages(string outputDir, T2IParamInput input)
    {
        List<Image> images = [];

        // Look for generated images
        string[] imageFiles = [.. Directory.GetFiles(outputDir, "*.png")
, .. Directory.GetFiles(outputDir, "*.jpg"), .. Directory.GetFiles(outputDir, "*.jpeg")];

        foreach (string imagePath in imageFiles)
        {
            try
            {
                byte[] imageData = await File.ReadAllBytesAsync(imagePath);
                // Determine media type from file extension
                string ext = Path.GetExtension(imagePath).ToLowerInvariant();
                MediaType mediaType = ext switch
                {
                    ".png" => MediaType.ImagePng,
                    ".jpg" or ".jpeg" => MediaType.ImageJpg,
                    _ => MediaType.ImagePng
                };
                Image image = new(imageData, mediaType);
                images.Add(image);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[SDcpp] Failed to load generated image {imagePath}: {ex.Message}");
            }
        }

        if (images.Count == 0)
        {
            throw new Exception("No images were generated by SD.cpp");
        }

        return [.. images];
    }

    /// <summary>
    /// Validates if this backend can handle the given generation request.
    /// Rejects requests that use features SD.cpp doesn't support.
    /// </summary>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        // SD.cpp doesn't currently support ControlNet
        foreach (var controlnet in T2IParamTypes.Controlnets)
        {
            if (input.TryGet(controlnet.Model, out T2IModel controlnetModel) && controlnetModel != null && controlnetModel.Name != "(none)")
            {
                Logs.Verbose($"[SDcpp] Rejecting request: ControlNet not supported by SD.cpp backend");
                return false;
            }
        }

        // SD.cpp doesn't currently support refiner models
        if (input.TryGet(T2IParamTypes.RefinerMethod, out string refinerMethod) && !string.IsNullOrEmpty(refinerMethod) && refinerMethod != "none")
        {
            Logs.Verbose($"[SDcpp] Rejecting request: Refiner not supported by SD.cpp backend");
            return false;
        }

        // Accept all other requests
        return true;
    }

    /// <summary>Free memory</summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        try
        {
            // SD.cpp doesn't maintain persistent memory, so nothing to free
            Logs.Debug("[SDcpp] Memory free requested (no action needed)");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error during memory free: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Performs cleanup when the backend is being shut down during SwarmUI exit.
    /// Terminates any running SD.cpp processes and disposes of resources to prevent memory leaks.
    /// Called by SwarmUI when the application is shutting down or the backend is being disabled.
    /// </summary>
    public override async Task Shutdown()
    {
        try
        {
            Logs.Info("[SDcpp] Shutting down backend");
            
            ProcessManager?.Dispose();
            ProcessManager = null;
            
            Status = BackendStatus.DISABLED;
            Logs.Info("[SDcpp] Backend shutdown complete");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error during shutdown: {ex.Message}");
        }
    }

    /// <summary>Get the settings file path</summary>
    public string SettingsFile => Path.Combine(Program.DataDir, "Settings", "SDcppBackend.fds");
}
