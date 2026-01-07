using Hartsy.Extensions.SDcppExtension.SwarmBackends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hartsy.Extensions.SDcppExtension.Models;

/// <summary>Converts SwarmUI generation parameters into SD.cpp CLI arguments format. Handles model paths, encoders, VAE, and all generation settings.</summary>
public class SDcppParameterBuilder
{
    public readonly string CurrentModelName;
    public readonly string CurrentModelArchitecture;

    public SDcppParameterBuilder(string modelName, string architecture)
    {
        CurrentModelName = modelName;
        CurrentModelArchitecture = architecture;
    }

    /// <summary>Builds complete SD.cpp parameters dictionary from SwarmUI input.</summary>
    public Dictionary<string, object> BuildParameters(T2IParamInput input, string outputDir)
    {
        Dictionary<string, object> parameters = [];

        bool isFluxModel = CurrentModelArchitecture is "flux";
        bool isSD3Model = CurrentModelArchitecture is "sd3";
        bool isZImageModel = CurrentModelArchitecture is "z-image";
        bool isVideoModel = CurrentModelArchitecture.Contains("wan") || CurrentModelArchitecture is "video";

        // Add performance and processing parameters
        AddPerformanceParameters(parameters, input);

        // Add basic generation parameters
        AddBasicParameters(parameters, input, isFluxModel);

        // Add model and components
        AddModelComponents(parameters, input, isFluxModel, isSD3Model, isZImageModel);

        // Add image parameters (init image, mask, controlnet)
        AddImageParameters(parameters, input, outputDir);

        // Add advanced parameters (TAESD, upscaling, color)
        AddAdvancedParameters(parameters, input);

        // Add video parameters if applicable
        if (isVideoModel)
        {
            AddVideoParameters(parameters, input);
        }

        // Set output path
        parameters["output"] = Path.Combine(outputDir, "generated_%03d.png");

        return parameters;
    }

    public void AddPerformanceParameters(Dictionary<string, object> parameters, T2IParamInput input)
    {
        if (SDcppExtension.MemoryMapParam is not null) parameters["mmap"] = input.Get(SDcppExtension.MemoryMapParam, true, autoFixDefault: true);
        if (SDcppExtension.VAEConvDirectParam is not null) parameters["vae_conv_direct"] = input.Get(SDcppExtension.VAEConvDirectParam, true, autoFixDefault: true);
        if (SDcppExtension.VAETilingParam is not null) parameters["vae_tiling"] = input.Get(SDcppExtension.VAETilingParam, true, autoFixDefault: true);
        bool isFlux = CurrentModelArchitecture is "flux";
        bool isSD3 = CurrentModelArchitecture is "sd3";
        bool isDiT = isFlux || isSD3 || CurrentModelArchitecture is "z-image" || CurrentModelArchitecture.Contains("wan");
        bool isUNet = !(isDiT);
        int width = input.Get(T2IParamTypes.Width, 512);
        int height = input.Get(T2IParamTypes.Height, 512);
        int batchCount = input.Get(T2IParamTypes.Images, 1);
        bool highRes = (width >= 768 || height >= 768);
        bool veryHighRes = (width >= 1024 || height >= 1024);
        bool heavy = highRes || batchCount > 1;
        bool veryHeavy = veryHighRes || batchCount > 2;
        string sampler = input.Get(SDcppExtension.SamplerParam, isFlux ? "euler" : "euler_a", autoFixDefault: true);
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
        parameters["flash_attention"] = true;
        parameters["diffusion_conv_direct"] = true;

        // TODO: Find a smarter way to auto enable CPU offloading.

        //bool enableOffload = heavy || isFlux || isSD3;
        //parameters["offload_to_cpu"] = enableOffload;
        //bool moveClip = veryHeavy;
        //bool moveVae = veryHeavy && isUNet; // avoid hurting DiT too much
        //if (SDcppExtension.CLIPOnCPUParam is not null) parameters["clip_on_cpu"] = moveClip;
        //if (SDcppExtension.VAEOnCPUParam is not null) parameters["vae_on_cpu"] = moveVae;
    }

