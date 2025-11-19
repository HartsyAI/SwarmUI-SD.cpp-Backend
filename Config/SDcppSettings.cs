using FreneticUtilities.FreneticDataSyntax;
using SwarmUI.Core;

namespace Hartsy.Extensions.SDcppExtension.Config;

/// <summary>
/// Configuration settings for the SD.cpp backend. SD.cpp will be automatically downloaded
/// and configured based on your device selection.
/// </summary>
public class SDcppSettings : AutoConfiguration
{
    [ConfigComment("Which compute device to use for image generation.\n'CPU' uses your processor (slower but works on any system).\n'GPU (CUDA)' uses NVIDIA graphics cards with CUDA support.\n'GPU (Vulkan)' uses any modern graphics card with Vulkan support.\nNote: Flux models may not work reliably with Vulkan - use CUDA or CPU instead.")]
    [ManualSettingsOptions(Impl = null, Vals = ["cpu", "cuda", "vulkan"], ManualNames = ["CPU (Universal)", "GPU (CUDA - NVIDIA)", "GPU (Vulkan - Any GPU)"])]
    public string Device = "cpu";

    [ConfigComment("Number of CPU threads to use during generation (0 for auto-detect based on your CPU).")]
    public int Threads = 0;

    [ConfigComment("Model precision type for standard SD models. Lower precision uses less memory but may reduce quality.\nNote: Flux models require GGUF conversion - use FluxQuantization setting instead.")]
    [ManualSettingsOptions(Impl = null, Vals = ["f32", "f16", "q8_0", "q4_0"], ManualNames = ["f32 (Highest Quality)", "f16 (Recommended)", "q8_0 (Lower Memory)", "q4_0 (Lowest Memory)"])]
    public string WeightType = "f16";

    [ConfigComment("Quantization level for Flux models (GGUF format).\n'q8_0' provides best quality with ~12GB VRAM.\n'q4_0' is good for 6-8GB VRAM.\n'q2_k' can run on 4GB VRAM but with quality loss.")]
    [ManualSettingsOptions(Impl = null, Vals = ["q8_0", "q4_0", "q4_k", "q3_k", "q2_k"], ManualNames = ["q8_0 (Best Quality, 12GB VRAM)", "q4_0 (Balanced, 6-8GB VRAM)", "q4_k (Balanced Alt)", "q3_k (Low VRAM, 4-6GB)", "q2_k (Minimal VRAM, 4GB)"])]
    public string FluxQuantization = "q8_0";

    [ConfigComment("Automatically convert Flux models to GGUF format if needed.\nFlux models MUST be in GGUF format to work properly with SD.cpp.")]
    public bool AutoConvertFluxToGGUF = true;

    [ConfigComment("Default number of sampling steps for Flux-dev models (20+ recommended for quality).")]
    public int FluxDevSteps = 20;

    [ConfigComment("Default number of sampling steps for Flux-schnell models (4 recommended for speed).")]
    public int FluxSchnellSteps = 4;

    [ConfigComment("Enable VAE tiling to reduce memory usage. Recommended for systems with limited RAM/VRAM.")]
    public bool VAETiling = true;

    [ConfigComment("Keep VAE processing on CPU instead of GPU. Useful if you're running out of VRAM.")]
    public bool VAEOnCPU = false;

    [ConfigComment("Keep CLIP text encoder on CPU instead of GPU. Useful if you're running out of VRAM.")]
    public bool CLIPOnCPU = false;

    [ConfigComment("Enable Flash Attention optimization. May reduce quality slightly but saves memory.")]
    public bool FlashAttention = false;

    [ConfigComment("Maximum time to wait for image generation before timing out (in seconds).")]
    public int ProcessTimeoutSeconds = 600;

    [ConfigComment("Enable debug logging to help troubleshoot issues.")]
    public bool DebugMode = false;

    [ConfigComment("Optional: Override path to Flux VAE file (ae.safetensors). Leave empty for auto-detection.")]
    public string FluxVAEPath = "";

    [ConfigComment("Optional: Override path to Flux CLIP-L encoder (clip_l.safetensors). Leave empty for auto-detection.")]
    public string FluxCLIPLPath = "";

    [ConfigComment("Optional: Override path to Flux T5-XXL encoder (t5xxl_fp16.safetensors). Leave empty for auto-detection.")]
    public string FluxT5XXLPath = "";

    // Internal settings - not exposed to user
    internal string ExecutablePath = "";
    internal string WorkingDirectory = "";
}
