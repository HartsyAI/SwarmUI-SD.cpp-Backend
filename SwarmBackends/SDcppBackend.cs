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
    /// Currently loaded model architecture type (SD15, SDXL, Flux, etc.)
    /// </summary>
    public string CurrentModelArchitecture { get; set; } = "unknown";

    /// <summary>
    /// Flux model components (for multi-component architecture)
    /// </summary>
    public FluxModelComponents CurrentFluxComponents { get; set; } = null;

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
                    if (Settings.FluxQuantization == "q8_0")
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
            // Populate Stable Diffusion models
            if (Program.T2IModelSets.TryGetValue("Stable-Diffusion", out var sdModelSet))
            {
                Models["Stable-Diffusion"] = sdModelSet.Models.Values
                    .Where(IsSupportedBySDcpp)
                    .Select(m => m.Name)
                    .ToList();

                Logs.Debug($"[SDcpp] Found {Models["Stable-Diffusion"].Count} compatible SD models");
            }

            // Populate LoRA models
            if (Program.T2IModelSets.TryGetValue("Lora", out var loraModelSet))
            {
                Models["Lora"] = loraModelSet.Models.Values
                    .Select(m => m.Name)
                    .ToList();

                Logs.Debug($"[SDcpp] Found {Models["Lora"].Count} LoRA models");
            }

            // Populate VAE models
            if (Program.T2IModelSets.TryGetValue("VAE", out var vaeModelSet))
            {
                Models["VAE"] = vaeModelSet.Models.Values
                    .Where(m => IsSupportedVAE(m))
                    .Select(m => m.Name)
                    .ToList();

                Logs.Debug($"[SDcpp] Found {Models["VAE"].Count} VAE models");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error populating models dictionary: {ex.Message}");
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
    /// For Flux models, also discovers required components and handles GGUF conversion.
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

            Logs.Info($"[SDcpp] Detected architecture: {CurrentModelArchitecture}");

            // Handle Flux-specific loading
            if (CurrentModelArchitecture == "flux")
            {
                return await LoadFluxModel(model);
            }

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
    /// Loads a Flux model, handling GGUF conversion and component discovery.
    /// </summary>
    /// <param name="model">Flux model to load</param>
    /// <returns>True if all components found and model ready, false otherwise</returns>
    private async Task<bool> LoadFluxModel(T2IModel model)
    {
        try
        {
            Logs.Info($"[SDcpp] Loading Flux model: {model.Name}");

            string modelPath = model.RawFilePath;

            // Check if model needs GGUF conversion
            if (!GGUFConverter.IsGGUFFormat(modelPath))
            {
                Logs.Warning($"[SDcpp] Flux model is not in GGUF format: {modelPath}");

                if (Settings.AutoConvertFluxToGGUF)
                {
                    Logs.Info($"[SDcpp] Auto-converting Flux model to GGUF format ({Settings.FluxQuantization})...");
                    Logs.Info($"[SDcpp] This may take 5-15 minutes depending on model size.");

                    var (success, outputPath, errorMessage) = await GGUFConverter.ConvertToGGUF(
                        Settings.ExecutablePath,
                        modelPath,
                        Settings.FluxQuantization,
                        debugMode: Settings.DebugMode
                    );

                    if (!success)
                    {
                        Logs.Error($"[SDcpp] GGUF conversion failed: {errorMessage}");
                        Logs.Error($"[SDcpp] Please convert the model manually or set AutoConvertFluxToGGUF=false");
                        return false;
                    }

                    modelPath = outputPath;
                    Logs.Info($"[SDcpp] Conversion successful! Using: {modelPath}");
                }
                else
                {
                    Logs.Error($"[SDcpp] Flux models require GGUF format. Please convert using:");
                    Logs.Error($"[SDcpp] {Settings.ExecutablePath} -M convert -m \"{modelPath}\" -o \"output.gguf\" --type {Settings.FluxQuantization}");
                    Logs.Error($"[SDcpp] Or enable AutoConvertFluxToGGUF in settings.");
                    return false;
                }
            }
            else
            {
                Logs.Info($"[SDcpp] Model is already in GGUF format");
            }

            // Discover Flux components
            Logs.Info($"[SDcpp] Discovering Flux model components...");
            CurrentFluxComponents = FluxModelComponents.DiscoverComponents(
                model,
                Settings.FluxVAEPath,
                Settings.FluxCLIPLPath,
                Settings.FluxT5XXLPath
            );

            // Log component paths
            CurrentFluxComponents.LogComponentPaths();

            // Validate all components exist
            if (!CurrentFluxComponents.IsComplete)
            {
                Logs.Error($"[SDcpp] Missing required Flux components!");
                Logs.Error(CurrentFluxComponents.GetMissingComponentsMessage());
                return false;
            }

            if (!CurrentFluxComponents.ValidateComponentsExist())
            {
                Logs.Error($"[SDcpp] Component validation failed!");
                return false;
            }

            // Update the diffusion model path to use the potentially converted GGUF version
            CurrentFluxComponents.DiffusionModelPath = modelPath;

            Logs.Info($"[SDcpp] Flux model loaded successfully with all components");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error loading Flux model: {ex.Message}");
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
                // Extract LoRA directory if needed
                string loraDir = Path.Combine(Program.DataDir, "Models", "Lora");
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
        string swarmSampler = "euler"; // Default for Flux, euler_a for SD

        // Check if there's a sampler parameter - use reflection to avoid obsolete warning
        // This is a temporary workaround until we have proper sampler parameter access
        try
        {
            var samplerField = input.GetType().GetProperty("Sampler");
            if (samplerField != null)
            {
                var samplerValue = samplerField.GetValue(input);
                if (samplerValue != null)
                {
                    swarmSampler = samplerValue.ToString();
                }
            }
        }
        catch
        {
            // Use default based on model type
            swarmSampler = isFluxModel ? "euler" : "euler_a";
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
                _ => isFluxModel ? "euler" : "euler_a" // Default based on model
            };

            // Flux works best with euler sampler
            if (isFluxModel && sdcppSampler != "euler")
            {
                Logs.Info($"[SDcpp] Flux works best with euler sampler (requested: {sdcppSampler})");
                Logs.Info($"[SDcpp] Using euler sampler for optimal results");
                sdcppSampler = "euler";
            }

            parameters["sampling_method"] = sdcppSampler;
        }
        else
        {
            parameters["sampling_method"] = isFluxModel ? "euler" : "euler_a";
        }

        // Model path - Flux uses multi-component architecture
        if (!string.IsNullOrEmpty(CurrentModelName))
        {
            if (isFluxModel && CurrentFluxComponents != null)
            {
                // Flux multi-component parameters
                parameters["diffusion_model"] = CurrentFluxComponents.DiffusionModelPath;
                parameters["vae"] = CurrentFluxComponents.VAEPath;
                parameters["clip_l"] = CurrentFluxComponents.CLIPLPath;
                parameters["t5xxl"] = CurrentFluxComponents.T5XXLPath;

                Logs.Debug($"[SDcpp] Using Flux components:");
                Logs.Debug($"  Diffusion: {CurrentFluxComponents.DiffusionModelPath}");
                Logs.Debug($"  VAE: {CurrentFluxComponents.VAEPath}");
                Logs.Debug($"  CLIP-L: {CurrentFluxComponents.CLIPLPath}");
                Logs.Debug($"  T5-XXL: {CurrentFluxComponents.T5XXLPath}");
            }
            else
            {
                // Standard SD model
                T2IModel model = Program.T2IModelSets["Stable-Diffusion"].Models.Values
                    .FirstOrDefault(m => m.Name == CurrentModelName);
                if (model != null)
                {
                    parameters["model"] = model.RawFilePath;
                }
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
