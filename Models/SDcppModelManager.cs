using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hartsy.Extensions.SDcppExtension.Models;

/// <summary>Manages SD.cpp model detection, validation, and automatic downloading of required components. Handles architecture-specific requirements for Flux, SD3, Z-Image, and other model types.</summary>
public static class SDcppModelManager
{
    /// <summary>Lock object for model downloads to prevent duplicate downloads</summary>
    public static readonly FreneticUtilities.FreneticToolkit.LockObject ModelDownloadLock = new();

    /// <summary>Determines if a model is supported by SD.cpp based on file format and architecture.</summary>
    public static bool IsSupportedModel(T2IModel model)
    {
        if (model is null) return false;
        string ext = Path.GetExtension(model.RawFilePath).ToLowerInvariant();
        return ext is ".safetensors" or ".gguf" or ".ckpt" or ".bin" or ".sft";
    }

    /// <summary>Determines if a VAE model is supported by SD.cpp.</summary>
    public static bool IsSupportedVAE(T2IModel model)
    {
        if (model is null) return false;
        string ext = Path.GetExtension(model.RawFilePath).ToLowerInvariant();
        return ext is ".safetensors" or ".sft" or ".ckpt" or ".bin";
    }

    /// <summary>Detects the model architecture type based on filename, model class, and metadata.
    /// Supports: Flux (dev/schnell/kontext), Flux.2, SD3/SD3.5, SDXL, SD1.5, SD2, Chroma, Qwen Image, Z-Image, Ovis, Wan 2.1/2.2, and more.</summary>
    public static string DetectArchitecture(T2IModel model)
    {
        if (model is null) return "unknown";
        string filename = Path.GetFileNameWithoutExtension(model.RawFilePath).ToLowerInvariant();
        string modelName = model.Name.ToLowerInvariant();
        string modelClass = model.ModelClass?.ID?.ToLowerInvariant() ?? "";

        // Chroma models (check before Flux since Chroma is Flux-based)
        if (modelClass.Contains("chroma-radiance") || filename.Contains("chroma-radiance") || filename.Contains("chroma_radiance") || modelName.Contains("chroma-radiance"))
            return "chroma-radiance";
        if (modelClass.Contains("chroma") || filename.Contains("chroma") || modelName.Contains("chroma"))
            return "chroma";

        // Ovis models (Flux-based multimodal)
        if (modelClass.Contains("ovis") || filename.Contains("ovis") || modelName.Contains("ovis"))
            return "ovis";

        // Qwen Image models (check edit variants first)
        if (modelClass.Contains("qwen-image-edit") || filename.Contains("qwen-image-edit") || filename.Contains("qwen_image_edit") || 
            modelName.Contains("qwen-image-edit") || modelName.Contains("qwen_image_edit") ||
            filename.Contains("qwen2.5-vl-edit") || filename.Contains("qwen2_5-vl-edit"))
            return "qwen-image-edit";
        if (modelClass.Contains("qwen-image") || filename.Contains("qwen-image") || filename.Contains("qwen_image") || 
            modelName.Contains("qwen-image") || modelName.Contains("qwen_image") ||
            filename.Contains("qwen2.5-vl") || filename.Contains("qwen2_5-vl"))
            return "qwen-image";

        // Z-Image models
        if (filename.Contains("z_image") || filename.Contains("z-image") || modelName.Contains("z_image") || 
            modelName.Contains("z-image") || modelClass.Contains("z-image"))
            return "z-image";

        // Flux models (various variants)
        if (modelClass.Contains("flux") || filename.Contains("flux") || modelName.Contains("flux"))
        {
            // Flux Kontext (image editing)
            if (filename.Contains("kontext") || modelName.Contains("kontext") || modelClass.Contains("kontext"))
                return "flux-kontext";
            // Flux.2 Dev
            if (filename.Contains("flux.2") || filename.Contains("flux2") || modelName.Contains("flux.2") || 
                modelName.Contains("flux2") || modelClass.Contains("flux.2"))
                return "flux2-dev";
            // Flux Schnell
            if (filename.Contains("schnell") || modelName.Contains("schnell") || modelClass.Contains("schnell"))
                return "flux-schnell";
            // Flux Dev (default Flux)
            return "flux-dev";
        }

        // SD3/SD3.5 models
        if (modelClass.Contains("sd3") || filename.Contains("sd3") || modelName.Contains("sd3"))
        {
            if (filename.Contains("3.5") || filename.Contains("3_5") || modelName.Contains("3.5") || modelClass.Contains("sd3.5"))
                return "sd3.5";
            return "sd3";
        }

        // Wan video models
        if (filename.Contains("wan") || modelName.Contains("wan") || modelClass.Contains("wan"))
        {
            if (filename.Contains("2.2") || modelName.Contains("2.2") || filename.Contains("2_2") || modelName.Contains("2_2"))
                return "wan-2.2";
            if (filename.Contains("2.1") || modelName.Contains("2.1") || filename.Contains("2_1") || modelName.Contains("2_1"))
                return "wan-2.1";
            return "wan-2.1"; // Default to 2.1
        }

        // Generic video models
        if (modelClass.Contains("-i2v") || modelClass.Contains("image2video") || modelClass.Contains("-ti2v") || 
            modelClass.Contains("-flf2v") || modelClass.Contains("video2world") || 
            filename.Contains("i2v") || filename.Contains("ti2v") || filename.Contains("flf2v"))
            return "video";

        // SDXL models
        if (modelClass.Contains("sdxl") || filename.Contains("sdxl"))
        {
            if (filename.Contains("turbo") || modelName.Contains("turbo"))
                return "sdxl-turbo";
            if (filename.Contains("lightning") || modelName.Contains("lightning"))
                return "sdxl-lightning";
            return "sdxl";
        }

        // SD2.x models
        if (modelClass.Contains("stable-diffusion-v2") || modelClass.Contains("stable-diffusion-2") || 
            filename.Contains("sd2") || filename.Contains("v2-"))
            return "sd2";

        // SD1.x models
        if (modelClass.Contains("stable-diffusion-v1") || modelClass.Contains("stable-diffusion-1") ||
            filename.Contains("sd1") || filename.Contains("v1-"))
        {
            if (filename.Contains("turbo") || modelName.Contains("turbo"))
                return "sd15-turbo";
            return "sd15";
        }

        // LCM models
        if (filename.Contains("lcm") || modelName.Contains("lcm"))
            return "lcm";

        // Fallback based on resolution
        if (model.StandardWidth == 1024 && model.StandardHeight == 1024)
            return "sdxl";
        if (model.StandardWidth == 512 && model.StandardHeight == 512)
            return "sd15";
        if (model.StandardWidth == 1328 && model.StandardHeight == 1328)
            return "qwen-image"; // Qwen default resolution

        return "unknown";
    }

