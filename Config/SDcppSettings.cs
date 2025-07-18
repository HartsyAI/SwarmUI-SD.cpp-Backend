using FreneticUtilities.FreneticDataSyntax;
using SwarmUI.Core;

namespace Hartsy.Extensions.SDcppExtension.Config;

/// <summary>
/// Configuration settings that control SD.cpp backend behavior, performance optimizations,
/// and CLI execution parameters. These settings are used to customize how the backend
/// interacts with the stable-diffusion.cpp executable.
/// </summary>
public class SDcppSettings : AutoConfiguration
{
    /// <summary>Full path to the stable-diffusion.cpp executable file</summary>
    [ConfigComment("Path to the stable-diffusion.cpp executable (sd.exe on Windows, sd on Linux/Mac)")]
    public string ExecutablePath = "sd.exe";

    /// <summary>Maximum number of threads to use</summary>
    [ConfigComment("Number of threads to use during computation (-1 for auto)")]
    public int Threads = -1;

    /// <summary>Default model path</summary>
    [ConfigComment("Default model path (can be overridden per generation)")]
    public string DefaultModelPath = "";

    /// <summary>Default VAE path</summary>
    [ConfigComment("Default VAE path (optional)")]
    public string DefaultVAEPath = "";

    /// <summary>Enable debug logging</summary>
    [ConfigComment("Enable debug logging for SD.cpp backend")]
    public bool DebugMode = false;

    /// <summary>GPU device to use</summary>
    [ConfigComment("GPU device to use (auto, cpu, cuda, metal, vulkan, opencl, sycl)")]
    public string Device = "auto";

    /// <summary>Precision type for model weights (f32, f16, q8_0, q4_0, q4_1, q5_0, q5_1, q2_k, q3_k, q4_k, q5_k, q6_k)</summary>
    [ConfigComment("Weight type (f32, f16, q8_0, q4_0, q4_1, q5_0, q5_1, q2_K, q3_K, q4_K)")]
    public string WeightType = "f16";

    /// <summary>Enable VAE tiling</summary>
    [ConfigComment("Enable VAE tiling to reduce memory usage")]
    public bool VAETiling = true;

    /// <summary>Keep VAE on CPU</summary>
    [ConfigComment("Keep VAE on CPU to reduce VRAM usage")]
    public bool VAEOnCPU = false;

    /// <summary>Keep CLIP on CPU</summary>
    [ConfigComment("Keep CLIP on CPU to reduce VRAM usage")]
    public bool CLIPOnCPU = false;

    /// <summary>Use Flash Attention</summary>
    [ConfigComment("Use Flash Attention in diffusion model (may reduce quality but saves VRAM)")]
    public bool FlashAttention = false;

    /// <summary>Process timeout in seconds</summary>
    [ConfigComment("Timeout for SD.cpp process operations in seconds")]
    public int ProcessTimeoutSeconds = 300;

    /// <summary>Directory where SD.cpp process will be executed and temporary files created</summary>
    [ConfigComment("Working directory for temporary files (empty for system temp)")]
    public string WorkingDirectory = "";
}
