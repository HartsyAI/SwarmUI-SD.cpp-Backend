using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System;
using System.Collections.Generic;
using System.IO;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Dynamic VRAM policy that automatically determines which offload flags are needed based on
/// actual model sizes, GPU VRAM, and generation parameters. Always runs but only applies flags when needed.</summary>
public class SDcppVramPolicy
{
    /// <summary>Result of VRAM policy evaluation containing recommended flags and diagnostics.</summary>
    public class PolicyResult
    {
        public bool VaeTiling { get; set; } = false;
        public bool ClipOnCpu { get; set; } = false;
        public bool VaeOnCpu { get; set; } = false;
        public bool OffloadToCpu { get; set; } = false;

        public long EstimatedVramBytes { get; set; }
        public long FreeVramBytes { get; set; }
        public long TotalVramBytes { get; set; }
        public double FitRatio { get; set; }
        public string Decision { get; set; } = "unknown";
        public List<string> Reasons { get; set; } = [];
    }

    /// <summary>Runtime overhead factor for activations and buffers during inference.</summary>
    private const double RuntimeFactor = 1.5;

    /// <summary>Safety margin to avoid OOM (leave some headroom).</summary>
    private const double SafetyMargin = 0.95;

    /// <summary>Minimum VRAM threshold in GB below which we always apply aggressive offloading.</summary>
    private const double AggressiveThresholdGB = 6.0;

    /// <summary>VRAM threshold in GB above which we default to no offloading unless fit test fails.</summary>
    private const double ComfortableThresholdGB = 12.0;