    /// <summary>Ensures a required model component exists, downloading it if necessary. Uses a thread-safe download lock to prevent duplicate downloads.</summary>
    public static string EnsureModelExists(string modelType, string fileName, string url, string hash = null)
    {
        T2IModelHandler modelSet = Program.T2IModelSets[modelType];
        if (modelSet.Models.TryGetValue(fileName, out T2IModel value))
        {
            return value.RawFilePath;
        }
        string baseName = Path.GetFileName(fileName);
        T2IModel existing = modelSet.Models.Values.FirstOrDefault(m => m.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase) || m.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.RawFilePath;
        }
        string downloadFolder = modelSet.FolderPaths[0];
        string filePath = Path.Combine(downloadFolder, fileName);
        string fileDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
        {
            Directory.CreateDirectory(fileDir);
        }
        if (File.Exists(filePath))
        {
            Logs.Debug($"[SDcpp] Found existing file at {filePath}, refreshing model list...");
            Program.RefreshAllModelSets();
            return filePath;
        }
        lock (ModelDownloadLock)
        {
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

    /// <summary>Gets the list of supported features for a given model architecture.</summary>
    public static List<string> GetFeaturesForArchitecture(string architecture)
    {
        List<string> features = [];
        switch (architecture)
        {
            // Flux family
            case "flux-dev":
                features.AddRange(["flux", "flux-dev", "lora", "controlnet"]);
                break;
            case "flux-schnell":
                features.AddRange(["flux", "flux-schnell", "lora", "controlnet", "fast"]);
                break;
            case "flux-kontext":
                features.AddRange(["flux", "flux-kontext", "image-edit", "lora"]);
                break;
            case "flux2-dev":
                features.AddRange(["flux", "flux2", "flux2-dev", "lora", "controlnet"]);
                break;

            // Chroma family (Flux-based distilled)
            case "chroma":
                features.AddRange(["flux", "chroma", "distilled", "lora"]);
                break;
            case "chroma-radiance":
                features.AddRange(["flux", "chroma", "chroma-radiance", "distilled", "lora"]);
                break;

            // Ovis (Flux-based multimodal)
            case "ovis":
                features.AddRange(["flux", "ovis", "multimodal", "lora"]);
                break;

            // Qwen Image family
            case "qwen-image":
                features.AddRange(["qwen-image", "lora"]);
                break;
            case "qwen-image-edit":
                features.AddRange(["qwen-image", "qwen-image-edit", "image-edit", "lora"]);
                break;

            // Z-Image
            case "z-image":
                features.AddRange(["z-image", "lora"]);
                break;

            // SD3 family
            case "sd3":
                features.AddRange(["sd3", "lora"]);
                break;
            case "sd3.5":
                features.AddRange(["sd3", "sd3.5", "lora"]);
                break;

            // SDXL family
            case "sdxl":
                features.AddRange(["sdxl", "lora", "controlnet"]);
                break;
            case "sdxl-turbo":
                features.AddRange(["sdxl", "turbo", "lora", "controlnet", "fast"]);
                break;
            case "sdxl-lightning":
                features.AddRange(["sdxl", "lightning", "lora", "controlnet", "fast"]);
                break;

            // SD1.x/SD2.x family
            case "sd15":
                features.AddRange(["sd15", "lora", "controlnet"]);
                break;
            case "sd15-turbo":
                features.AddRange(["sd15", "turbo", "lora", "controlnet", "fast"]);
                break;
            case "sd2":
                features.AddRange(["sd2", "lora", "controlnet"]);
                break;

            // LCM
            case "lcm":
                features.AddRange(["lcm", "lora", "controlnet", "fast"]);
                break;

            // Video models
            case "wan-2.1":
                features.AddRange(["video", "wan", "wan-2.1", "txt2vid", "img2vid"]);
                break;
            case "wan-2.2":
                features.AddRange(["video", "wan", "wan-2.2", "txt2vid", "img2vid"]);
                break;
            case "video":
                features.AddRange(["video", "img2vid"]);
                break;

            default:
                features.AddRange(["lora", "controlnet"]);
                break;
        }
        return features;
    }

    /// <summary>Determines if the architecture supports image editing (requires input image).</summary>
    public static bool IsImageEditArchitecture(string architecture) =>
        architecture is "flux-kontext" or "qwen-image-edit";

    /// <summary>Determines if the architecture is a video generation model.</summary>
    public static bool IsVideoArchitecture(string architecture) =>
        architecture is "wan-2.1" or "wan-2.2" or "video";

    /// <summary>Determines if the architecture is Flux-based (includes Chroma, Ovis).</summary>
    public static bool IsFluxBased(string architecture) =>
        architecture is "flux-dev" or "flux-schnell" or "flux-kontext" or "flux2-dev" or "chroma" or "chroma-radiance" or "ovis";

    /// <summary>Determines if the architecture requires a Qwen LLM component.</summary>
    public static bool RequiresQwenLLM(string architecture) =>
        architecture is "z-image" or "qwen-image" or "qwen-image-edit";

    /// <summary>Determines if the architecture is a distilled/fast model that works with fewer steps.</summary>
    public static bool IsDistilledModel(string architecture) =>
        architecture is "flux-schnell" or "chroma" or "chroma-radiance" or "sdxl-turbo" or "sdxl-lightning" or "sd15-turbo" or "lcm";

    /// <summary>Gets the recommended minimum steps for a given architecture.</summary>
    public static int GetRecommendedMinSteps(string architecture) => architecture switch
    {
        "flux-schnell" => 4,
        "chroma" or "chroma-radiance" => 4,
        "sdxl-turbo" or "sdxl-lightning" => 4,
        "sd15-turbo" => 4,
        "lcm" => 4,
        "flux-dev" or "flux-kontext" or "flux2-dev" => 20,
        "ovis" => 20,
        "sd3" or "sd3.5" => 20,
        "qwen-image" or "qwen-image-edit" => 20,
        "z-image" => 20,
        _ => 20
    };

    /// <summary>Gets the recommended CFG scale for a given architecture.</summary>
    public static double GetRecommendedCFG(string architecture) => architecture switch
    {
        "flux-dev" or "flux-schnell" or "flux-kontext" or "flux2-dev" => 1.0,
        "chroma" or "chroma-radiance" or "ovis" => 1.0,
        "sdxl-turbo" or "sdxl-lightning" => 1.0,
        "sd15-turbo" or "lcm" => 1.0,
        "sd3" or "sd3.5" => 4.5,
        "qwen-image" or "qwen-image-edit" => 5.0,
        "z-image" => 5.0,
        _ => 7.0
    };

    /// <summary>Gets the default resolution for a given architecture.</summary>
    public static (int width, int height) GetDefaultResolution(string architecture) => architecture switch
    {
        "flux-dev" or "flux-schnell" or "flux-kontext" or "flux2-dev" => (1024, 1024),
        "chroma" or "chroma-radiance" or "ovis" => (1024, 1024),
        "sd3" or "sd3.5" => (1024, 1024),
        "sdxl" or "sdxl-turbo" or "sdxl-lightning" => (1024, 1024),
        "qwen-image" or "qwen-image-edit" => (1328, 1328),
        "z-image" => (1024, 1024),
        "sd15" or "sd15-turbo" or "sd2" => (512, 512),
        "lcm" => (512, 512),
        "wan-2.1" or "wan-2.2" or "video" => (832, 480),
        _ => (512, 512)
    };
}
