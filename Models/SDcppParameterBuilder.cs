using Hartsy.Extensions.SDcppExtension.SwarmBackends;
using Hartsy.Extensions.SDcppExtension.Utils;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hartsy.Extensions.SDcppExtension.Models;

/// <summary>Converts SwarmUI generation parameters into SD.cpp CLI arguments format. Handles model paths, encoders, VAE, and all generation settings.</summary>
public class SDcppParameterBuilder(string modelName, string architecture)
{
    /// <summary>VRAM policy mode: "auto", "offload", or "disabled".</summary>
    public string VramPolicyMode { get; set; } = "auto";

    /// <summary>GPU index to use for VRAM calculations.</summary>
    public int GpuIndex { get; set; } = 0;

    /// <summary>Builds complete SD.cpp parameters dictionary from SwarmUI input.</summary>
    public Dictionary<string, object> BuildParameters(T2IParamInput input, string outputDir)
    {
        Dictionary<string, object> parameters = [];

        // Use SDcppModelManager helpers for architecture detection
        bool isFluxBased = SDcppModelManager.IsFluxBased(architecture);
        bool isSD3Model = architecture is "sd3" or "sd3.5";
        bool isZImageModel = architecture is "z-image";
        bool isQwenImageModel = architecture is "qwen-image" or "qwen-image-edit";
        bool isVideoModel = SDcppModelManager.IsVideoArchitecture(architecture);
        bool isImageEditModel = SDcppModelManager.IsImageEditArchitecture(architecture);
        bool requiresQwenLLM = SDcppModelManager.RequiresQwenLLM(architecture);
        bool isDistilled = SDcppModelManager.IsDistilledModel(architecture);

        // Add performance and processing parameters
        AddPerformanceParameters(parameters, input);

        // Add basic generation parameters
        AddBasicParameters(parameters, input, isFluxBased, isDistilled);

        // Add model and components based on architecture
        AddModelComponents(parameters, input, isFluxBased, isSD3Model, isZImageModel, isQwenImageModel, requiresQwenLLM);

        // Add image edit parameters if applicable
        if (isImageEditModel)
        {
            AddImageEditParameters(parameters, input, outputDir);
        }

        // Add image parameters (init image, mask, controlnet)
        AddImageParameters(parameters, input, outputDir);

        // Add advanced parameters (TAESD, upscaling, color)
        AddAdvancedParameters(parameters, input);

        // Add video parameters if applicable
        if (isVideoModel)
        {
            AddVideoParameters(parameters, input);
        }

        // Apply VRAM policy based on mode
        ApplyVramPolicy(parameters, input);

        // Set output path (video uses different format)
        if (isVideoModel)
        {
            parameters["output"] = Path.Combine(outputDir, "generated_%03d.mp4");
        }
        else
        {
            parameters["output"] = Path.Combine(outputDir, "generated_%03d.png");
        }

        return parameters;
    }

