using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>
/// Manages Flux model components (diffusion model, VAE, CLIP-L, T5-XXL).
/// Flux models require 4 separate files to function properly.
/// </summary>
public class FluxModelComponents
{
    /// <summary>Path to the main Flux diffusion model (GGUF format preferred)</summary>
    public string DiffusionModelPath { get; set; }

    /// <summary>Path to the VAE model (ae.safetensors or ae.sft)</summary>
    public string VAEPath { get; set; }

    /// <summary>Path to the CLIP-L text encoder</summary>
    public string CLIPLPath { get; set; }

    /// <summary>Path to the T5-XXL text encoder</summary>
    public string T5XXLPath { get; set; }

    /// <summary>Indicates if all required components were found</summary>
    public bool IsComplete => !string.IsNullOrEmpty(DiffusionModelPath)
                             && !string.IsNullOrEmpty(VAEPath)
                             && !string.IsNullOrEmpty(CLIPLPath)
                             && !string.IsNullOrEmpty(T5XXLPath);

    /// <summary>List of missing components</summary>
    public List<string> MissingComponents
    {
        get
        {
            List<string> missing = [];
            if (string.IsNullOrEmpty(DiffusionModelPath)) missing.Add("Diffusion Model");
            if (string.IsNullOrEmpty(VAEPath)) missing.Add("VAE (ae.safetensors)");
            if (string.IsNullOrEmpty(CLIPLPath)) missing.Add("CLIP-L (clip_l.safetensors)");
            if (string.IsNullOrEmpty(T5XXLPath)) missing.Add("T5-XXL (t5xxl_fp16.safetensors)");
            return missing;
        }
    }

    /// <summary>
    /// Discovers and validates all required Flux model components.
    /// Searches in multiple locations relative to the main model file.
    /// </summary>
    /// <param name="model">The Flux model to find components for</param>
    /// <param name="overrideVAE">Optional override path for VAE</param>
    /// <param name="overrideCLIPL">Optional override path for CLIP-L</param>
    /// <param name="overrideT5XXL">Optional override path for T5-XXL</param>
    /// <returns>FluxModelComponents with discovered paths</returns>
    public static FluxModelComponents DiscoverComponents(
        T2IModel model,
        string overrideVAE = null,
        string overrideCLIPL = null,
        string overrideT5XXL = null)
    {
        var components = new FluxModelComponents
        {
            DiffusionModelPath = model.RawFilePath
        };

        // Get search directories
        string modelDir = Path.GetDirectoryName(model.RawFilePath);
        string parentDir = Directory.GetParent(modelDir)?.FullName;
        List<string> searchDirs = [modelDir];
        if (!string.IsNullOrEmpty(parentDir))
            searchDirs.Add(parentDir);

        // Look for standard SwarmUI model directories
        string swarmModelsDir = Path.Combine(Program.DataDir, "Models");
        if (Directory.Exists(swarmModelsDir))
        {
            searchDirs.Add(swarmModelsDir);

            string vaeDir = Path.Combine(swarmModelsDir, "VAE");
            if (Directory.Exists(vaeDir))
                searchDirs.Add(vaeDir);

            string clipDir = Path.Combine(swarmModelsDir, "CLIP");
            if (Directory.Exists(clipDir))
                searchDirs.Add(clipDir);
        }

        // Find VAE
        if (!string.IsNullOrEmpty(overrideVAE) && File.Exists(overrideVAE))
        {
            components.VAEPath = overrideVAE;
        }
        else
        {
            components.VAEPath = FindFile(searchDirs, ["ae.safetensors", "ae.sft", "ae.gguf"]);
        }

        // Find CLIP-L
        if (!string.IsNullOrEmpty(overrideCLIPL) && File.Exists(overrideCLIPL))
        {
            components.CLIPLPath = overrideCLIPL;
        }
        else
        {
            components.CLIPLPath = FindFile(searchDirs, ["clip_l.safetensors", "clip_l.sft", "clip_l.gguf"]);
        }

        // Find T5-XXL
        if (!string.IsNullOrEmpty(overrideT5XXL) && File.Exists(overrideT5XXL))
        {
            components.T5XXLPath = overrideT5XXL;
        }
        else
        {
            components.T5XXLPath = FindFile(searchDirs, [
                "t5xxl_fp16.safetensors",
                "t5xxl_fp32.safetensors",
                "t5xxl.safetensors",
                "t5xxl_fp16.sft",
                "t5xxl_fp32.sft",
                "t5xxl.sft",
                "t5xxl.gguf"
            ]);
        }

        return components;
    }

