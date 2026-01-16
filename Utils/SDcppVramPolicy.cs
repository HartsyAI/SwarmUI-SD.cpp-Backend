using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Dynamic VRAM policy that automatically determines which offload flags are needed based on
/// actual model sizes, GPU VRAM, and generation parameters. Applies flags incrementally based on
/// actual VRAM savings until the model fits in available memory.</summary>
public class SDcppVramPolicy
{
    /// <summary>Result of VRAM policy evaluation containing recommended flags and diagnostics.</summary>
    public class PolicyResult
    {
        public bool VaeTiling { get; set; } = false;
        public bool ClipOnCpu { get; set; } = false;
        public bool VaeOnCpu { get; set; } = false;
        public bool OffloadToCpu { get; set; } = false;
        public bool ControlNetOnCpu { get; set; } = false;

        public long EstimatedVramBytes { get; set; }
        public long FreeVramBytes { get; set; }
        public long TotalVramBytes { get; set; }
        public double FitRatio { get; set; }
        public string Decision { get; set; } = "unknown";
        public List<string> Reasons { get; set; } = [];
        public List<string> AppliedFlags { get; set; } = [];
    }

    /// <summary>Represents a VRAM-saving option with its estimated savings and performance impact.</summary>
    private class VramSavingOption
    {
        public string Name { get; set; }
        public string FlagName { get; set; }
        public long EstimatedSavingsBytes { get; set; }
        public int PerformanceImpact { get; set; } // 1=minimal, 2=moderate, 3=significant, 4=severe
        public Action<PolicyResult> ApplyFlag { get; set; }
        public Func<PolicyResult, bool> IsAlreadyApplied { get; set; }
    }

    /// <summary>Runtime overhead factor for activations and buffers during inference.</summary>
    private const double RuntimeFactor = 1.4;

    /// <summary>Safety margin to avoid OOM (leave some headroom).</summary>
    private const double SafetyMargin = 0.92;

    /// <summary>Minimum VRAM threshold in GB below which we always apply aggressive offloading.</summary>
    private const double AggressiveThresholdGB = 6.0;

    /// <summary>Evaluates VRAM requirements and returns recommended offload flags.
    /// This method always runs but only sets flags when actually needed.</summary>
    /// <param name="input">The generation input parameters.</param>
    /// <param name="diffusionModelPath">Path to the main diffusion model file.</param>
    /// <param name="encoderPaths">Dictionary mapping encoder type to path (e.g., "clip_l" -> path, "t5xxl" -> path).</param>
    /// <param name="vaePath">Path to VAE model file (optional).</param>
    /// <param name="controlNetPath">Path to ControlNet model file (optional).</param>
    /// <param name="gpuIndex">GPU index to check VRAM for (default 0).</param>
    /// <returns>Policy result with recommended flags and diagnostics.</returns>
    public static PolicyResult Evaluate(T2IParamInput input, string diffusionModelPath,
        Dictionary<string, string> encoderPaths, string vaePath = null, string controlNetPath = null, int gpuIndex = 0)
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

            // Calculate individual component sizes for smart offloading decisions
            long diffusionSize = GetFileSize(diffusionModelPath);
            long vaeSize = GetFileSize(vaePath);
            long controlNetSize = GetFileSize(controlNetPath);

            // Get individual encoder sizes
            Dictionary<string, long> encoderSizes = [];
            long totalEncoderSize = 0;
            foreach (KeyValuePair<string, string> kvp in encoderPaths ?? [])
            {
                long size = GetFileSize(kvp.Value);
                encoderSizes[kvp.Key] = size;
                totalEncoderSize += size;
            }

            Logs.Debug($"[SDcpp VRAM] Model sizes - Diffusion: {diffusionSize / (1024.0 * 1024.0):F1} MB, " +
                      $"Encoders: {totalEncoderSize / (1024.0 * 1024.0):F1} MB, VAE: {vaeSize / (1024.0 * 1024.0):F1} MB" +
                      (controlNetSize > 0 ? $", ControlNet: {controlNetSize / (1024.0 * 1024.0):F1} MB" : ""));