    /// <summary>Applies dynamic VRAM policy based on actual model sizes and available GPU VRAM.
    /// Behavior depends on VramPolicyMode: "auto" runs smart detection, "offload" forces all flags,
    /// "disabled" skips automatic policy entirely.</summary>
    public void ApplyVramPolicy(Dictionary<string, object> parameters, T2IParamInput input)
    {
        string mode = (VramPolicyMode ?? "auto").ToLowerInvariant();

        // Disabled mode: skip all automatic VRAM policy, let user control via manual parameters
        if (mode == "disabled")
        {
            Logs.Debug("[SDcpp VRAM] Policy disabled - skipping automatic VRAM optimization");
            return;
        }

        // Offload mode: force all memory-saving flags on (ignore smart detection)
        if (mode == "offload")
        {
            Logs.Debug("[SDcpp VRAM] Policy set to 'Always Offload' - enabling all memory-saving flags");
            parameters["vae_tiling"] = true;
            parameters["clip_on_cpu"] = true;
            parameters["vae_on_cpu"] = true;
            parameters["offload_to_cpu"] = true;
            Logs.Info("[SDcpp VRAM] Applied flags: vae_tiling, clip_on_cpu, vae_on_cpu, offload_to_cpu (forced by policy)");
            return;
        }

        // Auto mode: use smart VRAM policy based on actual model sizes
        // Extract model paths from parameters
        string diffusionPath = null;
        Dictionary<string, string> encoderPaths = [];
        string vaePath = null;
        string controlNetPath = null;

        if (parameters.TryGetValue("diffusion_model", out object diffObj))
        {
            diffusionPath = diffObj?.ToString();
        }
        else if (parameters.TryGetValue("model", out object modelObj))
        {
            diffusionPath = modelObj?.ToString();
        }

        // Build encoder paths dictionary with proper keys for VRAM savings calculation
        if (parameters.TryGetValue("clip_l", out object clipL) && !string.IsNullOrEmpty(clipL?.ToString()))
        {
            encoderPaths["clip_l"] = clipL.ToString();
        }
        if (parameters.TryGetValue("clip_g", out object clipG) && !string.IsNullOrEmpty(clipG?.ToString()))
        {
            encoderPaths["clip_g"] = clipG.ToString();
        }
        if (parameters.TryGetValue("t5xxl", out object t5) && !string.IsNullOrEmpty(t5?.ToString()))
        {
            encoderPaths["t5xxl"] = t5.ToString();
        }
        if (parameters.TryGetValue("llm", out object llm) && !string.IsNullOrEmpty(llm?.ToString()))
        {
            encoderPaths["llm"] = llm.ToString();
        }

        if (parameters.TryGetValue("vae", out object vaeObj))
        {
            vaePath = vaeObj?.ToString();
        }

        if (parameters.TryGetValue("control_net", out object cnObj))
        {
            controlNetPath = cnObj?.ToString();
        }

        // Evaluate VRAM policy with detailed component information
        SDcppVramPolicy.PolicyResult policy = SDcppVramPolicy.Evaluate(
            input,
            diffusionPath,
            encoderPaths,
            vaePath,
            controlNetPath,
            GpuIndex
        );

        // Apply policy to parameters (respects user overrides if they're more aggressive)
        SDcppVramPolicy.ApplyToParameters(parameters, policy, respectUserOverrides: true);
    }

    public void AddPerformanceParameters(Dictionary<string, object> parameters, T2IParamInput input)
    {
        // Memory mapping for faster model loading (not VRAM-related)
        if (SDcppExtension.MemoryMapParam is not null)
        {
            parameters["mmap"] = input.Get(SDcppExtension.MemoryMapParam, true, autoFixDefault: true);
        }

        // VAE direct convolution for faster decoding
        if (SDcppExtension.VAEConvDirectParam is not null)
        {
            parameters["vae_conv_direct"] = input.Get(SDcppExtension.VAEConvDirectParam, true, autoFixDefault: true);
        }

        // Store user's explicit VRAM preferences (VRAM policy will respect these if more aggressive)
        if (SDcppExtension.VAETilingParam is not null && input.TryGet(SDcppExtension.VAETilingParam, out bool userVaeTiling))
        {
            parameters["vae_tiling"] = userVaeTiling;
        }
        if (SDcppExtension.CLIPOnCPUParam is not null && input.TryGet(SDcppExtension.CLIPOnCPUParam, out bool userClipOnCpu))
        {
            parameters["clip_on_cpu"] = userClipOnCpu;
        }
        if (SDcppExtension.VAEOnCPUParam is not null && input.TryGet(SDcppExtension.VAEOnCPUParam, out bool userVaeOnCpu))
        {
            parameters["vae_on_cpu"] = userVaeOnCpu;
        }

        // Architecture-specific caching for performance
        bool isFluxBased = SDcppModelManager.IsFluxBased(architecture);
        bool isSD3 = architecture is "sd3" or "sd3.5";
        bool isQwenImage = architecture is "qwen-image" or "qwen-image-edit";
        bool isDiT = isFluxBased || isSD3 || architecture is "z-image" || isQwenImage || SDcppModelManager.IsVideoArchitecture(architecture);
        bool isUNet = !isDiT;
        string sampler = input.Get(SDcppExtension.SamplerParam, isFluxBased ? "euler" : "euler_a", autoFixDefault: true);

        if (isUNet)
        {
            parameters["cache_mode"] = "ucache";
            parameters["cache_option"] = sampler == "euler_a" ? "reset=0" : "reset=1";
        }
        else if (isDiT)
        {
            parameters["cache_mode"] = "cache-dit";
            parameters["cache_preset"] = "ultra";
        }

        // Performance optimizations
        parameters["flash_attention"] = true;
        parameters["diffusion_conv_direct"] = true;

        // Note: VRAM offload flags (vae_tiling, clip_on_cpu, vae_on_cpu, offload_to_cpu)
        // are now handled dynamically by ApplyVramPolicy() which runs after model paths are known.
    }

