using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.SDcppExtension.Utils;
using SwarmUI.Backends;
using SwarmUI.Core;
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

        [ConfigComment("Model precision type for standard SD models. Lower precision uses less memory but may reduce quality.\nNote: Flux models require GGUF conversion - use FluxQuantization setting instead.")]
        [ManualSettingsOptions(Impl = null, Vals = ["f32", "f16", "q8_0", "q4_0"], ManualNames = ["f32 (Highest Quality)", "f16 (Recommended)", "q8_0 (Lower Memory)", "q4_0 (Lowest Memory)"])]
        public string WeightType = "f16";

        [ConfigComment("Quantization level for Flux models (GGUF format).\n'q8_0' provides best quality with ~12GB VRAM.\n'q4_0' is good for 6-8GB VRAM.\n'q2_k' can run on 4GB VRAM but with quality loss.")]
        [ManualSettingsOptions(Impl = null, Vals = ["q8_0", "q4_0", "q4_k", "q3_k", "q2_k"], ManualNames = ["q8_0 (Best Quality, 12GB VRAM)", "q4_0 (Balanced, 6-8GB VRAM)", "q4_k (Balanced Alt)", "q3_k (Low VRAM, 4-6GB)", "q2_k (Minimal VRAM, 4GB)"])]
        public string FluxQuantization = "q8_0";

        [ConfigComment("Default number of sampling steps for Flux-dev models (20+ recommended for quality).")]
        public int FluxDevSteps = 20;

        [ConfigComment("Default number of sampling steps for Flux-schnell models (4 recommended for speed).")]
        public int FluxSchnellSteps = 4;

        [ConfigComment("Enable VAE tiling to reduce memory usage. Recommended for systems with limited RAM/VRAM.")]
        public bool VAETiling = true;

        [ConfigComment("Keep VAE processing on CPU instead of GPU. Useful if you're running out of VRAM.")]
        public bool VAEOnCPU = false;

        [ConfigComment("Keep CLIP text encoder on CPU instead of GPU. Useful if you're running out of VRAM.")]
        public bool CLIPOnCPU = false;

        [ConfigComment("Enable Flash Attention optimization. May reduce quality slightly but saves memory.")]
        public bool FlashAttention = false;

        [ConfigComment("Maximum time to wait for image generation before timing out (in seconds).")]
        public int ProcessTimeoutSeconds = 600;

        [ConfigComment("Enable debug logging to help troubleshoot issues.")]
        public bool DebugMode = false;

        // Internal settings - not exposed to user
        internal string ExecutablePath = "";
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
                    if (CurrentModelArchitecture == "sdxl-turbo")
                        features.Add("turbo");
                    break;

                case "sd15":
                case "sd15-turbo":
                case "sd2":
                    features.Add("lora");
                    if (CurrentModelArchitecture.Contains("turbo"))
                        features.Add("turbo");
                    break;

                case "lcm":
                    features.Add("lcm");
                    features.Add("lora");
                    break;

                default:
                    // Standard models get LoRA support
                    features.Add("lora");
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
            string updatedExecutablePath = await SDcppDownloadManager.EnsureSDcppAvailable(Settings.ExecutablePath, Settings.Device);
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
                    string cpuExecutablePath = await SDcppDownloadManager.EnsureSDcppAvailable("", "cpu");
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
                    Logs.Warning($"[SDcpp] Forcing CFG scale to 1.0 for optimal results");
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
                    Logs.Info($"[SDcpp] Adjusting steps to 4 for Flux-schnell");
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

        // Basic parameters
        if (input.TryGet(T2IParamTypes.Prompt, out string prompt))
        {
            parameters["prompt"] = prompt;

            // Check for LoRA syntax in prompt
            if (prompt.Contains("<lora:"))
            {
                // Use SwarmUI's configured LoRA directory
                string loraFolder = Program.ServerSettings.Paths.SDLoraFolder.Split(';')[0];
                string loraDir = Utilities.CombinePathWithAbsolute(Program.ServerSettings.Paths.ActualModelRoot, loraFolder);
                if (Directory.Exists(loraDir))
                {
                    parameters["lora_model_dir"] = loraDir;

                    // Warn if using Flux with LoRA and not q8_0
                    if (isFluxModel && Settings.FluxQuantization != "q8_0")
                    {
                        Logs.Warning($"[SDcpp] LoRA with Flux requires q8_0 quantization for reliable results");
                        Logs.Warning($"[SDcpp] Current quantization: {Settings.FluxQuantization}");
                        Logs.Warning($"[SDcpp] LoRA may not work properly or produce unstable output");
                    }
                }
            }
        }

        if (input.TryGet(T2IParamTypes.NegativePrompt, out string negPrompt))
            parameters["negative_prompt"] = negPrompt;

        if (input.TryGet(T2IParamTypes.Width, out int width))
            parameters["width"] = width;

        if (input.TryGet(T2IParamTypes.Height, out int height))
            parameters["height"] = height;

        if (input.TryGet(T2IParamTypes.Steps, out int steps))
        {
            // Apply Flux-specific step defaults if needed
            if (isFluxModel && steps == 0)
            {
                bool isSchnell = CurrentModelName.ToLowerInvariant().Contains("schnell");
                steps = isSchnell ? Settings.FluxSchnellSteps : Settings.FluxDevSteps;
                Logs.Info($"[SDcpp] Using default Flux steps: {steps}");
            }
            parameters["steps"] = steps;
        }

        if (input.TryGet(T2IParamTypes.CFGScale, out double cfgScale))
        {
            // Flux requires CFG scale 1.0
            if (isFluxModel)
            {
                parameters["cfg_scale"] = 1.0;
            }
            else
            {
                parameters["cfg_scale"] = cfgScale;
            }
        }

        if (input.TryGet(T2IParamTypes.Seed, out long seed))
            parameters["seed"] = seed;

        // Map SwarmUI sampler to SD.cpp sampling method
        // Default based on model architecture
        string swarmSampler = isFluxModel ? "euler" : "euler_a";

        // Use registered sampler parameter if available
        if (SDcppExtension.SamplerParam != null && input.TryGet(SDcppExtension.SamplerParam, out string userSampler))
        {
            swarmSampler = userSampler;
        }

        // Flux works best with euler sampler - override if needed
        if (isFluxModel && swarmSampler != "euler")
        {
            Logs.Info($"[SDcpp] Flux works best with euler sampler (requested: {swarmSampler})");
            Logs.Info($"[SDcpp] Using euler sampler for optimal results");
            swarmSampler = "euler";
        }

        parameters["sampling_method"] = swarmSampler;

        // Model path - Flux and SD3 use multi-component architecture with separate encoders
        bool isSD3Model = CurrentModelArchitecture == "sd3";
        bool isMultiComponentModel = isFluxModel || isSD3Model;

        if (!string.IsNullOrEmpty(CurrentModelName))
        {
            T2IModel mainModel = Program.T2IModelSets["Stable-Diffusion"].Models.Values
                .FirstOrDefault(m => m.Name == CurrentModelName);

            if (isMultiComponentModel && mainModel != null)
            {
                // Multi-component parameters - use SwarmUI's parameter system
                parameters["diffusion_model"] = mainModel.RawFilePath;
                Logs.Info($"[SDcpp] Multi-component model detected: {(isFluxModel ? "Flux" : "SD3")}");
                Logs.Info($"[SDcpp] Diffusion model path: {mainModel.RawFilePath}");

                // VAE - Required for Flux, optional for SD3 (has built-in VAE)
                if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel != null && vaeModel.Name != "(none)")
                {
                    parameters["vae"] = vaeModel.RawFilePath;
                    Logs.Debug($"[SDcpp] Using VAE: {vaeModel.Name}");
                }
                else if (isFluxModel)
                {
                    // Flux requires external VAE - try to find default
                    var vaeModelSet = Program.T2IModelSets["VAE"];
                    var defaultVae = vaeModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("ae") && m.Name.EndsWith(".safetensors"));
                    if (defaultVae != null)
                    {
                        parameters["vae"] = defaultVae.RawFilePath;
                        Logs.Debug($"[SDcpp] Using default VAE: {defaultVae.Name}");
                    }
                }
                // SD3 VAE is optional - uses built-in if not specified

                // CLIP-G - Required for SD3, not used by Flux
                if (isSD3Model)
                {
                    if (input.TryGet(T2IParamTypes.ClipGModel, out T2IModel clipGModel) && clipGModel != null)
                    {
                        parameters["clip_g"] = clipGModel.RawFilePath;
                        Logs.Debug($"[SDcpp] Using CLIP-G: {clipGModel.Name}");
                    }
                    else
                    {
                        // Use default CLIP-G from SwarmUI's registry
                        var clipModelSet = Program.T2IModelSets["Clip"];
                        Logs.Debug($"[SDcpp] Searching for CLIP-G in registry with {clipModelSet.Models.Count} CLIP models");
                        var defaultClipG = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("clip_g"));
                        if (defaultClipG != null)
                        {
                            parameters["clip_g"] = defaultClipG.RawFilePath;
                            Logs.Info($"[SDcpp] Using default CLIP-G: {defaultClipG.Name}");
                        }
                        else
                        {
                            Logs.Warning($"[SDcpp] CLIP-G not found in registry! Available models: {string.Join(", ", clipModelSet.Models.Values.Take(5).Select(m => m.Name))}");
                        }
                    }
                }

                // CLIP-L - Required for both Flux and SD3
                if (input.TryGet(T2IParamTypes.ClipLModel, out T2IModel clipLModel) && clipLModel != null)
                {
                    parameters["clip_l"] = clipLModel.RawFilePath;
                    Logs.Debug($"[SDcpp] Using CLIP-L: {clipLModel.Name}");
                }
                else
                {
                    // Use default CLIP-L from SwarmUI's registry
                    var clipModelSet = Program.T2IModelSets["Clip"];
                    var defaultClipL = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("clip_l"));
                    if (defaultClipL != null)
                    {
                        parameters["clip_l"] = defaultClipL.RawFilePath;
                        Logs.Info($"[SDcpp] Using default CLIP-L: {defaultClipL.Name}");
                    }
                    else
                    {
                        Logs.Warning($"[SDcpp] CLIP-L not found in registry!");
                    }
                }

                // T5-XXL - Required for both Flux and SD3
                if (input.TryGet(T2IParamTypes.T5XXLModel, out T2IModel t5xxlModel) && t5xxlModel != null)
                {
                    parameters["t5xxl"] = t5xxlModel.RawFilePath;
                    Logs.Debug($"[SDcpp] Using T5-XXL: {t5xxlModel.Name}");
                }
                else
                {
                    // Use default T5-XXL from SwarmUI's registry
                    var clipModelSet = Program.T2IModelSets["Clip"];
                    var defaultT5 = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("t5xxl"));
                    if (defaultT5 != null)
                    {
                        parameters["t5xxl"] = defaultT5.RawFilePath;
                        Logs.Info($"[SDcpp] Using default T5-XXL: {defaultT5.Name}");
                    }
                    else
                    {
                        Logs.Warning($"[SDcpp] T5-XXL not found in registry!");
                    }
                }

                // Validate required components
                if (isFluxModel)
                {
                    if (!parameters.ContainsKey("vae") || !parameters.ContainsKey("clip_l") || !parameters.ContainsKey("t5xxl"))
                    {
                        Logs.Warning("[SDcpp] Missing Flux components! SwarmUI will auto-download them.");
                        Logs.Warning("[SDcpp] Please ensure VAE/CLIP models are available in Models/VAE and Models/clip folders.");
                    }
                    else
                    {
                        Logs.Info("[SDcpp] All Flux components found successfully");
                    }
                }
                else if (isSD3Model)
                {
                    if (!parameters.ContainsKey("clip_g") || !parameters.ContainsKey("clip_l") || !parameters.ContainsKey("t5xxl"))
                    {
                        Logs.Warning("[SDcpp] Missing SD3 components! SwarmUI will auto-download them.");
                        Logs.Warning("[SDcpp] Please ensure CLIP models are available in Models/clip folder.");
                        Logs.Info($"[SDcpp] Components status: clip_g={parameters.ContainsKey("clip_g")}, clip_l={parameters.ContainsKey("clip_l")}, t5xxl={parameters.ContainsKey("t5xxl")}");
                    }
                    else
                    {
                        Logs.Info("[SDcpp] All SD3 components found successfully");
                    }
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
            File.WriteAllBytes(initImagePath, initImage.ImageData);
            parameters["init_img"] = initImagePath;

            if (input.TryGet(T2IParamTypes.InitImageCreativity, out double strength))
                parameters["strength"] = strength;
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
                Image image = new(imageData, Image.ImageType.IMAGE, "png");
                
                // Metadata will be added later when Image class supports it
                // For now, just create the image without metadata

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