            // Log individual encoder sizes for debugging
            foreach (KeyValuePair<string, long> kvp in encoderSizes)
            {
                Logs.Debug($"[SDcpp VRAM]   {kvp.Key}: {kvp.Value / (1024.0 * 1024.0):F1} MB");
            }

            // Calculate resolution overhead
            int width = input.GetImageWidth();
            int height = input.GetImageHeight();
            int batchSize = input.Get(T2IParamTypes.BatchSize, 1);
            long resolutionOverhead = CalculateResolutionOverhead(width, height, batchSize);

            Logs.Debug($"[SDcpp VRAM] Resolution: {width}x{height}, Batch: {batchSize}, " +
                      $"Resolution overhead: {resolutionOverhead / (1024.0 * 1024.0):F1} MB");

            // Estimate total VRAM needed (without any offloading)
            long baseModelFootprint = diffusionSize + totalEncoderSize + vaeSize + controlNetSize;
            result.EstimatedVramBytes = (long)(baseModelFootprint * RuntimeFactor) + resolutionOverhead;

            double estimatedVramGB = result.EstimatedVramBytes / (1024.0 * 1024.0 * 1024.0);
            Logs.Debug($"[SDcpp VRAM] Estimated VRAM needed: {estimatedVramGB:F2} GB");

            // Calculate available VRAM with safety margin
            long availableVram = (long)(result.FreeVramBytes * SafetyMargin);

            // Calculate fit ratio
            result.FitRatio = availableVram > 0
                ? (double)result.EstimatedVramBytes / availableVram
                : double.MaxValue;

            Logs.Debug($"[SDcpp VRAM] Fit ratio: {result.FitRatio:F3}");

            // Determine flags using smart incremental approach
            DetermineFlagsIncremental(result, totalVramGB, availableVram, diffusionSize,
                vaeSize, encoderSizes, controlNetSize, resolutionOverhead);

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