    public void AddBasicParameters(Dictionary<string, object> parameters, T2IParamInput input, bool isFluxBased, bool isDistilled)
    {
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
            // Flux-based models don't use negative prompts effectively
            if (!isFluxBased)
            {
                parameters["negative_prompt"] = negPrompt;
            }
            else if (!string.IsNullOrWhiteSpace(negPrompt))
            {
                Logs.Debug("[SDcpp] Flux-based models do not benefit from negative prompts, ignoring");
            }
        }
        if (input.TryGet(T2IParamTypes.Width, out int width)) parameters["width"] = width;
        if (input.TryGet(T2IParamTypes.Height, out int height)) parameters["height"] = height;

        // Handle steps with architecture-aware defaults
        int recommendedMinSteps = SDcppModelManager.GetRecommendedMinSteps(architecture);
        if (input.TryGet(T2IParamTypes.Steps, out int steps))
        {
            if (steps <= 0)
            {
                steps = recommendedMinSteps;
                Logs.Info($"[SDcpp] Using default steps for {architecture}: {steps}");
            }
            else if (steps < recommendedMinSteps && isDistilled)
            {
                Logs.Debug($"[SDcpp] Using {steps} steps (distilled model minimum: {recommendedMinSteps})");
            }
            parameters["steps"] = steps;
        }

        // Handle CFG scale with architecture-aware recommendations
        double recommendedCFG = SDcppModelManager.GetRecommendedCFG(architecture);
        if (input.TryGet(T2IParamTypes.CFGScale, out double cfgScale))
        {
            if (isFluxBased && Math.Abs(cfgScale - 1.0) > 0.5)
            {
                Logs.Warning($"[SDcpp] {architecture} works best with CFG scale ~1.0 (current: {cfgScale})");
            }
            parameters["cfg_scale"] = cfgScale;
        }

        if (input.TryGet(T2IParamTypes.Seed, out long seed)) parameters["seed"] = seed;

        // Handle sampler selection
        string defaultSampler = isFluxBased ? "euler" : "euler_a";
        string sampler = defaultSampler;
        if (SDcppExtension.SamplerParam is not null && input.TryGet(SDcppExtension.SamplerParam, out string userSampler))
        {
            sampler = userSampler;
        }
        if (isFluxBased && sampler is not "euler")
        {
            Logs.Debug($"[SDcpp] Flux-based models work best with euler sampler (requested: {sampler})");
            sampler = "euler";
        }
        parameters["sampling_method"] = sampler;