    /// <summary>Evaluates VRAM requirements and returns recommended offload flags.
    /// This method always runs but only sets flags when actually needed.</summary>
    /// <param name="input">The generation input parameters.</param>
    /// <param name="diffusionModelPath">Path to the main diffusion model file.</param>
    /// <param name="encoderPaths">Paths to text encoder files (CLIP, T5, LLM, etc.).</param>
    /// <param name="vaePath">Path to VAE model file (optional).</param>
    /// <param name="gpuIndex">GPU index to check VRAM for (default 0).</param>
    /// <returns>Policy result with recommended flags and diagnostics.</returns>
    public static PolicyResult Evaluate(T2IParamInput input, string diffusionModelPath,
        IEnumerable<string> encoderPaths, string vaePath = null, int gpuIndex = 0)
    {
        PolicyResult result = new();

        try
        {
            // Get GPU VRAM info
            NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
            if (gpus is null || gpus.Length == 0 || gpuIndex >= gpus.Length)
            {
                result.Decision = "no_gpu";
                result.Reasons.Add("No NVIDIA GPU detected, skipping VRAM policy");
                return result;
            }

            NvidiaUtil.NvidiaInfo gpu = gpus[gpuIndex];
            result.TotalVramBytes = gpu.TotalMemory.InBytes;
            result.FreeVramBytes = gpu.FreeMemory.InBytes;

            double totalVramGB = result.TotalVramBytes / (1024.0 * 1024.0 * 1024.0);
            double freeVramGB = result.FreeVramBytes / (1024.0 * 1024.0 * 1024.0);

            Logs.Debug($"[SDcpp VRAM] GPU: {gpu.GPUName}, Total: {totalVramGB:F2} GB, Free: {freeVramGB:F2} GB");

            // Calculate model sizes
            long diffusionSize = GetFileSize(diffusionModelPath);
            long encoderSize = 0;
            foreach (string encoderPath in encoderPaths ?? [])
            {
                encoderSize += GetFileSize(encoderPath);
            }
            long vaeSize = GetFileSize(vaePath);

            Logs.Debug($"[SDcpp VRAM] Model sizes - Diffusion: {diffusionSize / (1024.0 * 1024.0):F1} MB, " +
                      $"Encoders: {encoderSize / (1024.0 * 1024.0):F1} MB, VAE: {vaeSize / (1024.0 * 1024.0):F1} MB");

            // Calculate resolution overhead
            int width = input.GetImageWidth();
            int height = input.GetImageHeight();
            int batchSize = input.Get(T2IParamTypes.BatchSize, 1);
            long resolutionOverhead = CalculateResolutionOverhead(width, height, batchSize);

            Logs.Debug($"[SDcpp VRAM] Resolution: {width}x{height}, Batch: {batchSize}, " +
                      $"Resolution overhead: {resolutionOverhead / (1024.0 * 1024.0):F1} MB");

            // Estimate total VRAM needed
            long baseModelFootprint = diffusionSize + encoderSize + vaeSize;
            result.EstimatedVramBytes = (long)(baseModelFootprint * RuntimeFactor) + resolutionOverhead;

            double estimatedVramGB = result.EstimatedVramBytes / (1024.0 * 1024.0 * 1024.0);
            Logs.Debug($"[SDcpp VRAM] Estimated VRAM needed: {estimatedVramGB:F2} GB");

            // Calculate fit ratio
            result.FitRatio = result.FreeVramBytes > 0
                ? (double)result.EstimatedVramBytes / (result.FreeVramBytes * SafetyMargin)
                : double.MaxValue;

            Logs.Debug($"[SDcpp VRAM] Fit ratio: {result.FitRatio:F3}");

            // Determine flags based on fit ratio and thresholds
            DetermineFlags(result, totalVramGB, freeVramGB, estimatedVramGB);

            Logs.Info($"[SDcpp VRAM] Decision: {result.Decision} (fit ratio: {result.FitRatio:F2}, " +
                     $"estimated: {estimatedVramGB:F2} GB, free: {freeVramGB:F2} GB)");
            foreach (string reason in result.Reasons)
            {
                Logs.Debug($"[SDcpp VRAM] - {reason}");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp VRAM] Error evaluating VRAM policy: {ex.Message}");
            result.Decision = "error";
            result.Reasons.Add($"VRAM policy evaluation failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>Gets file size in bytes, returns 0 if file doesn't exist or path is null.</summary>
    private static long GetFileSize(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return 0;
        }
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Calculates approximate VRAM overhead for latents and activations based on resolution.</summary>
    private static long CalculateResolutionOverhead(int width, int height, int batchSize)
    {
        // Latent space is 1/8 of pixel space, 4 channels, fp16 = 2 bytes
        // Plus activation memory which scales with resolution
        long latentSize = (width / 8) * (height / 8) * 4 * 2 * batchSize;
        
        // Activation memory is roughly proportional to resolution
        // Estimate ~100MB per megapixel for activations
        double megapixels = (width * height) / (1024.0 * 1024.0);
        long activationSize = (long)(megapixels * 100 * 1024 * 1024) * batchSize;

        return latentSize + activationSize;
    }

    /// <summary>Determines which offload flags to enable based on fit ratio and VRAM thresholds.</summary>
    private static void DetermineFlags(PolicyResult result, double totalVramGB, double freeVramGB, double estimatedVramGB)
    {
        // If very low VRAM GPU, always be aggressive
        if (totalVramGB < AggressiveThresholdGB)
        {
            result.VaeTiling = true;
            result.ClipOnCpu = true;
            result.VaeOnCpu = true;
            result.OffloadToCpu = true;
            result.Decision = "aggressive_low_vram";
            result.Reasons.Add($"GPU has only {totalVramGB:F1} GB total VRAM (< {AggressiveThresholdGB} GB threshold)");
            result.Reasons.Add("Enabling all offload flags for maximum VRAM savings");
            return;
        }

        // Comfortable VRAM and fits well - no offloading needed
        if (totalVramGB >= ComfortableThresholdGB && result.FitRatio <= 0.70)
        {
            result.Decision = "comfortable_fit";
            result.Reasons.Add($"GPU has {totalVramGB:F1} GB VRAM and model fits comfortably (ratio: {result.FitRatio:F2})");
            result.Reasons.Add("No offload flags needed");
            return;
        }

        // Incremental escalation based on fit ratio
        if (result.FitRatio <= 0.70)
        {
            result.Decision = "fits_no_offload";
            result.Reasons.Add($"Model fits in VRAM with good margin (ratio: {result.FitRatio:F2})");
            return;
        }

        if (result.FitRatio <= 0.85)
        {
            result.VaeTiling = true;
            result.Decision = "tight_fit_tiling";
            result.Reasons.Add($"Model fits but tight (ratio: {result.FitRatio:F2})");
            result.Reasons.Add("Enabling VAE tiling for safety margin");
            return;
        }

        if (result.FitRatio <= 0.95)
        {
            result.VaeTiling = true;
            result.ClipOnCpu = true;
            result.Decision = "moderate_pressure";
            result.Reasons.Add($"Model approaching VRAM limit (ratio: {result.FitRatio:F2})");
            result.Reasons.Add("Enabling VAE tiling and CLIP on CPU");
            return;
        }

        if (result.FitRatio <= 1.05)
        {
            result.VaeTiling = true;
            result.ClipOnCpu = true;
            result.VaeOnCpu = true;
            result.Decision = "high_pressure";
            result.Reasons.Add($"Model near or at VRAM limit (ratio: {result.FitRatio:F2})");
            result.Reasons.Add("Enabling VAE tiling, CLIP on CPU, and VAE on CPU");
            return;
        }

        // Over capacity - enable everything
        result.VaeTiling = true;
        result.ClipOnCpu = true;
        result.VaeOnCpu = true;
        result.OffloadToCpu = true;
        result.Decision = "over_capacity";
        result.Reasons.Add($"Model exceeds available VRAM (ratio: {result.FitRatio:F2})");
        result.Reasons.Add($"Estimated: {estimatedVramGB:F2} GB, Free: {freeVramGB:F2} GB");
        result.Reasons.Add("Enabling all offload flags to prevent OOM");
    }

    /// <summary>Applies the policy result to a parameters dictionary.</summary>
    /// <param name="parameters">The parameters dictionary to modify.</param>
    /// <param name="policyResult">The policy evaluation result.</param>
    /// <param name="respectUserOverrides">If true, user-set values take precedence over policy.</param>
    public static void ApplyToParameters(Dictionary<string, object> parameters, PolicyResult policyResult,
        bool respectUserOverrides = true)
    {
        // Apply flags, respecting user overrides if requested
        void SetFlag(string key, bool policyValue)
        {
            if (respectUserOverrides && parameters.TryGetValue(key, out object existing) && existing is bool userValue)
            {
                // User explicitly set this flag - keep their value if it's MORE aggressive
                if (userValue || !policyValue)
                {
                    return;
                }
            }
            parameters[key] = policyValue;
        }

        SetFlag("vae_tiling", policyResult.VaeTiling);
        SetFlag("clip_on_cpu", policyResult.ClipOnCpu);
        SetFlag("vae_on_cpu", policyResult.VaeOnCpu);
        SetFlag("offload_to_cpu", policyResult.OffloadToCpu);

        // Log what was applied
        List<string> appliedFlags = [];
        if (policyResult.VaeTiling) appliedFlags.Add("vae_tiling");
        if (policyResult.ClipOnCpu) appliedFlags.Add("clip_on_cpu");
        if (policyResult.VaeOnCpu) appliedFlags.Add("vae_on_cpu");
        if (policyResult.OffloadToCpu) appliedFlags.Add("offload_to_cpu");

        if (appliedFlags.Count > 0)
        {
            Logs.Info($"[SDcpp VRAM] Applied flags: {string.Join(", ", appliedFlags)}");
        }
        else
        {
            Logs.Debug("[SDcpp VRAM] No offload flags needed");
        }
    }
}