    /// <summary>Overload for backward compatibility with IEnumerable encoder paths.</summary>
    public static PolicyResult Evaluate(T2IParamInput input, string diffusionModelPath,
        IEnumerable<string> encoderPaths, string vaePath = null, int gpuIndex = 0)
    {
        // Convert to dictionary with generic keys
        Dictionary<string, string> encoderDict = [];
        int i = 0;
        foreach (string path in encoderPaths ?? [])
        {
            encoderDict[$"encoder_{i++}"] = path;
        }
        return Evaluate(input, diffusionModelPath, encoderDict, vaePath, null, gpuIndex);
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

    /// <summary>Determines which offload flags to enable using smart incremental selection based on actual VRAM savings.
    /// Prioritizes flags by their VRAM savings vs performance impact ratio.</summary>
    private static void DetermineFlagsIncremental(PolicyResult result, double totalVramGB, long availableVram,
        long diffusionSize, long vaeSize, Dictionary<string, long> encoderSizes, long controlNetSize, long resolutionOverhead)
    {
        // If very low VRAM GPU, always be aggressive
        if (totalVramGB < AggressiveThresholdGB)
        {
            result.VaeTiling = true;
            result.ClipOnCpu = true;
            result.VaeOnCpu = true;
            result.OffloadToCpu = true;
            if (controlNetSize > 0) result.ControlNetOnCpu = true;
            result.Decision = "aggressive_low_vram";
            result.Reasons.Add($"GPU has only {totalVramGB:F1} GB total VRAM (< {AggressiveThresholdGB} GB threshold)");
            result.Reasons.Add("Enabling all offload flags for maximum VRAM savings");
            result.AppliedFlags.AddRange(["vae_tiling", "clip_on_cpu", "vae_on_cpu", "offload_to_cpu"]);
            if (controlNetSize > 0) result.AppliedFlags.Add("control_net_cpu");
            return;
        }

        // Calculate current VRAM usage estimate
        long currentEstimate = (long)((diffusionSize + vaeSize + controlNetSize) * RuntimeFactor) + resolutionOverhead;
        foreach (long size in encoderSizes.Values)
        {
            currentEstimate += (long)(size * RuntimeFactor);
        }

        // If it fits comfortably, no flags needed
        if (currentEstimate <= availableVram)
        {
            result.Decision = "fits_no_offload";
            result.Reasons.Add($"Model fits in VRAM with margin ({currentEstimate / (1024.0 * 1024.0 * 1024.0):F2} GB needed, {availableVram / (1024.0 * 1024.0 * 1024.0):F2} GB available)");
            return;
        }

        // Build list of available VRAM-saving options, sorted by efficiency (savings per performance cost)
        List<VramSavingOption> options = BuildVramSavingOptions(diffusionSize, vaeSize, encoderSizes, controlNetSize, resolutionOverhead);

        // Sort by efficiency: highest savings per performance impact first
        options = [.. options.OrderByDescending(o => o.EstimatedSavingsBytes / (double)o.PerformanceImpact)];

        long vramDeficit = currentEstimate - availableVram;
        long totalSavings = 0;
        List<string> appliedOptions = [];

        Logs.Debug($"[SDcpp VRAM] Need to save {vramDeficit / (1024.0 * 1024.0):F1} MB to fit in VRAM");

        // Apply flags incrementally until we fit
        foreach (VramSavingOption option in options)
        {
            if (option.IsAlreadyApplied(result))
                continue;

            // Check if this option would help
            if (option.EstimatedSavingsBytes <= 0)
                continue;

            // Apply the flag
            option.ApplyFlag(result);
            totalSavings += option.EstimatedSavingsBytes;
            appliedOptions.Add(option.Name);
            result.AppliedFlags.Add(option.FlagName);

            Logs.Debug($"[SDcpp VRAM] Applied {option.Name}: saves ~{option.EstimatedSavingsBytes / (1024.0 * 1024.0):F1} MB (impact: {option.PerformanceImpact})");

            // Check if we've saved enough
            if (totalSavings >= vramDeficit)
            {
                result.Decision = "incremental_fit";
                result.Reasons.Add($"Applied {appliedOptions.Count} optimization(s) to fit model in VRAM");
                result.Reasons.Add($"Total VRAM savings: ~{totalSavings / (1024.0 * 1024.0):F1} MB (needed: {vramDeficit / (1024.0 * 1024.0):F1} MB)");
                result.Reasons.Add($"Applied: {string.Join(", ", appliedOptions)}");
                return;
            }
        }

        // If we've applied all flags and still don't fit, note it but proceed anyway
        if (totalSavings < vramDeficit)
        {
            result.Decision = "best_effort";
            result.Reasons.Add($"Applied all available optimizations but may still exceed VRAM");
            result.Reasons.Add($"Total savings: ~{totalSavings / (1024.0 * 1024.0):F1} MB, still short by ~{(vramDeficit - totalSavings) / (1024.0 * 1024.0):F1} MB");
            result.Reasons.Add($"Applied: {string.Join(", ", appliedOptions)}");
            result.Reasons.Add("Generation may fail or use system RAM swap - consider a smaller model or resolution");
        }
    }

    /// <summary>Builds a list of available VRAM-saving options with their estimated savings and performance impacts.</summary>
    private static List<VramSavingOption> BuildVramSavingOptions(long diffusionSize, long vaeSize,
        Dictionary<string, long> encoderSizes, long controlNetSize, long resolutionOverhead)
    {
        List<VramSavingOption> options = [];

        // VAE Tiling - reduces peak VRAM during VAE decode, minimal performance impact
        // Savings: Reduces peak VAE memory from ~4x to ~1.5x of VAE size during decode
        // This mainly helps with resolution overhead during decode phase
        long vaeTilingSavings = (long)(resolutionOverhead * 0.6); // ~60% reduction in resolution overhead during VAE
        if (vaeTilingSavings > 50 * 1024 * 1024) // Only worth it if saving >50MB
        {
            options.Add(new VramSavingOption
            {
                Name = "VAE Tiling",
                FlagName = "vae_tiling",
                EstimatedSavingsBytes = vaeTilingSavings,
                PerformanceImpact = 1, // Minimal impact
                ApplyFlag = r => r.VaeTiling = true,
                IsAlreadyApplied = r => r.VaeTiling
            });
        }

        // VAE on CPU - offloads entire VAE to CPU
        // Savings: Full VAE size * runtime factor
        if (vaeSize > 0)
        {
            options.Add(new VramSavingOption
            {
                Name = "VAE on CPU",
                FlagName = "vae_on_cpu",
                EstimatedSavingsBytes = (long)(vaeSize * RuntimeFactor),
                PerformanceImpact = 2, // Moderate impact - VAE runs at end anyway
                ApplyFlag = r => r.VaeOnCpu = true,
                IsAlreadyApplied = r => r.VaeOnCpu
            });
        }

        // CLIP on CPU - offloads CLIP encoders (clip_l, clip_g) to CPU
        // Only applies to CLIP models, not T5/LLM which stay on GPU
        long clipSize = 0;
        foreach (KeyValuePair<string, long> kvp in encoderSizes)
        {
            if (kvp.Key.Contains("clip", StringComparison.OrdinalIgnoreCase))
            {
                clipSize += kvp.Value;
            }
        }
        if (clipSize > 0)
        {
            options.Add(new VramSavingOption
            {
                Name = "CLIP on CPU",
                FlagName = "clip_on_cpu",
                EstimatedSavingsBytes = (long)(clipSize * RuntimeFactor),
                PerformanceImpact = 2, // Moderate - encoding happens once at start
                ApplyFlag = r => r.ClipOnCpu = true,
                IsAlreadyApplied = r => r.ClipOnCpu
            });
        }

        // ControlNet on CPU - offloads ControlNet to CPU
        if (controlNetSize > 0)
        {
            options.Add(new VramSavingOption
            {
                Name = "ControlNet on CPU",
                FlagName = "control_net_cpu",
                EstimatedSavingsBytes = (long)(controlNetSize * RuntimeFactor),
                PerformanceImpact = 3, // Significant - ControlNet runs every step
                ApplyFlag = r => r.ControlNetOnCpu = true,
                IsAlreadyApplied = r => r.ControlNetOnCpu
            });
        }

        // Offload to CPU - keeps diffusion model weights in RAM, loads to VRAM per step
        // This is the nuclear option - significant performance impact but saves the most VRAM
        // Savings: Most of diffusion model size (kept in RAM, streamed as needed)
        if (diffusionSize > 0)
        {
            options.Add(new VramSavingOption
            {
                Name = "Diffusion Offload to CPU",
                FlagName = "offload_to_cpu",
                EstimatedSavingsBytes = (long)(diffusionSize * 0.7), // ~70% savings (some layers still need to be in VRAM)
                PerformanceImpact = 4, // Severe - memory transfers every step
                ApplyFlag = r => r.OffloadToCpu = true,
                IsAlreadyApplied = r => r.OffloadToCpu
            });
        }

        return options;
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
        SetFlag("control_net_cpu", policyResult.ControlNetOnCpu);

        // Log what was applied
        if (policyResult.AppliedFlags.Count > 0)
        {
            Logs.Info($"[SDcpp VRAM] Applied flags: {string.Join(", ", policyResult.AppliedFlags)}");
        }
        else
        {
            Logs.Debug("[SDcpp VRAM] No offload flags needed");
        }
    }
}