    public void AddBasicParameters(Dictionary<string, object> parameters, T2IParamInput input, bool isFluxModel)
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
            parameters["negative_prompt"] = negPrompt;
        }
        if (input.TryGet(T2IParamTypes.Width, out int width)) parameters["width"] = width;
        if (input.TryGet(T2IParamTypes.Height, out int height)) parameters["height"] = height;
        if (input.TryGet(T2IParamTypes.Steps, out int steps))
        {
            if (isFluxModel && steps is 0)
            {
                bool isSchnell = CurrentModelName.ToLowerInvariant().Contains("schnell");
                steps = isSchnell ? 4 : 20;
                Logs.Info($"[SDcpp] Using default Flux steps: {steps}");
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
        if (input.TryGet(T2IParamTypes.Seed, out long seed)) parameters["seed"] = seed;
        string sampler = isFluxModel ? "euler" : "euler_a";
        if (SDcppExtension.SamplerParam is not null && input.TryGet(SDcppExtension.SamplerParam, out string userSampler))
        {
            sampler = userSampler;
        }
        if (isFluxModel && sampler is not "euler")
        {
            Logs.Info($"[SDcpp] Flux works best with euler sampler (requested: {sampler})");
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
        if (input.TryGet(T2IParamTypes.Images, out int batchCount) && batchCount > 1)
        {
            parameters["batch_count"] = batchCount;
        }
        if (isFluxModel && input.TryGet(T2IParamTypes.FluxGuidanceScale, out double fluxGuidance))
        {
            parameters["guidance"] = fluxGuidance;
            Logs.Debug($"[SDcpp] Flux guidance scale: {fluxGuidance}");
        }
    }

    public void AddModelComponents(Dictionary<string, object> parameters, T2IParamInput input, bool isFluxModel, bool isSD3Model, bool isZImageModel)
    {
        if (string.IsNullOrEmpty(CurrentModelName)) return;
        T2IModel mainModel = Program.T2IModelSets["Stable-Diffusion"].Models.Values.FirstOrDefault(m => m.Name == CurrentModelName);
        if ((isFluxModel || isSD3Model || isZImageModel) && mainModel is not null)
        {
            parameters["diffusion_model"] = mainModel.RawFilePath;
            AddVAE(parameters, input, isFluxModel);
            if (isSD3Model)
            {
                AddSD3Encoders(parameters, input);
            }
            else if (isFluxModel || isSD3Model)
            {
                AddCLIPEncoders(parameters, input);
            }
            if (!isZImageModel)
            {
                AddT5XXL(parameters, input);
            }
            if (isZImageModel)
            {
                AddQwenLLM(parameters, input);
            }
            ValidateRequiredComponents(parameters, isFluxModel, isSD3Model, isZImageModel);
        }
        else if (mainModel is not null)
        {
            parameters["model"] = mainModel.RawFilePath;
            if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel is not null && vaeModel.Name is not "(none)")
            {
                parameters["vae"] = vaeModel.RawFilePath;
            }
        }
    }

    public void AddVAE(Dictionary<string, object> parameters, T2IParamInput input, bool isFluxModel)
    {
        if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel is not null && vaeModel.Name is not "(none)")
        {
            parameters["vae"] = vaeModel.RawFilePath;
            Logs.Debug($"[SDcpp] Using user-specified VAE: {vaeModel.Name}");
            return;
        }
        if (!isFluxModel) return;
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
            Logs.Info("[SDcpp] Flux VAE not found, auto-downloading...");
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
            T2IModel existingQwen = clipModelSet.Models.Values.FirstOrDefault(m => m.Name.Contains("qwen_3_4b", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("qwen", StringComparison.OrdinalIgnoreCase));
            if (existingQwen is not null)
            {
                parameters["llm"] = existingQwen.RawFilePath;
                Logs.Debug($"[SDcpp] Using existing Qwen model: {existingQwen.Name}");
            }
            else
            {
                Logs.Info("[SDcpp] Z-Image requires Qwen model, auto-downloading...");
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
        bool isFluxModel, bool isSD3Model, bool isZImageModel)
    {
        List<string> missing = [];

        if (isFluxModel)
        {
            if (!parameters.ContainsKey("vae")) missing.Add("VAE (ae.safetensors)");
            if (!parameters.ContainsKey("clip_l")) missing.Add("CLIP-L (clip_l.safetensors)");
            if (!parameters.ContainsKey("t5xxl")) missing.Add("T5-XXL (t5xxl_fp16.safetensors or t5xxl_fp8_e4m3fn.safetensors)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"Flux models require additional component files that are not installed.\n\nMissing components:\n  • {string.Join("\n  • ",
                    missing)}\n\nThese should auto-download. If download failed, manually download and place in:\n• VAE → Models/VAE/\n• CLIP-L, T5-XXL → Models/clip/");
            }
            Logs.Info("[SDcpp] All Flux components found successfully");
        }
        else if (isSD3Model)
        {
            if (!parameters.ContainsKey("clip_g")) missing.Add("CLIP-G (clip_g.safetensors)");
            if (!parameters.ContainsKey("clip_l")) missing.Add("CLIP-L (clip_l.safetensors)");
            if (!parameters.ContainsKey("t5xxl")) missing.Add("T5-XXL (t5xxl_fp16.safetensors)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"SD3 models require additional component files that are not installed.\n\nMissing components:\n  • {string.Join("\n  • ", missing)}");
            }
            Logs.Info("[SDcpp] All SD3 components found successfully");
        }
        else if (isZImageModel)
        {
            if (!parameters.ContainsKey("vae")) missing.Add("VAE (flux-ae.safetensors)");
            if (!parameters.ContainsKey("llm")) missing.Add("Qwen LLM (qwen_3_4b.safetensors)");
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"Z-Image models require additional component files that are not installed.\n\nMissing components:\n  • {string.Join("\n  • ", missing)}");
            }
            Logs.Info("[SDcpp] All Z-Image components found successfully");
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
        if (CurrentModelArchitecture.Contains("wan"))
        {
            parameters["flow_shift"] = 3.0;
            Logs.Debug("[SDcpp] Flow shift: 3.0 (Wan model default)");
        }
        if (input.TryGet(T2IParamTypes.VideoFPS, out int videoFPS) && videoFPS > 0)
        {
            parameters["video_fps"] = videoFPS;
            Logs.Debug($"[SDcpp] Video FPS: {videoFPS}");
        }
        if (CurrentModelArchitecture is "wan-2.2")
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
