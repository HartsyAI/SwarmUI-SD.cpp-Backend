using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>
/// Handles conversion of Flux models to GGUF format using SD.cpp's conversion tool.
/// GGUF format is required for Flux models to avoid fp16 overflow issues and reduce VRAM usage.
/// </summary>
public static class GGUFConverter
{
    /// <summary>
    /// Checks if a model file is already in GGUF format.
    /// </summary>
    /// <param name="modelPath">Path to model file</param>
    /// <returns>True if already GGUF, false otherwise</returns>
    public static bool IsGGUFFormat(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            return false;

        string extension = Path.GetExtension(modelPath).ToLowerInvariant();
        return extension == ".gguf";
    }

    /// <summary>
    /// Generates the output path for a converted GGUF model.
    /// </summary>
    /// <param name="inputPath">Original model path</param>
    /// <param name="quantization">Quantization level (q8_0, q4_0, etc.)</param>
    /// <returns>Path where converted model will be saved</returns>
    public static string GetConvertedModelPath(string inputPath, string quantization = "q8_0")
    {
        string directory = Path.GetDirectoryName(inputPath);
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{fileNameWithoutExt}-{quantization}.gguf");
    }

    /// <summary>
    /// Converts a Flux model to GGUF format with the specified quantization level.
    /// </summary>
    /// <param name="sdcppExecutable">Path to SD.cpp executable</param>
    /// <param name="inputModelPath">Path to model to convert</param>
    /// <param name="quantization">Quantization level (f32, f16, q8_0, q4_0, q2_k, etc.)</param>
    /// <param name="outputPath">Optional custom output path</param>
    /// <param name="debugMode">Enable verbose logging</param>
    /// <returns>Tuple of (success, output path, error message)</returns>
    public static async Task<(bool Success, string OutputPath, string ErrorMessage)> ConvertToGGUF(
        string sdcppExecutable,
        string inputModelPath,
        string quantization = "q8_0",
        string outputPath = null,
        bool debugMode = false)
    {
        try
        {
            // Validate inputs
            if (!File.Exists(sdcppExecutable))
            {
                return (false, null, $"SD.cpp executable not found: {sdcppExecutable}");
            }

            if (!File.Exists(inputModelPath))
            {
                return (false, null, $"Input model not found: {inputModelPath}");
            }

            if (IsGGUFFormat(inputModelPath))
            {
                Logs.Info($"[SDcpp] Model is already in GGUF format: {inputModelPath}");
                return (true, inputModelPath, null);
            }

            // Determine output path
            outputPath ??= GetConvertedModelPath(inputModelPath, quantization);

            // Check if conversion already exists
            if (File.Exists(outputPath))
            {
                Logs.Info($"[SDcpp] Converted model already exists: {outputPath}");
                return (true, outputPath, null);
            }

            Logs.Info($"[SDcpp] Converting model to GGUF format ({quantization})...");
            Logs.Info($"[SDcpp] Input: {inputModelPath}");
            Logs.Info($"[SDcpp] Output: {outputPath}");

            // Build conversion command
            // Format: sd.exe -M convert -m input.sft -o output.gguf --type q8_0
            string arguments = $"-M convert -m \"{inputModelPath}\" -o \"{outputPath}\" --type {quantization}";

            if (debugMode)
            {
                arguments += " -v";
            }

            // Execute conversion
            ProcessStartInfo processInfo = new()
            {
                FileName = sdcppExecutable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Logs.Info($"[SDcpp] Starting conversion: {sdcppExecutable} {arguments}");

            using Process process = Process.Start(processInfo);
            if (process == null)
            {
                return (false, null, "Failed to start conversion process");
            }

            // Capture output
            System.Text.StringBuilder outputBuilder = new();
            System.Text.StringBuilder errorBuilder = new();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    if (debugMode)
                        Logs.Debug($"[SDcpp Convert] {e.Data}");
                    else if (e.Data.Contains("%") || e.Data.Contains("convert"))
                        Logs.Info($"[SDcpp Convert] {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    if (debugMode)
                        Logs.Debug($"[SDcpp Convert Error] {e.Data}");
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for conversion to complete (can take 5-15 minutes for large models)
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string error = errorBuilder.ToString();
                Logs.Error($"[SDcpp] Conversion failed with exit code {process.ExitCode}");
                Logs.Error($"[SDcpp] Error output: {error}");
                return (false, null, $"Conversion failed: {error}");
            }

            // Verify output file was created
            if (!File.Exists(outputPath))
            {
                return (false, null, "Conversion completed but output file not found");
            }

            Logs.Info($"[SDcpp] Conversion completed successfully: {outputPath}");
            return (true, outputPath, null);
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error during GGUF conversion: {ex.Message}");
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Gets the recommended quantization level based on available VRAM.
    /// </summary>
    /// <param name="availableVRAM_GB">Available VRAM in gigabytes</param>
    /// <returns>Recommended quantization level</returns>
    public static string GetRecommendedQuantization(int availableVRAM_GB)
    {
        return availableVRAM_GB switch
        {
            >= 12 => "q8_0",  // 12GB+ - Best quality
            >= 8 => "q4_0",   // 8-12GB - Good balance
            >= 6 => "q4_0",   // 6-8GB - Good balance
            >= 4 => "q3_k",   // 4-6GB - Lower quality
            _ => "q2_k"       // <4GB - Minimal quality
        };
    }

    /// <summary>
    /// Estimates the output file size for a given quantization level.
    /// </summary>
    /// <param name="quantization">Quantization level</param>
    /// <returns>Approximate size in GB</returns>
    public static double EstimateGGUFSize(string quantization)
    {
        return quantization.ToLowerInvariant() switch
        {
            "f32" => 24.0,
            "f16" => 12.0,
            "q8_0" => 12.0,
            "q4_0" or "q4_k" => 6.4,
            "q3_k" => 4.9,
            "q2_k" => 3.7,
            _ => 12.0  // Default estimate
        };
    }

    /// <summary>
    /// Deletes a converted GGUF model file.
    /// Useful if conversion failed or user wants to re-convert.
    /// </summary>
    /// <param name="ggufPath">Path to GGUF file to delete</param>
    /// <returns>True if deleted successfully</returns>
    public static bool DeleteConvertedModel(string ggufPath)
    {
        try
        {
            if (File.Exists(ggufPath) && IsGGUFFormat(ggufPath))
            {
                File.Delete(ggufPath);
                Logs.Info($"[SDcpp] Deleted converted model: {ggufPath}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error deleting converted model: {ex.Message}");
            return false;
        }
    }
}
