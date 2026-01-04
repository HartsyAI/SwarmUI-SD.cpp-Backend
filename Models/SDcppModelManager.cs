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
    /// <summary>Detects the model architecture type (Flux, SD3, SDXL, SD15, etc.) based on filename, model class, and metadata.</summary>
    public static string DetectArchitecture(T2IModel model)
    {
        if (model is null) return "unknown";
        string filename = Path.GetFileNameWithoutExtension(model.RawFilePath).ToLowerInvariant();
        string modelName = model.Name.ToLowerInvariant();
        string modelClass = model.ModelClass?.ID.ToLowerInvariant() ?? "";
        if ((model.ModelClass?.ID?.ToLowerInvariant().Contains("flux") ?? false) ||
            filename.Contains("flux") || modelName.Contains("flux"))
            return "flux";
        if ((model.ModelClass?.ID?.ToLowerInvariant().Contains("sd3") ?? false) ||
            filename.Contains("sd3") || filename.Contains("sd3.5") || modelName.Contains("sd3"))
            return "sd3";
        if (filename.Contains("z_image") || filename.Contains("z-image") ||
            modelName.Contains("z_image") || modelName.Contains("z-image") ||
            modelClass.Contains("z-image"))
            return "z-image";
        if (filename.Contains("wan") || modelName.Contains("wan") || modelClass.Contains("wan"))
        {
            if (filename.Contains("2.2") || modelName.Contains("2.2") ||
                filename.Contains("2_2") || modelName.Contains("2_2"))
                return "wan-2.2";
            if (filename.Contains("2.1") || modelName.Contains("2.1") ||
                filename.Contains("2_1") || modelName.Contains("2_1"))
                return "wan-2.1";
            return "wan";
        }
        if (modelClass.Contains("-i2v") || modelClass.Contains("image2video") ||
            modelClass.Contains("-ti2v") || modelClass.Contains("-flf2v") ||
            modelClass.Contains("video2world") || filename.Contains("i2v") ||
            filename.Contains("ti2v") || filename.Contains("flf2v"))
            return "video";
        if (modelClass.Contains("sdxl") || filename.Contains("sdxl"))
        {
            if (filename.Contains("turbo") || modelName.Contains("turbo"))
                return "sdxl-turbo";
            return "sdxl";
        }
        if (modelClass.Contains("stable-diffusion-v2") || modelClass.Contains("stable-diffusion-2"))
            return "sd2";
        if (modelClass.Contains("stable-diffusion-v1") || modelClass.Contains("stable-diffusion-1"))
        {
            if (filename.Contains("turbo") || modelName.Contains("turbo"))
                return "sd15-turbo";
            return "sd15";
        }
        if (filename.Contains("lcm") || modelName.Contains("lcm"))
            return "lcm";
        if (model.StandardWidth == 1024 && model.StandardHeight == 1024)
            return "sdxl";
        if (model.StandardWidth == 512 && model.StandardHeight == 512)
            return "sd15";
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
        T2IModel existing = modelSet.Models.Values.FirstOrDefault(m =>
            m.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
            m.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
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
            case "flux":
                features.AddRange(["flux", "flux-dev", "lora", "controlnet"]);
                break;
            case "sd3":
                features.AddRange(["sd3", "sd3.5", "lora"]);
                break;
            case "sdxl":
            case "sdxl-turbo":
                features.AddRange(["sdxl", "lora", "controlnet"]);
                if (architecture == "sdxl-turbo")
                    features.Add("turbo");
                break;
            case "sd15":
            case "sd15-turbo":
            case "sd2":
                features.AddRange(["lora", "controlnet"]);
                if (architecture.Contains("turbo"))
                    features.Add("turbo");
                break;
            case "lcm":
                features.AddRange(["lcm", "lora", "controlnet"]);
                break;
            case "z-image":
                features.AddRange(["z-image", "lora"]);
                break;
            case "wan-2.1":
            case "wan-2.2":
            case "wan":
                features.AddRange(["video", "wan", "txt2vid", "img2vid"]);
                if (architecture == "wan-2.1")
                    features.Add("wan-2.1");
                else if (architecture == "wan-2.2")
                    features.Add("wan-2.2");
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
}
