using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Handles conversion of models to GGUF format using SD.cpp's conversion tool.</summary>
public static class GGUFConverter
{
    private static readonly HashSet<string> SupportedQuantizations = ["f32", "f16", "q8_0", "q5_0", "q5_1", "q4_0", "q4_1", "q3_k", "q2_k"];

    /// <summary>Checks if a model file is already in GGUF format.</summary>
    public static bool IsGGUFFormat(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)) return false;
        return Path.GetExtension(modelPath).Equals(".gguf", StringComparison.InvariantCultureIgnoreCase);
    }

    /// <summary>Generates the output path for a converted GGUF model.</summary>
    public static string GetConvertedModelPath(string inputPath, string quantization = "q8_0")
    {
        string directory = Path.GetDirectoryName(inputPath);
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{fileNameWithoutExt}-{quantization}.gguf");
    }

    /// <summary>Converts a model to GGUF format with the specified quantization level.</summary>
    /// <param name="sdcppExecutable">Path to SD.cpp executable</param>
    /// <param name="inputModelPath">Path to model to convert</param>
    /// <param name="quantization">Quantization level</param>
    /// <param name="outputPath">Optional custom output path</param>
    /// <param name="progressCallback">Callback for progress updates (0.0 to 1.0)</param>
    /// <returns>Tuple of (success, output path, error message)</returns>
    public static async Task<(bool Success, string OutputPath, string ErrorMessage)> ConvertToGGUF(
        string sdcppExecutable,
        string inputModelPath,
        string quantization = "q8_0",
        string outputPath = null,
        Action<float> progressCallback = null)
    {
        try
        {
            if (!SupportedQuantizations.Contains(quantization))
            {
                return (false, null, $"Unsupported quantization: {quantization}. Supported: {string.Join(", ", SupportedQuantizations)}");
            }
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
            outputPath ??= GetConvertedModelPath(inputModelPath, quantization);
            if (File.Exists(outputPath))
            {
                Logs.Info($"[SDcpp] Converted model already exists: {outputPath}");
                return (true, outputPath, null);
            }
            progressCallback?.Invoke(0.0f);
            Logs.Info($"[SDcpp] Converting model to GGUF format ({quantization})...");
            Logs.Info($"[SDcpp] Input: {inputModelPath}");
            Logs.Info($"[SDcpp] Output: {outputPath}");
            string arguments = $"-M convert -m \"{inputModelPath}\" -o \"{outputPath}\" --type {quantization} -v";
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
            if (process is null)
            {
                return (false, null, "Failed to start conversion process");
            }
            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    Logs.Info($"[SDcpp Convert] {e.Data}");
                    if (e.Data.Contains("100%")) progressCallback?.Invoke(1.0f);
                    else if (e.Data.Contains('%'))
                    {
                        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d+)%");
                        if (match.Success && float.TryParse(match.Groups[1].Value, out float progress))
                        {
                            progressCallback?.Invoke(progress / 100f);
                        }
                    }
                }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    Logs.Debug($"[SDcpp Convert Error] {e.Data}");
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                string error = errorBuilder.ToString();
                Logs.Error($"[SDcpp] Conversion failed with exit code {process.ExitCode}");
                Logs.Error($"[SDcpp] Error output: {error}");
                return (false, null, $"Conversion failed: {error}");
            }
            if (!File.Exists(outputPath))
            {
                return (false, null, "Conversion completed but output file not found");
            }
            progressCallback?.Invoke(1.0f);
            Logs.Info($"[SDcpp] Conversion completed successfully: {outputPath}");
            return (true, outputPath, null);
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error during GGUF conversion: {ex.Message}");
            return (false, null, ex.Message);
        }
    }

    /// <summary>Gets the recommended quantization level based on available VRAM.</summary>
    public static string GetRecommendedQuantization(int availableVRAM_GB)
    {
        return availableVRAM_GB switch
        {
            >= 12 => "q8_0",
            >= 8 => "q4_0",
            >= 6 => "q4_0",
            >= 4 => "q3_k",
            _ => "q2_k"
        };
    }

    /// <summary>Estimates the output file size for a given quantization level.</summary>
    public static double EstimateGGUFSize(string quantization)
    {
        return quantization.ToLowerInvariant() switch
        {
            "f32" => 24.0,
            "f16" => 12.0,
            "q8_0" => 12.0,
            "q5_0" or "q5_1" => 9.5,
            "q4_0" or "q4_1" => 6.4,
            "q3_k" => 4.9,
            "q2_k" => 3.7,
            _ => 12.0
        };
    }

    /// <summary>Deletes a converted GGUF model file.</summary>
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
