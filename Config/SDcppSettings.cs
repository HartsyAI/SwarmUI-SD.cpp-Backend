using FreneticUtilities.FreneticDataSyntax;
using SwarmUI.Core;

namespace Hartsy.Extensions.SDcppExtension.Config;

/// <summary>
/// Configuration settings for the SD.cpp backend. SD.cpp will be automatically downloaded
/// and configured based on your device selection.
/// </summary>
public class SDcppSettings : AutoConfiguration
{
    [ConfigComment("Which compute device to use for image generation.\n'CPU' uses your processor (slower but works on any system).\n'GPU (CUDA)' uses NVIDIA graphics cards with CUDA support.\n'GPU (Vulkan)' uses any modern graphics card with Vulkan support.")]
    [ManualSettingsOptions(Impl = null, Vals = ["cpu", "cuda", "vulkan"], ManualNames = ["CPU (Universal)", "GPU (CUDA - NVIDIA)", "GPU (Vulkan - Any GPU)"])]
    public string Device = "cpu";

    [ConfigComment("Number of CPU threads to use during generation (0 for auto-detect based on your CPU).")]
    public int Threads = 0;

    [ConfigComment("Model precision type. Lower precision uses less memory but may reduce quality.\n'f16' is recommended for most users.\n'q4_0' and 'q8_0' are quantized versions that use less memory.")]
    [ManualSettingsOptions(Impl = null, Vals = ["f32", "f16", "q8_0", "q4_0"], ManualNames = ["f32 (Highest Quality)", "f16 (Recommended)", "q8_0 (Lower Memory)", "q4_0 (Lowest Memory)"])]
    public string WeightType = "f16";

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

    // Internal settings - not exposed to user
    internal string ExecutablePath = "";
    internal string WorkingDirectory = "";
}
