using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.SDcppExtension.Models;
using Hartsy.Extensions.SDcppExtension.Utils;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Hartsy.Extensions.SDcppExtension.SwarmBackends;

/// <summary>Backend that provides image and video generation through SD.cpp CLI execution. Manages model loading, generation requests, and automatic binary updates.</summary>
public class SDcppBackend : AbstractT2IBackend
{
    #region Settings and Configuration

    /// <summary>Backend configuration settings with automatic download and device selection</summary>
    public class SDcppBackendSettings : AutoConfiguration
    {
        [ConfigComment("Which compute device to use for image generation.\n'CPU' uses your processor (slower but works on any system).\n'GPU (CUDA)' uses NVIDIA graphics cards with CUDA support.\n'GPU (Vulkan)' uses any modern graphics card with Vulkan support.\nNote: Flux models may not work reliably with Vulkan - use CUDA or CPU instead.")]
        [ManualSettingsOptions(Impl = null, Vals = ["cpu", "cuda", "vulkan"], ManualNames = ["CPU (Universal)", "GPU (CUDA - NVIDIA)", "GPU (Vulkan - Any GPU)"])]
        public string Device = "cpu";

        [ConfigComment("CUDA version to use (only applies when Device is set to CUDA).\n'Auto' automatically detects your installed CUDA version and downloads the matching binary.\n'CUDA 11.x' for older NVIDIA drivers (driver 450+).\n'CUDA 12.x' for newer NVIDIA drivers (driver 525+).\nNote: If unsure, leave on Auto - it will select the best version for your system.")]
        [ManualSettingsOptions(Impl = null, Vals = ["auto", "11", "12"], ManualNames = ["Auto (Recommended)", "CUDA 11.x", "CUDA 12.x"])]
        public string CudaVersion = "auto";

        [ConfigComment("Number of CPU threads to use during generation (0 for auto-detect based on your CPU).")]
        public int Threads = 0;

        [ConfigComment("Maximum time to wait for image generation before timing out (in seconds).")]
        public int ProcessTimeoutSeconds = 3600;

        [ConfigComment("Whether SD.cpp should automatically check for updates and download newer versions.")]
        [ManualSettingsOptions(Impl = null, Vals = ["true", "false"], ManualNames = ["Auto-Update", "Don't Update"])]
        public string AutoUpdate = "true";

        [ConfigComment("Enable debug logging to help troubleshoot issues.")]
        public bool DebugMode = false;

        [SettingHidden]
        internal string ExecutablePath = "";

        [SettingHidden]
        internal string WorkingDirectory = "";
    }

    public SDcppBackendSettings Settings => SettingsRaw as SDcppBackendSettings;

    #endregion

    #region Properties

    /// <summary>Manages SD.cpp process lifecycle and execution</summary>
    public SDcppProcessManager ProcessManager;

    /// <summary>Currently loaded model architecture (flux, sdxl, sd15, etc.)</summary>
    public string CurrentModelArchitecture { get; set; } = "unknown";

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedFeatures
    {
        get
        {
            List<string> features = ["sdcpp", "txt2img", "img2img", "inpainting",
                "negative_prompt", "batch_generation", "vae_tiling", "gguf"];

            features.AddRange(SDcppModelManager.GetFeaturesForArchitecture(CurrentModelArchitecture));
            return features;
        }
    }

    public string SettingsFile => Path.Combine(Program.DataDir, "Settings", "SDcppBackend.fds");

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[SDcpp] Initializing SD.cpp backend");
            AddLoadStatus("Starting SD.cpp backend initialization...");

            // Validate and configure device (fallback to CPU if needed)
            ValidateAndConfigureDevice();

            // Ensure SD.cpp binary is available
            AddLoadStatus($"Checking for SD.cpp binary (device: {Settings.Device})...");
            bool autoUpdate = Settings.AutoUpdate?.ToLowerInvariant() == "true";
            string updatedExecutablePath = await SDcppDownloadManager.EnsureSDcppAvailable(
                Settings.ExecutablePath, Settings.Device, Settings.CudaVersion, autoUpdate);

            if (updatedExecutablePath != Settings.ExecutablePath)
            {
                Settings.ExecutablePath = updatedExecutablePath;
                AddLoadStatus($"SD.cpp binary configured: {Path.GetFileName(updatedExecutablePath)}");
            }

            // Initialize process manager
            ProcessManager = new SDcppProcessManager(Settings);