    /// <summary>
    /// Searches multiple directories for a file matching any of the given filenames.
    /// </summary>
    /// <param name="directories">Directories to search</param>
    /// <param name="filenames">Possible filenames to look for</param>
    /// <returns>Full path to first matching file, or null if not found</returns>
    private static string FindFile(List<string> directories, string[] filenames)
    {
        foreach (string dir in directories)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (string filename in filenames)
            {
                string fullPath = Path.Combine(dir, filename);
                if (File.Exists(fullPath))
                {
                    Logs.Debug($"[SDcpp] Found component: {filename} at {fullPath}");
                    return fullPath;
                }
            }

            // Also search subdirectories (one level deep)
            try
            {
                foreach (string subDir in Directory.GetDirectories(dir))
                {
                    foreach (string filename in filenames)
                    {
                        string fullPath = Path.Combine(subDir, filename);
                        if (File.Exists(fullPath))
                        {
                            Logs.Debug($"[SDcpp] Found component: {filename} at {fullPath}");
                            return fullPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[SDcpp] Error searching subdirectories in {dir}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a helpful error message with download instructions for missing components.
    /// </summary>
    /// <returns>Formatted error message with download links</returns>
    public string GetMissingComponentsMessage()
    {
        if (IsComplete)
            return string.Empty;

        var message = new System.Text.StringBuilder();
        message.AppendLine("Missing required Flux model components:");
        message.AppendLine();

        foreach (string component in MissingComponents)
        {
            message.AppendLine($"  - {component}");
        }

        message.AppendLine();
        message.AppendLine("Required Flux components and download locations:");
        message.AppendLine();
        message.AppendLine("1. VAE (ae.safetensors):");
        message.AppendLine("   https://huggingface.co/black-forest-labs/FLUX.1-dev/blob/main/ae.safetensors");
        message.AppendLine();
        message.AppendLine("2. CLIP-L text encoder (clip_l.safetensors):");
        message.AppendLine("   https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/clip_l.safetensors");
        message.AppendLine();
        message.AppendLine("3. T5-XXL text encoder (t5xxl_fp16.safetensors):");
        message.AppendLine("   https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/t5xxl_fp16.safetensors");
        message.AppendLine();
        message.AppendLine("Place these files in the same directory as your Flux model, or in:");
        message.AppendLine($"  {Path.Combine(Program.DataDir, "Models")}");
        message.AppendLine($"  {Path.Combine(Program.DataDir, "Models", "VAE")}");
        message.AppendLine($"  {Path.Combine(Program.DataDir, "Models", "CLIP")}");

        return message.ToString();
    }

    /// <summary>
    /// Validates that all component files exist and are accessible.
    /// </summary>
    /// <returns>True if all components exist, false otherwise</returns>
    public bool ValidateComponentsExist()
    {
        if (!File.Exists(DiffusionModelPath))
        {
            Logs.Error($"[SDcpp] Diffusion model not found: {DiffusionModelPath}");
            return false;
        }

        if (!File.Exists(VAEPath))
        {
            Logs.Error($"[SDcpp] VAE not found: {VAEPath}");
            return false;
        }

        if (!File.Exists(CLIPLPath))
        {
            Logs.Error($"[SDcpp] CLIP-L not found: {CLIPLPath}");
            return false;
        }

        if (!File.Exists(T5XXLPath))
        {
            Logs.Error($"[SDcpp] T5-XXL not found: {T5XXLPath}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Logs the discovered component paths for debugging.
    /// </summary>
    public void LogComponentPaths()
    {
        Logs.Info("[SDcpp] Flux model components:");
        Logs.Info($"  Diffusion Model: {DiffusionModelPath ?? "NOT FOUND"}");
        Logs.Info($"  VAE: {VAEPath ?? "NOT FOUND"}");
        Logs.Info($"  CLIP-L: {CLIPLPath ?? "NOT FOUND"}");
        Logs.Info($"  T5-XXL: {T5XXLPath ?? "NOT FOUND"}");
    }
}