        if (SDcppExtension.SchedulerParam is not null && input.TryGet(SDcppExtension.SchedulerParam, out string scheduler) && !string.IsNullOrEmpty(scheduler) && scheduler is not "default")
        {
            parameters["scheduler"] = scheduler;
        }
        if (input.TryGet(T2IParamTypes.ClipStopAtLayer, out int clipStopLayer))
        {
            int clipSkip = Math.Abs(clipStopLayer);
            if (clipSkip > 1)
            {
                parameters["clip_skip"] = clipSkip;
            }
        }
        // NOTE: SwarmUI handles 'Images' and 'Batch Size' at a higher level by issuing multiple backend jobs.
        // This backend should generate 1 image per job to preserve per-image previews and sequential behavior.

        // Flux guidance scale (applies to all Flux-based models)
        if (isFluxBased && input.TryGet(T2IParamTypes.FluxGuidanceScale, out double fluxGuidance))
        {
            parameters["guidance"] = fluxGuidance;
            Logs.Debug($"[SDcpp] Flux guidance scale: {fluxGuidance}");
        }
    }

    public void AddModelComponents(Dictionary<string, object> parameters, T2IParamInput input, 
        bool isFluxBased, bool isSD3Model, bool isZImageModel, bool isQwenImageModel, bool requiresQwenLLM)
    {
        if (string.IsNullOrEmpty(modelName)) return;
        T2IModel mainModel = Program.T2IModelSets["Stable-Diffusion"].Models.Values.FirstOrDefault(m => m.Name == modelName);
        
        // DiT-based models use diffusion_model parameter
        bool isDiTModel = isFluxBased || isSD3Model || isZImageModel || isQwenImageModel;
        
        if (isDiTModel && mainModel is not null)
        {
            parameters["diffusion_model"] = mainModel.RawFilePath;
            
            // Add VAE (Flux-based and Z-Image share the same Flux VAE)
            bool needsFluxVAE = isFluxBased || isZImageModel;
            AddVAE(parameters, input, needsFluxVAE, isZImageModel);
            
            // Add text encoders based on architecture
            if (isSD3Model)
            {
                // SD3 needs CLIP-G, CLIP-L, and T5-XXL
                AddSD3Encoders(parameters, input);
                AddCLIPEncoders(parameters, input);
                AddT5XXL(parameters, input);
            }
            else if (isFluxBased)
            {
                // Flux-based models need CLIP-L and T5-XXL (no CLIP-G)
                AddCLIPEncoders(parameters, input);
                AddT5XXL(parameters, input);
            }
            else if (isZImageModel)
            {
                // Z-Image uses Qwen LLM instead of CLIP/T5
                AddQwenLLM(parameters, input);
            }
            else if (isQwenImageModel)
            {
                // Qwen Image models use Qwen LLM
                AddQwenLLM(parameters, input);
            }
            
            // Some architectures may need additional LLM components
            if (requiresQwenLLM && !parameters.ContainsKey("llm"))
            {
                AddQwenLLM(parameters, input);
            }
            
            ValidateRequiredComponents(parameters, isFluxBased, isSD3Model, isZImageModel, isQwenImageModel);
        }
        else if (mainModel is not null)
        {
            // UNet-based models (SD1.5, SD2, SDXL) use model parameter
            parameters["model"] = mainModel.RawFilePath;
            if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel is not null && vaeModel.Name is not "(none)")
            {
                parameters["vae"] = vaeModel.RawFilePath;
            }
        }
    }

    public void AddVAE(Dictionary<string, object> parameters, T2IParamInput input, bool isFluxModel, bool isZImageModel = false)
    {
        if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel is not null && vaeModel.Name is not "(none)")
        {
            parameters["vae"] = vaeModel.RawFilePath;
            Logs.Debug($"[SDcpp] Using user-specified VAE: {vaeModel.Name}");
            return;
        }
        if (!isFluxModel && !isZImageModel) return;
        T2IModelHandler vaeModelSet = Program.T2IModelSets["VAE"];
        T2IModel defaultVae = vaeModelSet.Models.Values.FirstOrDefault(m => m.Name.Equals("ae.safetensors", StringComparison.OrdinalIgnoreCase) || m.Name.EndsWith("ae.safetensors", StringComparison.OrdinalIgnoreCase) ||
            (m.Name.Contains("flux", StringComparison.OrdinalIgnoreCase) && m.Name.Contains("ae", StringComparison.OrdinalIgnoreCase)));
        if (defaultVae is not null)
        {
            parameters["vae"] = defaultVae.RawFilePath;
            Logs.Debug($"[SDcpp] Using existing VAE: {defaultVae.Name}");
        }
        else
        {
            Logs.Info($"[SDcpp] {(isFluxModel ? "Flux" : "Z-Image")} VAE not found, auto-downloading...");
            string vaePath = SDcppModelManager.EnsureModelExists("VAE", "Flux/ae.safetensors", "https://huggingface.co/mcmonkey/swarm-vaes/resolve/main/flux_ae.safetensors", "afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38");
            if (!string.IsNullOrEmpty(vaePath))
            {
                parameters["vae"] = vaePath;
            }
        }
    }

    public void AddSD3Encoders(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (input.TryGet(T2IParamTypes.ClipGModel, out T2IModel clipGModel) && clipGModel is not null)
        {
            parameters["clip_g"] = clipGModel.RawFilePath;
            Logs.Debug($"[SDcpp] Using user-specified CLIP-G: {clipGModel.Name}");
        }
        else
        {
            T2IModelHandler clipModelSet = Program.T2IModelSets["Clip"];
            T2IModel defaultClipG = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("clip_g"));
            if (defaultClipG is not null)
            {
                parameters["clip_g"] = defaultClipG.RawFilePath;
                Logs.Debug($"[SDcpp] Using existing CLIP-G: {defaultClipG.Name}");
            }
            else
            {
                Logs.Info("[SDcpp] CLIP-G not found, auto-downloading...");
                string clipGPath = SDcppModelManager.EnsureModelExists("Clip", "clip_g.safetensors", "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/text_encoder_2/model.fp16.safetensors",
                    "ec310df2af79c318e24d20511b601a591ca8cd4f1fce1d8dff822a356bcdb1f4");
                if (!string.IsNullOrEmpty(clipGPath))
                {
                    parameters["clip_g"] = clipGPath;
                }
            }
        }
    }

    public void AddCLIPEncoders(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (input.TryGet(T2IParamTypes.ClipLModel, out T2IModel clipLModel) && clipLModel is not null)
        {
            parameters["clip_l"] = clipLModel.RawFilePath;
            Logs.Debug($"[SDcpp] Using user-specified CLIP-L: {clipLModel.Name}");
        }
        else
        {
            T2IModelHandler clipModelSet = Program.T2IModelSets["Clip"];
            T2IModel defaultClipL = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("clip_l"));
            if (defaultClipL is not null)
            {
                parameters["clip_l"] = defaultClipL.RawFilePath;
                Logs.Debug($"[SDcpp] Using existing CLIP-L: {defaultClipL.Name}");
            }
            else
            {
                Logs.Info("[SDcpp] CLIP-L not found, auto-downloading...");
                string clipLPath = SDcppModelManager.EnsureModelExists("Clip", "clip_l.safetensors", "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/text_encoder/model.fp16.safetensors",
                    "660c6f5b1abae9dc498ac2d21e1347d2abdb0cf6c0c0c8576cd796491d9a6cdd");
                if (!string.IsNullOrEmpty(clipLPath))
                {
                    parameters["clip_l"] = clipLPath;
                }
            }
        }
    }

    public void AddT5XXL(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (input.TryGet(T2IParamTypes.T5XXLModel, out T2IModel t5xxlModel) && t5xxlModel is not null)
        {
            parameters["t5xxl"] = t5xxlModel.RawFilePath;
            Logs.Debug($"[SDcpp] Using user-specified T5-XXL: {t5xxlModel.Name}");
        }
        else
        {
            T2IModelHandler clipModelSet = Program.T2IModelSets["Clip"];
            T2IModel defaultT5 = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("t5xxl"));
            if (defaultT5 is not null)
            {
                parameters["t5xxl"] = defaultT5.RawFilePath;
                Logs.Debug($"[SDcpp] Using existing T5-XXL: {defaultT5.Name}");
            }
            else
            {
                Logs.Info("[SDcpp] T5-XXL not found, auto-downloading (FP8 version)...");
                string t5Path = SDcppModelManager.EnsureModelExists("Clip", "t5xxl_fp8_e4m3fn.safetensors", "https://huggingface.co/mcmonkey/google_t5-v1_1-xxl_encoderonly/resolve/main/t5xxl_fp8_e4m3fn.safetensors",
                    "7d330da4816157540d6bb7838bf63a0f02f573fc48ca4d8de34bb0cbfd514f09");
                if (!string.IsNullOrEmpty(t5Path))
                {
                    parameters["t5xxl"] = t5Path;
                }
            }
        }
    }

    public void AddQwenLLM(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (input.TryGet(T2IParamTypes.QwenModel, out T2IModel qwenModel) && qwenModel is not null)
        {
            parameters["llm"] = qwenModel.RawFilePath;
            Logs.Debug($"[SDcpp] Using user-specified Qwen model: {qwenModel.Name}");
        }
        else
        {
            T2IModelHandler clipModelSet = Program.T2IModelSets["Clip"];
            // Z-Image specifically requires Qwen 3 4B - other Qwen models (like qwen_2.5_vl) are NOT compatible
            // Priority order: exact match for qwen_3_4b variants, then download if not found
            T2IModel existingQwen = clipModelSet.Models.Values.FirstOrDefault(m =>
                m.Name.Contains("qwen_3_4b", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("qwen3_4b", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("qwen-3-4b", StringComparison.OrdinalIgnoreCase));

            if (existingQwen is not null)
            {
                parameters["llm"] = existingQwen.RawFilePath;
                Logs.Debug($"[SDcpp] Using existing Qwen 3 4B model: {existingQwen.Name}");
            }
            else
            {
                Logs.Info("[SDcpp] Z-Image requires Qwen 3 4B model (other Qwen versions are not compatible), auto-downloading...");
                string qwenPath = SDcppModelManager.EnsureModelExists("Clip", "qwen_3_4b.safetensors", "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/text_encoders/qwen_3_4b.safetensors",
                    "6c671498573ac2f7a5501502ccce8d2b08ea6ca2f661c458e708f36b36edfc5a");
                if (!string.IsNullOrEmpty(qwenPath))
                {
                    parameters["llm"] = qwenPath;
                }
            }
        }
    }

    public void ValidateRequiredComponents(Dictionary<string, object> parameters,
        bool isFluxBased, bool isSD3Model, bool isZImageModel, bool isQwenImageModel)
    {
        List<string> missing = [];

        if (isFluxBased)
        {
            if (!parameters.ContainsKey("vae")) missing.Add("VAE (ae.safetensors)");
            if (!parameters.ContainsKey("clip_l")) missing.Add("CLIP-L (clip_l.safetensors)");
            if (!parameters.ContainsKey("t5xxl")) missing.Add("T5-XXL (t5xxl_fp16.safetensors or t5xxl_fp8_e4m3fn.safetensors)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"Flux-based models ({architecture}) require additional component files.\n\nMissing components:\n  • {string.Join("\n  • ", missing)}\n\nThese should auto-download. If download failed, manually download and place in:\n• VAE → Models/VAE/\n• CLIP-L, T5-XXL → Models/clip/");
            }
            Logs.Info($"[SDcpp] All {architecture} components found successfully");
        }
        else if (isSD3Model)
        {
            if (!parameters.ContainsKey("clip_g")) missing.Add("CLIP-G (clip_g.safetensors)");
            if (!parameters.ContainsKey("clip_l")) missing.Add("CLIP-L (clip_l.safetensors)");
            if (!parameters.ContainsKey("t5xxl")) missing.Add("T5-XXL (t5xxl_fp16.safetensors)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"SD3 models require additional component files.\n\nMissing components:\n  • {string.Join("\n  • ", missing)}\n\nPlace in: Models/clip/");
            }
            Logs.Info("[SDcpp] All SD3 components found successfully");
        }
        else if (isZImageModel)
        {
            if (!parameters.ContainsKey("vae")) missing.Add("VAE (ae.safetensors)");
            if (!parameters.ContainsKey("llm")) missing.Add("Qwen LLM (qwen_3_4b.safetensors)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"Z-Image models require additional component files.\n\nMissing components:\n  • {string.Join("\n  • ", missing)}\n\nThese should auto-download. Place in:\n• VAE → Models/VAE/\n• Qwen LLM → Models/clip/");
            }
            Logs.Info("[SDcpp] All Z-Image components found successfully");
        }
        else if (isQwenImageModel)
        {
            if (!parameters.ContainsKey("llm")) missing.Add("Qwen LLM (qwen_3_4b.safetensors or similar)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"Qwen Image models require additional component files.\n\nMissing components:\n  • {string.Join("\n  • ", missing)}\n\nThese should auto-download. Place in: Models/clip/");
            }
            Logs.Info("[SDcpp] All Qwen Image components found successfully");
        }
    }

    /// <summary>Adds parameters for image editing models (Flux Kontext, Qwen Image Edit).</summary>
    public void AddImageEditParameters(Dictionary<string, object> parameters, T2IParamInput input, string outputDir)
    {
        // Image edit models require an input image
        if (!input.TryGet(T2IParamTypes.InitImage, out Image editImage) || editImage is null)
        {
            Logs.Warning($"[SDcpp] {architecture} is an image edit model but no input image was provided");
            return;
        }

        // For Flux Kontext, the input image is used as reference
        if (architecture is "flux-kontext")
        {
            string editImagePath = Path.Combine(outputDir, "edit_input.png");
            File.WriteAllBytes(editImagePath, editImage.RawData);
            parameters["init_img"] = editImagePath;
            
            // Kontext uses strength to control how much to preserve from original
            if (input.TryGet(T2IParamTypes.InitImageCreativity, out double strength))
            {
                parameters["strength"] = strength;
            }
            else
            {
                parameters["strength"] = 0.75; // Default for image editing
            }
            Logs.Debug($"[SDcpp] Flux Kontext image edit mode enabled");
        }
        else if (architecture is "qwen-image-edit")
        {
            string editImagePath = Path.Combine(outputDir, "edit_input.png");
            File.WriteAllBytes(editImagePath, editImage.RawData);
            parameters["init_img"] = editImagePath;
            
            if (input.TryGet(T2IParamTypes.InitImageCreativity, out double strength))
            {
                parameters["strength"] = strength;
            }
            else
            {
                parameters["strength"] = 0.8; // Default for Qwen edit
            }
            Logs.Debug($"[SDcpp] Qwen Image Edit mode enabled");
        }
    }

    public void AddImageParameters(Dictionary<string, object> parameters, T2IParamInput input, string outputDir)
    {
        if (input.TryGet(T2IParamTypes.InitImage, out Image initImage))
        {
            string initImagePath = Path.Combine(outputDir, "init.png");
            File.WriteAllBytes(initImagePath, initImage.RawData);
            parameters["init_img"] = initImagePath;
            if (input.TryGet(T2IParamTypes.InitImageCreativity, out double strength)) parameters["strength"] = strength;
        }
        if (input.TryGet(T2IParamTypes.MaskImage, out Image maskImage))
        {
            string maskImagePath = Path.Combine(outputDir, "mask.png");
            File.WriteAllBytes(maskImagePath, maskImage.RawData);
            parameters["mask"] = maskImagePath;
        }
        for (int i = 0; i < T2IParamTypes.Controlnets.Length; i++)
        {
            T2IParamTypes.ControlNetParamHolder cn = T2IParamTypes.Controlnets[i];
            if (input.TryGet(cn.Model, out T2IModel cnModel) && cnModel is not null && cnModel.Name is not "(None)")
            {
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
                if (controlImage is not null)
                {
                    string controlImagePath = Path.Combine(outputDir, $"control{i + 1}.png");
                    File.WriteAllBytes(controlImagePath, controlImage.RawData);
                    if (i is 0)
                    {
                        parameters["control_net"] = cnModel.RawFilePath;
                        parameters["control_image"] = controlImagePath;
                        if (input.TryGet(cn.Strength, out double strength))
                        {
                            parameters["control_strength"] = strength;
                        }
                        Logs.Info($"[SDcpp] ControlNet enabled: {cnModel.Name}");
                    }
                    else
                    {
                        Logs.Warning("[SDcpp] Multiple ControlNets not fully supported. Only first will be used.");
                        break;
                    }
                }
            }
        }
    }

    public void AddAdvancedParameters(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (SDcppExtension.TAESDParam is not null && input.TryGet(SDcppExtension.TAESDParam, out T2IModel taesdModel) && taesdModel is not null && taesdModel.Name is not "(None)")
        {
            parameters["taesd"] = taesdModel.RawFilePath;
            Logs.Info($"[SDcpp] Using TAESD preview decoder: {taesdModel.Name}");
        }
        if (SDcppExtension.UpscaleModelParam is not null && input.TryGet(SDcppExtension.UpscaleModelParam, out T2IModel upscaleModel) && upscaleModel is not null && upscaleModel.Name is not "(None)")
        {
            parameters["upscale_model"] = upscaleModel.RawFilePath;
            Logs.Info($"[SDcpp] Using ESRGAN upscale model: {upscaleModel.Name}");
            if (SDcppExtension.UpscaleRepeatsParam is not null && input.TryGet(SDcppExtension.UpscaleRepeatsParam, out int upscaleRepeats) && upscaleRepeats > 1)
            {
                parameters["upscale_repeats"] = upscaleRepeats;
                Logs.Debug($"[SDcpp] Upscale repeats: {upscaleRepeats}");
            }
        }
        if (SDcppExtension.ColorProjectionParam is not null && input.TryGet(SDcppExtension.ColorProjectionParam, out bool colorProjection) && colorProjection)
        {
            parameters["color"] = true;
            Logs.Debug("[SDcpp] Color projection enabled");
        }
    }

    public void AddVideoParameters(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (input.TryGet(T2IParamTypes.VideoFrames, out int videoFrames) && videoFrames > 0)
        {
            parameters["video_frames"] = videoFrames;
            Logs.Debug($"[SDcpp] Video frames: {videoFrames}");
        }
        if (architecture.Contains("wan"))
        {
            parameters["flow_shift"] = 3.0;
            Logs.Debug("[SDcpp] Flow shift: 3.0 (Wan model default)");
        }
        if (input.TryGet(T2IParamTypes.VideoFPS, out int videoFPS) && videoFPS > 0)
        {
            parameters["video_fps"] = videoFPS;
            Logs.Debug($"[SDcpp] Video FPS: {videoFPS}");
        }
        if (architecture is "wan-2.2")
        {
            if (input.TryGet(T2IParamTypes.VideoSwapModel, out T2IModel swapModel) && swapModel is not null && swapModel.Name is not "(None)")
            {
                parameters["high_noise_diffusion_model"] = swapModel.RawFilePath;
                Logs.Debug($"[SDcpp] Wan 2.2 high-noise model: {swapModel.Name}");
                if (input.TryGet(T2IParamTypes.VideoSwapPercent, out double swapPercent))
                {
                    parameters["video_swap_percent"] = swapPercent;
                    Logs.Debug($"[SDcpp] Video swap percent: {swapPercent}");
                }
            }
        }
    }
}