            // Validate runtime environment
            AddLoadStatus("Validating SD.cpp executable and runtime...");
            if (!ProcessManager.ValidateRuntime(out string runtimeError))
            {
                // Try CUDA → CPU fallback if CUDA fails
                if (TryFallbackToCPU(runtimeError))
                {
                    AddLoadStatus("Using CPU device (CUDA runtime not available)");
                }
                else
                {
                    Status = BackendStatus.ERRORED;
                    Logs.Error($"[SDcpp] Runtime validation failed: {runtimeError}");
                    AddLoadStatus("SD.cpp backend disabled: " + runtimeError);
                    return;
                }
            }

            // Populate available models
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

    public void ValidateAndConfigureDevice()
    {
        try
        {
            string configured = (Settings.Device ?? "cpu").ToLowerInvariant();
            NvidiaUtil.NvidiaInfo[] nvidiaInfo = NvidiaUtil.QueryNvidia();

            if (configured is "cuda")
            {
                if (nvidiaInfo is null || nvidiaInfo.Length is 0)
                {
                    Logs.Warning("[SDcpp] CUDA requested but no NVIDIA GPU detected. Falling back to CPU.");
                    AddLoadStatus("No NVIDIA GPU detected. Falling back to CPU device.");
                    Settings.Device = "cpu";
                }
                else
                {
                    Logs.Info($"[SDcpp] Using CUDA on: {nvidiaInfo[0].GPUName} (driver {nvidiaInfo[0].DriverVersion})");
                }
            }
            else if (configured is "cpu" && nvidiaInfo is not null && nvidiaInfo.Length > 0)
            {
                Logs.Warning("[SDcpp] NVIDIA GPU detected but using CPU. Change to CUDA in settings for better performance.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error validating device: {ex.Message}");
        }
    }

    public bool TryFallbackToCPU(string cudaError)
    {
        if (!string.Equals(Settings.Device, "cuda", StringComparison.OrdinalIgnoreCase) ||
            !cudaError.Contains("CUDA"))
        {
            return false;
        }

        Logs.Warning("[SDcpp] CUDA runtime validation failed. Attempting CPU fallback...");
        AddLoadStatus("CUDA runtime not found. Falling back to CPU device...");

        Settings.Device = "cpu";
        bool enableAutoUpdate = Settings.AutoUpdate?.ToLowerInvariant() is "true";

        Task<string> cpuTask = SDcppDownloadManager.EnsureSDcppAvailable("", "cpu", "auto", enableAutoUpdate);
        string cpuExecutablePath = cpuTask.Result;

        if (!string.IsNullOrEmpty(cpuExecutablePath) && File.Exists(cpuExecutablePath))
        {
            Settings.ExecutablePath = cpuExecutablePath;
            ProcessManager = new SDcppProcessManager(Settings);

            if (ProcessManager.ValidateRuntime(out string cpuRuntimeError))
            {
                Logs.Info("[SDcpp] Successfully fell back to CPU device");
                return true;
            }

            Logs.Error($"[SDcpp] CPU fallback also failed: {cpuRuntimeError}");
        }

        return false;
    }

    public new void AddLoadStatus(string message)
    {
        Logs.Info($"[SDcpp] {message}");
    }

    #endregion

    #region Model Management

    public void PopulateModelsDict()
    {
        try
        {
            Models ??= new ConcurrentDictionary<string, List<string>>();

            if (Program.T2IModelSets is null)
            {
                Logs.Debug("[SDcpp] Model sets not yet initialized");
                return;
            }

            if (Program.T2IModelSets.TryGetValue("Stable-Diffusion", out T2IModelHandler sdModelSet))
            {
                Models["Stable-Diffusion"] = [.. sdModelSet.Models.Values
                    .Where(m => m is not null && SDcppModelManager.IsSupportedModel(m))
                    .Select(m => m.Name)];
                Logs.Debug($"[SDcpp] Found {Models["Stable-Diffusion"].Count} compatible models");
            }

            if (Program.T2IModelSets.TryGetValue("Lora", out T2IModelHandler loraModelSet))
            {
                Models["LoRA"] = [.. loraModelSet.Models.Values
                    .Where(m => m is not null)
                    .Select(m => m.Name)];
            }

            if (Program.T2IModelSets.TryGetValue("VAE", out T2IModelHandler vaeModelSet))
            {
                Models["VAE"] = [.. vaeModelSet.Models.Values
                    .Where(m => m is not null && SDcppModelManager.IsSupportedVAE(m))
                    .Select(m => m.Name)];
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error populating models: {ex.Message}");
        }
    }

    public void RefreshModels()
    {
        try
        {
            Logs.Debug("[SDcpp] Refreshing models list");
            PopulateModelsDict();
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error refreshing models: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Info($"[SDcpp] Loading model: {model.Name}");

            if (!File.Exists(model.RawFilePath))
            {
                Logs.Error($"[SDcpp] Model file not found: {model.RawFilePath}");
                return false;
            }

            CurrentModelArchitecture = SDcppModelManager.DetectArchitecture(model);
            CurrentModelName = model.Name;

            Logs.Info($"[SDcpp] Model loaded: {model.Name} (Architecture: {CurrentModelArchitecture})");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error loading model: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Generation

    /// <inheritdoc/>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        try
        {
            if (ProcessManager is null)
                throw new InvalidOperationException("Process manager not initialized");

            Logs.Info("[SDcpp] Starting live generation");
            bool isFluxBased = SDcppModelManager.IsFluxBased(CurrentModelArchitecture);

            if (isFluxBased)
            {
                ValidateFluxParameters(user_input);
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "sdcpp_output", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            Logs.Debug($"[SDcpp] Temp output directory: {tempDir}");

            try
            {
                SDcppParameterBuilder paramBuilder = new(CurrentModelName, CurrentModelArchitecture);
                Dictionary<string, object> parameters = paramBuilder.BuildParameters(user_input, tempDir);
                // Enable TAESD preview support
                string previewPath = Path.Combine(tempDir, "preview.png");
                parameters["enable_preview"] = true;
                parameters["preview_path"] = previewPath;
                Logs.Debug($"[SDcpp] Preview enabled. Path: {previewPath}");

                long startTime = Environment.TickCount64;
                int lastStep = 0;
                DateTime lastPreviewCheck = DateTime.MinValue;

                (bool success, string output, string error) = await ProcessManager.ExecuteWithProgressAsync(
                    parameters, isFluxBased,
                    progress =>
                    {
                        int totalSteps = user_input.Get(T2IParamTypes.Steps, 20);
                        int currentStep = (int)(progress * totalSteps);

                        if (currentStep > lastStep || DateTime.Now - lastPreviewCheck > TimeSpan.FromMilliseconds(500))
                        {
                            lastStep = currentStep;
                            lastPreviewCheck = DateTime.Now;

                            Logs.Debug($"[SDcpp] Progress update: step={currentStep}/{totalSteps}, progress={progress:0.000}");
                            SendProgressUpdate(batchId, user_input, takeOutput, previewPath, progress);
                            Logs.Debug($"[SDcpp] Preview file exists: {File.Exists(previewPath)}");
                            Logs.Debug($"[SDcpp] Preview file size: {new FileInfo(previewPath).Length} bytes");
                        }
                    });

                long genTime = Environment.TickCount64 - startTime;

                if (!success)
                {
                    Logs.Error($"[SDcpp] Generation failed: {error}");
                    throw new Exception($"SD.cpp generation failed: {error}");
                }

                Image[] images = await CollectGeneratedImages(tempDir, user_input);
                Logs.Info($"[SDcpp] Generated {images.Length} images in {genTime}ms");

                foreach (Image img in images)
                {
                    takeOutput(img);
                }
            }
            finally
            {
                CleanupTempDirectory(tempDir);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Generation error: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task<Image[]> Generate(T2IParamInput input)
    {
        try
        {
            if (ProcessManager is null) throw new InvalidOperationException("Process manager not initialized");
            bool isFluxBased = SDcppModelManager.IsFluxBased(CurrentModelArchitecture);
            if (isFluxBased)
            {
                ValidateFluxParameters(input);
            }
            string tempDir = Path.Combine(Path.GetTempPath(), "sdcpp_output", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            try
            {
                SDcppParameterBuilder paramBuilder = new(CurrentModelName, CurrentModelArchitecture);
                Dictionary<string, object> parameters = paramBuilder.BuildParameters(input, tempDir);
                (bool success, string output, string error) = await ProcessManager.ExecuteAsync(parameters, isFluxBased);
                if (!success)
                {
                    Logs.Error($"[SDcpp] Generation failed: {error}");
                    throw new Exception($"SD.cpp generation failed: {error}");
                }
                Image[] images = await CollectGeneratedImages(tempDir, input);
                Logs.Info($"[SDcpp] Generated {images.Length} images successfully");
                return images;
            }
            finally
            {
                CleanupTempDirectory(tempDir);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Generation error: {ex.Message}");
            throw;
        }
    }

    public void SendProgressUpdate(string batchId, T2IParamInput user_input, Action<object> takeOutput,
        string previewPath, float progress)
    {
        if (File.Exists(previewPath))
        {
            try
            {
                FileInfo previewInfo = new(previewPath);
                Logs.Debug($"[SDcpp] Preview file found. Path={previewPath}, Size={previewInfo.Length} bytes, LastWrite={previewInfo.LastWriteTimeUtc:O}");
                byte[] previewBytes = File.ReadAllBytes(previewPath);
                string previewBase64 = Convert.ToBase64String(previewBytes);
                Logs.Debug($"[SDcpp] Preview bytes read: {previewBytes.Length} bytes (progress={progress:0.000})");

                takeOutput(new Newtonsoft.Json.Linq.JObject
                {
                    ["batch_index"] = batchId,
                    ["request_id"] = user_input.UserRequestId.ToString(),
                    ["preview"] = $"data:image/png;base64,{previewBase64}",
                    ["overall_percent"] = progress,
                    ["current_percent"] = progress
                });
            }
            catch (IOException)
            {
                Logs.Debug($"[SDcpp] Preview file locked or unreadable: {previewPath}");
                // File locked, skip this update
            }
        }
        else
        {
            Logs.Debug($"[SDcpp] Preview file missing at progress {progress:0.000}: {previewPath}");
            // Send progress without preview
            takeOutput(new Newtonsoft.Json.Linq.JObject
            {
                ["batch_index"] = batchId,
                ["request_id"] = user_input.UserRequestId.ToString(),
                ["overall_percent"] = progress,
                ["current_percent"] = progress
            });
        }
    }

    public static async Task<Image[]> CollectGeneratedImages(string outputDir, T2IParamInput input)
    {
        List<Image> images = [];
        string[] imageFiles = [.. Directory.GetFiles(outputDir, "*.png"),
            .. Directory.GetFiles(outputDir, "*.jpg"),
            .. Directory.GetFiles(outputDir, "*.jpeg")];

        foreach (string imagePath in imageFiles)
        {
            try
            {
                byte[] imageData = await File.ReadAllBytesAsync(imagePath);
                string ext = Path.GetExtension(imagePath).ToLowerInvariant();
                MediaType mediaType = ext switch
                {
                    ".png" => MediaType.ImagePng,
                    ".jpg" or ".jpeg" => MediaType.ImageJpg,
                    _ => MediaType.ImagePng
                };
                images.Add(new Image(imageData, mediaType));
            }
            catch (Exception ex)
            {
                Logs.Warning($"[SDcpp] Failed to load image {imagePath}: {ex.Message}");
            }
        }

        if (images.Count is 0)
        {
            throw new Exception("No images were generated by SD.cpp");
        }

        return [.. images];
    }

    public static void CleanupTempDirectory(string tempDir)
    {
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

    #endregion

    #region Validation

    public void ValidateFluxParameters(T2IParamInput input)
    {
        try
        {
            // Get recommended values for this architecture
            double recommendedCFG = SDcppModelManager.GetRecommendedCFG(CurrentModelArchitecture);
            int recommendedMinSteps = SDcppModelManager.GetRecommendedMinSteps(CurrentModelArchitecture);
            bool isDistilled = SDcppModelManager.IsDistilledModel(CurrentModelArchitecture);

            if (input.TryGet(T2IParamTypes.CFGScale, out double cfgScale) && Math.Abs(cfgScale - recommendedCFG) > 0.5)
            {
                Logs.Warning($"[SDcpp] {CurrentModelArchitecture} works best with CFG scale ~{recommendedCFG} (current: {cfgScale})");
            }

            if (input.TryGet(T2IParamTypes.NegativePrompt, out string negPrompt) && !string.IsNullOrWhiteSpace(negPrompt))
            {
                Logs.Debug($"[SDcpp] {CurrentModelArchitecture} does not benefit from negative prompts");
            }

            if (input.TryGet(T2IParamTypes.Steps, out int steps) && steps < recommendedMinSteps)
            {
                if (isDistilled)
                {
                    Logs.Debug($"[SDcpp] {CurrentModelArchitecture} is distilled, {steps} steps should work fine");
                }
                else
                {
                    Logs.Warning($"[SDcpp] {CurrentModelArchitecture} works best with {recommendedMinSteps}+ steps (current: {steps})");
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error during parameter validation: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        // Reject requests with unsupported features
        foreach (T2IParamTypes.ControlNetParamHolder controlnet in T2IParamTypes.Controlnets)
        {
            if (input.TryGet(controlnet.Model, out T2IModel controlnetModel) &&
                controlnetModel is not null && controlnetModel.Name is not "(none)")
            {
                Logs.Verbose("[SDcpp] Rejecting request: ControlNet not currently supported");
                return false;
            }
        }

        if (input.TryGet(T2IParamTypes.RefinerMethod, out string refinerMethod) &&
            !string.IsNullOrEmpty(refinerMethod) && refinerMethod is not "none")
        {
            Logs.Verbose("[SDcpp] Rejecting request: Refiner not supported");
            return false;
        }

        return true;
    }

    #endregion

    #region Shutdown

    /// <inheritdoc/>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        Logs.Debug("[SDcpp] Memory free requested (no action needed)");
        return true;
    }

    /// <inheritdoc/>
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

    #endregion
}
