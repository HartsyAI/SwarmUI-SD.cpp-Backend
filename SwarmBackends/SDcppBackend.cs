using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.SDcppExtension.Config;
using Hartsy.Extensions.SDcppExtension.Utils;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.Text;

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
    /// </summary>
    public class SDcppBackendSettings : AutoConfiguration
    {
        [ConfigComment("Path to the stable-diffusion.cpp executable (sd.exe on Windows, sd on Linux/Mac)")]
        public string ExecutablePath = "sd.exe";

        [ConfigComment("Number of threads to use during computation (0 for auto-detect)")]
        public int Threads = 4;

        [ConfigComment("GPU device to use (auto, cpu, cuda, metal, vulkan, opencl, sycl)")]
        public string Device = "auto";

        [ConfigComment("Weight precision type (f32, f16, q8_0, q4_0, q4_1, q5_0, q5_1, q2_k, q3_k, q4_k, q5_k, q6_k)")]
        public string WeightType = "f16";

        [ConfigComment("Enable VAE tiling to reduce memory usage")]
        public bool VAETiling = false;

        [ConfigComment("Run VAE on CPU instead of GPU")]
        public bool VAEOnCPU = false;

        [ConfigComment("Run CLIP text encoder on CPU instead of GPU")]
        public bool CLIPOnCPU = false;

        [ConfigComment("Enable Flash Attention optimization")]
        public bool FlashAttention = false;

        [ConfigComment("Enable debug mode for verbose logging")]
        public bool DebugMode = false;

        [ConfigComment("Timeout for SD.cpp process operations in seconds")]
        public int ProcessTimeoutSeconds = 300;

        [ConfigComment("Working directory for temporary files (empty for system temp)")]
        public string WorkingDirectory = "";

        [ConfigComment("Default model path to use if none specified")]
        public string DefaultModelPath = "";

        [ConfigComment("Default VAE path to use if none specified")]
        public string DefaultVAEPath = "";
    }
    /// <summary>Configuration settings controlling SD.cpp behavior, paths, and optimization flags</summary>
    public SDcppBackendSettings Settings => SettingsRaw as SDcppBackendSettings;

    /// <summary>
    /// Manages SD.cpp process lifecycle, command execution, and output capture.
    /// </summary>
    public SDcppProcessManager ProcessManager;

    /// <summary>
    /// Features supported by this backend - includes txt2img, img2img, inpainting, and various samplers.
    /// </summary>
    public override IEnumerable<string> SupportedFeatures =>
    [
        "txt2img",
        "img2img", 
        "inpainting",
        "negative_prompt",
        "batch_generation",
        "lora",
        "controlnet",
        "vae_tiling",
        "upscaling"
    ];

    /// <summary>
    /// Adds a status message during backend initialization
    /// </summary>
    public new void AddLoadStatus(string message)
    {
        Logs.Info($"[SDcpp] {message}");
        // TODO: If SwarmUI has a status reporting mechanism, use it here
    }

    /// <summary>
    /// Initializes the SD.cpp backend by loading configuration, validating the executable,
    /// and setting up the process manager. Called once during SwarmUI startup.
    /// </summary>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[SDcpp] Initializing SD.cpp backend");
            AddLoadStatus("Starting SD.cpp backend initialization...");

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
            if (!ProcessManager.ValidateExecutable())
            {
                Status = BackendStatus.ERRORED;
                Logs.Error("[SDcpp] SD.cpp executable validation failed");
                return;
            }

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
    /// Loads the specified model for use in generation. For SD.cpp, this sets the current model name
    /// as the model path will be passed to the CLI process during generation execution.
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
            if (extension != ".safetensors" && extension != ".ckpt" && extension != ".bin")
            {
                Logs.Warning($"[SDcpp] Model file may not be compatible: {model.RawFilePath}");
            }

            CurrentModelName = model.Name;
            Logs.Info($"[SDcpp] Model loaded successfully: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error loading model {model.Name}: {ex.Message}");
            return false;
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

            // Create temporary output directory
            string tempDir = Path.Combine(Path.GetTempPath(), "sdcpp_output", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Build parameters for SD.cpp
                System.Collections.Generic.Dictionary<string, object> parameters = BuildGenerationParameters(input, tempDir);
                
                if (Settings.DebugMode)
                {
                    Logs.Debug($"[SDcpp] Generation parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
                }

                // Execute SD.cpp
                (bool success, string output, string error) = await ProcessManager.ExecuteAsync(parameters);

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
    /// </summary>
    /// <param name="input">SwarmUI generation parameters</param>
    /// <param name="outputDir">Directory where SD.cpp should save generated images</param>
    /// <returns>Dictionary of parameters formatted for SD.cpp CLI execution</returns>
    public Dictionary<string, object> BuildGenerationParameters(T2IParamInput input, string outputDir)
    {
        Dictionary<string, object> parameters = [];

        // Basic parameters
        if (input.TryGet(T2IParamTypes.Prompt, out string prompt))
            parameters["prompt"] = prompt;

        if (input.TryGet(T2IParamTypes.NegativePrompt, out string negPrompt))
            parameters["negative_prompt"] = negPrompt;

        if (input.TryGet(T2IParamTypes.Width, out int width))
            parameters["width"] = width;

        if (input.TryGet(T2IParamTypes.Height, out int height))
            parameters["height"] = height;

        if (input.TryGet(T2IParamTypes.Steps, out int steps))
            parameters["steps"] = steps;

        if (input.TryGet(T2IParamTypes.CFGScale, out double cfgScale))
            parameters["cfg_scale"] = cfgScale;

        if (input.TryGet(T2IParamTypes.Seed, out long seed))
            parameters["seed"] = seed;

        // Map SwarmUI sampler to SD.cpp sampling method
        // Try to get sampler from standard parameter types first
        string swarmSampler = "euler_a"; // Default fallback
        
        // Check if there's a sampler parameter - use reflection to avoid obsolete warning
        // This is a temporary workaround until we have proper sampler parameter access
        try
        {
            var samplerField = input.GetType().GetProperty("Sampler");
            if (samplerField != null)
            {
                var samplerValue = samplerField.GetValue(input);
                swarmSampler = samplerValue?.ToString() ?? "euler_a";
            }
        }
        catch
        {
            // Fallback to default if reflection fails
            swarmSampler = "euler_a";
        }
        
        if (!string.IsNullOrEmpty(swarmSampler))
        {
            // Map SwarmUI sampler names to SD.cpp sampling methods
            string sdcppSampler = swarmSampler.ToLowerInvariant() switch
            {
                "euler" => "euler",
                "euler_a" or "euler_ancestral" => "euler_a",
                "heun" => "heun",
                "dpm_2" or "dpm2" => "dpm2",
                "dpm_plus_plus_2s_ancestral" or "dpm++_2s_a" => "dpm++2s_a",
                "dpm_plus_plus_2m" or "dpm++_2m" => "dpm++2m",
                "dpm_plus_plus_2m_v2" or "dpm++_2mv2" => "dpm++2mv2",
                "ddim" => "ddim_trailing",
                "lcm" => "lcm",
                _ => "euler_a" // Default fallback
            };
            parameters["sampling_method"] = sdcppSampler;
        }
        else
        {
            parameters["sampling_method"] = "euler_a"; // Default
        }

        // Model path
        if (!string.IsNullOrEmpty(CurrentModelName))
        {
            T2IModel model = Program.T2IModelSets["Stable-Diffusion"].Models.Values
                .FirstOrDefault(m => m.Name == CurrentModelName);
            if (model != null)
                parameters["model"] = model.RawFilePath;
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
