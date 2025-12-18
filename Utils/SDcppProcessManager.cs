using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using static Hartsy.Extensions.SDcppExtension.SwarmBackends.SDcppBackend;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>
/// Manages the lifecycle of SD.cpp CLI processes, handling command-line argument construction,
/// process execution, output capture, and cleanup. Used by SDcppBackend to execute generation requests.
/// </summary>
public class SDcppProcessManager : IDisposable
{
    public Process Process;
    public readonly SDcppBackendSettings Settings;
    public readonly string WorkingDirectory;
    public bool Disposed = false;

    /// <summary>Required CUDA version for SD.cpp CUDA builds</summary>
    private const string REQUIRED_CUDA_VERSION = "12";

    public SDcppProcessManager(SDcppBackendSettings settings)
    {
        Settings = settings;
        WorkingDirectory = string.IsNullOrEmpty(settings.WorkingDirectory)
            ? Path.GetTempPath()
            : settings.WorkingDirectory;

        // Ensure working directory exists
        Directory.CreateDirectory(WorkingDirectory);
    }

    /// <summary>
    /// Gets detailed system information for debugging purposes.
    /// Includes GPU info, CUDA version, driver version, and OS details.
    /// </summary>
    public static string GetSystemDebugInfo()
    {
        StringBuilder sb = new();
        sb.AppendLine("=== SD.cpp System Debug Info ===");

        // OS Info
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET Runtime: {RuntimeInformation.FrameworkDescription}");

        // NVIDIA GPU Info via SwarmUI's NvidiaUtil
        try
        {
            var nvidiaInfo = NvidiaUtil.QueryNvidia();
            if (nvidiaInfo != null && nvidiaInfo.Length > 0)
            {
                sb.AppendLine($"\n--- NVIDIA GPU(s) Detected ---");
                foreach (var gpu in nvidiaInfo)
                {
                    sb.AppendLine($"  GPU {gpu.ID}: {gpu.GPUName}");
                    sb.AppendLine($"    Driver Version: {gpu.DriverVersion}");
                    sb.AppendLine($"    Total Memory: {gpu.TotalMemory}");
                    sb.AppendLine($"    Free Memory: {gpu.FreeMemory}");
                    sb.AppendLine($"    Temperature: {gpu.Temperature}°C");
                }
            }
            else
            {
                sb.AppendLine("\n--- No NVIDIA GPU detected via nvidia-smi ---");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\n--- Failed to query NVIDIA GPU: {ex.Message} ---");
        }

        // CUDA Installation Info
        sb.AppendLine($"\n--- CUDA Installation ---");
        var (cudaVersion, cudaPath) = DetectInstalledCudaVersion();
        if (!string.IsNullOrEmpty(cudaVersion))
        {
            sb.AppendLine($"  Installed CUDA Version: {cudaVersion}");
            sb.AppendLine($"  CUDA Path: {cudaPath}");

            // Check if it matches required version
            if (!cudaVersion.StartsWith(REQUIRED_CUDA_VERSION))
            {
                sb.AppendLine($"  ⚠️ WARNING: SD.cpp CUDA build requires CUDA {REQUIRED_CUDA_VERSION}.x");
                sb.AppendLine($"     Your installed version ({cudaVersion}) may not be compatible.");
            }
            else
            {
                sb.AppendLine($"  ✓ CUDA version is compatible with SD.cpp");
            }
        }
        else
        {
            sb.AppendLine("  No CUDA installation detected");
            sb.AppendLine($"  Required: CUDA {REQUIRED_CUDA_VERSION}.x runtime");
        }

        // Check CUDA_PATH environment variable
        string cudaPathEnv = Environment.GetEnvironmentVariable("CUDA_PATH");
        sb.AppendLine($"\n--- Environment Variables ---");
        sb.AppendLine($"  CUDA_PATH: {cudaPathEnv ?? "(not set)"}");

        // Check for specific CUDA DLLs that SD.cpp needs
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sb.AppendLine($"\n--- CUDA Runtime DLL Check ---");
            string[] criticalDlls = ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];
            string systemPath = Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (string dll in criticalDlls)
            {
                bool found = false;
                // Check in CUDA bin path
                if (!string.IsNullOrEmpty(cudaPath))
                {
                    string dllPath = Path.Combine(cudaPath, "bin", dll);
                    if (File.Exists(dllPath))
                    {
                        sb.AppendLine($"  ✓ {dll} found at: {dllPath}");
                        found = true;
                    }
                }
                // Also check System32
                string system32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll);
                if (File.Exists(system32Path))
                {
                    sb.AppendLine($"  ✓ {dll} found at: {system32Path}");
                    found = true;
                }

                if (!found)
                {
                    sb.AppendLine($"  ✗ {dll} NOT FOUND - this is required for CUDA {REQUIRED_CUDA_VERSION}");
                }
            }
        }

        sb.AppendLine("================================");
        return sb.ToString();
    }

    /// <summary>
    /// Detects the installed CUDA version by checking environment variables and common install paths.
    /// </summary>
    /// <returns>Tuple of (version string, installation path) or (null, null) if not found</returns>
    public static (string Version, string Path) DetectInstalledCudaVersion()
    {
        try
        {
            // Method 1: Check CUDA_PATH environment variable
            string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath) && Directory.Exists(cudaPath))
            {
                string version = ExtractCudaVersionFromPath(cudaPath);
                if (!string.IsNullOrEmpty(version))
                {
                    return (version, cudaPath);
                }
            }

            // Method 2: Run nvcc --version if available
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "nvcc",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using Process proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);

                    // Parse version from output like "Cuda compilation tools, release 12.2, V12.2.140"
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"release\s+(\d+\.\d+)");
                    if (match.Success)
                    {
                        return (match.Groups[1].Value, cudaPath ?? "nvcc in PATH");
                    }
                }
            }
            catch
            {
                // nvcc not in PATH, continue to other methods
            }

            // Method 3: Check common installation directories on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");

                if (Directory.Exists(cudaRoot))
                {
                    // Find the most recent CUDA version directory
                    var versionDirs = Directory.GetDirectories(cudaRoot)
                        .Select(d => new { Path = d, Version = ExtractCudaVersionFromPath(d) })
                        .Where(x => !string.IsNullOrEmpty(x.Version))
                        .OrderByDescending(x => x.Version)
                        .ToList();

                    if (versionDirs.Count > 0)
                    {
                        return (versionDirs[0].Version, versionDirs[0].Path);
                    }
                }
            }

            return (null, null);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[SDcpp] Error detecting CUDA version: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Extracts CUDA version number from a path like "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.2"
    /// </summary>
    private static string ExtractCudaVersionFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Look for version pattern like "v12.2" or "12.2" in the path
        var match = System.Text.RegularExpressions.Regex.Match(path, @"v?(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Attempts to find the CUDA Toolkit installation directory and returns the bin path.
    /// Checks common installation locations and environment variables.
    /// </summary>
    /// <returns>CUDA bin directory path if found, null otherwise</returns>
    private static string FindCudaBinDirectory()
    {
        try
        {
            // Check CUDA_PATH environment variable (set by CUDA installer)
            string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                string binPath = Path.Combine(cudaPath, "bin");
                if (Directory.Exists(binPath))
                {
                    Logs.Debug($"[SDcpp] Found CUDA bin directory via CUDA_PATH: {binPath}");
                    return binPath;
                }
            }

            // Check common installation directories on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");

                if (Directory.Exists(cudaRoot))
                {
                    // Find the most recent CUDA version directory
                    var versionDirs = Directory.GetDirectories(cudaRoot)
                        .OrderByDescending(d => d)
                        .ToList();

                    foreach (string versionDir in versionDirs)
                    {
                        string binPath = Path.Combine(versionDir, "bin");
                        if (Directory.Exists(binPath))
                        {
                            Logs.Debug($"[SDcpp] Found CUDA bin directory: {binPath}");
                            return binPath;
                        }
                    }
                }
            }
            // On Linux, CUDA is typically in /usr/local/cuda/bin or /opt/cuda/bin
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string[] linuxPaths =
                [
                    "/usr/local/cuda/bin",
                    "/opt/cuda/bin"
                ];

                foreach (string path in linuxPaths)
                {
                    if (Directory.Exists(path))
                    {
                        Logs.Debug($"[SDcpp] Found CUDA bin directory: {path}");
                        return path;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error while searching for CUDA installation: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Validates that the SD.cpp executable exists at the configured path and is accessible.
    /// Called during backend initialization to ensure the backend can function properly.
    /// </summary>
    /// <returns>True if executable exists and is accessible, false otherwise</returns>
    public bool ValidateExecutable()
    {
        try
        {
            if (string.IsNullOrEmpty(Settings.ExecutablePath))
            {
                Logs.Error("[SDcpp] Executable path not configured");
                return false;
            }

            if (!File.Exists(Settings.ExecutablePath))
            {
                Logs.Error($"[SDcpp] Executable not found at: {Settings.ExecutablePath}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error validating executable: {ex.Message}");
            return false;
        }
    }

    public bool ValidateRuntime(out string errorMessage)
    {
        errorMessage = null;
        bool isCuda = Settings.Device.ToLowerInvariant() == "cuda";
        const string CudaDownloadUrl = "https://developer.nvidia.com/cuda-12-6-0-download-archive";

        try
        {
            if (!ValidateExecutable())
            {
                errorMessage = "SD.cpp executable is missing or not configured.";
                return false;
            }

            // Log system info upfront for CUDA to help diagnose issues
            if (isCuda)
            {
                var (cudaVersion, cudaPath) = DetectInstalledCudaVersion();
                Logs.Info($"[SDcpp] Validating CUDA runtime for SD.cpp...");
                Logs.Info($"[SDcpp] Detected CUDA version: {cudaVersion ?? "NOT FOUND"}");
                Logs.Info($"[SDcpp] CUDA path: {cudaPath ?? "NOT FOUND"}");
                Logs.Info($"[SDcpp] Required CUDA version: {REQUIRED_CUDA_VERSION}.x");

                if (string.IsNullOrEmpty(cudaVersion))
                {
                    Logs.Warning($"[SDcpp] No CUDA installation detected. SD.cpp CUDA build requires CUDA {REQUIRED_CUDA_VERSION} runtime.");
                }
                else if (!cudaVersion.StartsWith(REQUIRED_CUDA_VERSION))
                {
                    Logs.Warning($"[SDcpp] CUDA version mismatch: installed {cudaVersion}, required {REQUIRED_CUDA_VERSION}.x");
                }
            }

            // For validation, we just need to verify the binary can START (DLLs present)
            // We don't need it to complete - CUDA initialization can take a very long time
            // Use --help which should print quickly, but we only wait a short time
            ProcessStartInfo psi = new()
            {
                FileName = Settings.ExecutablePath,
                Arguments = "--help",
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Add CUDA bin directory to PATH for CUDA device to find runtime DLLs
            if (isCuda)
            {
                string cudaBinPath = FindCudaBinDirectory();
                if (!string.IsNullOrEmpty(cudaBinPath))
                {
                    string currentPath = psi.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
                    psi.EnvironmentVariables["PATH"] = $"{cudaBinPath};{currentPath}";
                    Logs.Debug($"[SDcpp] Added CUDA bin to PATH for validation: {cudaBinPath}");
                }
                else
                {
                    Logs.Warning("[SDcpp] Could not find CUDA bin directory to add to PATH");
                }
            }

            try
            {
                using Process testProcess = Process.Start(psi);
                if (testProcess == null)
                {
                    errorMessage = "Failed to start SD.cpp test process.";
                    return false;
                }

                StringBuilder stdout = new();
                StringBuilder stderr = new();

                testProcess.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        stdout.AppendLine(e.Data);
                    }
                };

                testProcess.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        stderr.AppendLine(e.Data);
                    }
                };

                testProcess.BeginOutputReadLine();
                testProcess.BeginErrorReadLine();

                // Wait a short time for the process to either:
                // 1. Exit quickly (with success or error)
                // 2. Start producing output (proving it loaded successfully)
                // 3. Crash immediately with a DLL error
                // For CUDA, we give it more time since GPU init can be slow
                int quickCheckMs = isCuda ? 5000 : 2000;
                bool exited = testProcess.WaitForExit(quickCheckMs);

                if (!exited)
                {
                    // Process is still running after quick check
                    // For CUDA, if it's still running, that means DLLs loaded successfully
                    // The process might just be slow to initialize CUDA - that's OK
                    if (isCuda)
                    {
                        Logs.Info("[SDcpp] CUDA binary started successfully (process is initializing)");
                        Logs.Debug("[SDcpp] Killing validation process - we've confirmed it can start");
                        try { testProcess.Kill(); } catch { }
                        Logs.Info("[SDcpp] Runtime validation successful - CUDA runtime is available");
                        return true;
                    }
                    
                    // For non-CUDA, wait longer
                    exited = testProcess.WaitForExit(13000); // Total 15 seconds
                    if (!exited)
                    {
                        try { testProcess.Kill(); } catch { }
                        errorMessage = "SD.cpp test process did not exit in time (possible driver/runtime issue).";
                        return false;
                    }
                }

                int code = testProcess.ExitCode;
                string stderrText = stderr.ToString().Trim();

                if (code != 0)
                {
                    // Log detailed system info on failure
                    Logs.Error($"[SDcpp] Runtime validation failed with exit code {code} (0x{code:X8})");
                    if (!string.IsNullOrEmpty(stderrText))
                    {
                        Logs.Error($"[SDcpp] Error output: {stderrText}");
                    }

                    // Log full debug info
                    string debugInfo = GetSystemDebugInfo();
                    foreach (string line in debugInfo.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Logs.Info($"[SDcpp] {line.TrimEnd()}");
                        }
                    }

                    // Exit code -1073741515 (0xC0000135) = STATUS_DLL_NOT_FOUND
                    if (code == -1073741515)
                    {
                        if (isCuda)
                        {
                            var (installedVersion, _) = DetectInstalledCudaVersion();
                            errorMessage = BuildCudaErrorMessage(installedVersion, CudaDownloadUrl);
                        }
                        else
                        {
                            errorMessage = $"SD.cpp binary failed to start (missing DLL error - 0xC0000135).\n\n" +
                                $"This usually means you need to install:\n" +
                                $"Microsoft Visual C++ Redistributable (2015-2022)\n" +
                                $"Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                                $"After installing, restart SwarmUI.";
                            Logs.Info("[SDcpp] Download Visual C++ Redistributable: https://aka.ms/vs/17/release/vc_redist.x64.exe");
                        }
                    }
                    else if (isCuda)
                    {
                        var (installedVersion, _) = DetectInstalledCudaVersion();
                        errorMessage = BuildCudaErrorMessage(installedVersion, CudaDownloadUrl);
                    }
                    else
                    {
                        errorMessage = $"SD.cpp returned non-zero exit code {code} during startup test.\nError output: {stderrText}";
                    }
                    return false;
                }

                Logs.Info("[SDcpp] Runtime validation successful");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Log full debug info on Win32 exception
                Logs.Error($"[SDcpp] Win32 exception during runtime validation: {ex.Message}");
                string debugInfo = GetSystemDebugInfo();
                foreach (string line in debugInfo.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Logs.Info($"[SDcpp] {line.TrimEnd()}");
                    }
                }

                if (isCuda)
                {
                    var (installedVersion, _) = DetectInstalledCudaVersion();
                    errorMessage = BuildCudaErrorMessage(installedVersion, CudaDownloadUrl);
                }
                else
                {
                    errorMessage = $"Failed to launch SD.cpp executable: {ex.Message}";
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Unexpected error during SD.cpp runtime validation: {ex.Message}";
            Logs.Error($"[SDcpp] Unexpected error during runtime validation: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Builds a detailed error message for CUDA runtime issues.
    /// </summary>
    private static string BuildCudaErrorMessage(string installedVersion, string downloadUrl)
    {
        StringBuilder sb = new();
        sb.AppendLine("SD.cpp CUDA binary failed to start due to missing or incompatible CUDA runtime.");
        sb.AppendLine();

        if (string.IsNullOrEmpty(installedVersion))
        {
            sb.AppendLine($"❌ No CUDA installation detected on your system.");
            sb.AppendLine($"   SD.cpp requires CUDA {REQUIRED_CUDA_VERSION}.x runtime libraries.");
        }
        else if (!installedVersion.StartsWith(REQUIRED_CUDA_VERSION))
        {
            sb.AppendLine($"❌ CUDA version mismatch detected:");
            sb.AppendLine($"   • Installed: CUDA {installedVersion}");
            sb.AppendLine($"   • Required:  CUDA {REQUIRED_CUDA_VERSION}.x");
            sb.AppendLine();
            sb.AppendLine($"   The SD.cpp binary was compiled for CUDA {REQUIRED_CUDA_VERSION} and requires");
            sb.AppendLine($"   the matching runtime DLLs (cudart64_12.dll, cublas64_12.dll, etc.).");
        }
        else
        {
            sb.AppendLine($"⚠️ CUDA {installedVersion} detected but runtime DLLs may not be in PATH.");
            sb.AppendLine($"   Try adding the CUDA bin directory to your system PATH.");
        }

        sb.AppendLine();
        sb.AppendLine("To fix this, choose one of these options:");
        sb.AppendLine();
        sb.AppendLine($"  1. Install CUDA {REQUIRED_CUDA_VERSION} Toolkit (includes runtime):");
        sb.AppendLine($"     {downloadUrl}");
        sb.AppendLine();
        sb.AppendLine("  2. Switch to CPU mode:");
        sb.AppendLine("     Change 'Device' to 'CPU (Universal)' in backend settings.");
        sb.AppendLine("     (Slower but works without CUDA installation)");

        Logs.Info($"[SDcpp] CUDA {REQUIRED_CUDA_VERSION} download page: {downloadUrl}");

        return sb.ToString();
    }

    /// <summary>
    /// Constructs SD.cpp command-line arguments from generation parameters.
    /// Handles model paths, generation settings, memory optimization flags, and input images.
    /// Supports both standard SD models and Flux models with their multi-component architecture.
    /// </summary>
    /// <param name="parameters">Dictionary containing generation parameters like prompt, model, dimensions, etc.</param>
    /// <param name="isFluxModel">Whether this is a Flux model (uses different parameter names)</param>
    /// <returns>Complete command-line argument string ready for process execution</returns>
    public string BuildCommandLine(Dictionary<string, object> parameters, bool isFluxModel = false)
    {
        List<string> args = [];

        if (Settings.Threads > 0)
            args.Add($"--threads {Settings.Threads}");

        // Per-request runtime toggles (provided by SDcppBackend.BuildGenerationParameters).
        // These are not backend settings.
        bool vaeTiling = parameters.TryGetValue("vae_tiling", out object vaeTilingRaw) && vaeTilingRaw is bool vaeTilingVal && vaeTilingVal;
        bool vaeOnCpu = parameters.TryGetValue("vae_on_cpu", out object vaeOnCpuRaw) && vaeOnCpuRaw is bool vaeOnCpuVal && vaeOnCpuVal;
        bool clipOnCpu = parameters.TryGetValue("clip_on_cpu", out object clipOnCpuRaw) && clipOnCpuRaw is bool clipOnCpuVal && clipOnCpuVal;
        bool flashAttention = parameters.TryGetValue("flash_attention", out object flashAttnRaw) && flashAttnRaw is bool flashAttnVal && flashAttnVal;

        // Handle model type based on model type
        bool isMultiComponent = parameters.ContainsKey("diffusion_model");
        if (isMultiComponent && isFluxModel)
        {
            // For Flux GGUF models, don't specify --type - let SD.cpp use the GGUF's built-in quantization
            // The diffusion model already has quantization baked in (Q2_K, Q4_K, Q8_0, etc.)
            // Only use --type for non-GGUF models or if we need to force a specific compute type
            
            // Check if it's a GGUF model (quantization is embedded)
            bool isGgufModel = parameters.TryGetValue("diffusion_model", out var dm) && 
                               dm?.ToString()?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true;
            
            if (!isGgufModel)
            {
                // Non-GGUF Flux models (safetensors) need explicit type
                args.Add("--type f16");
            }
            // For GGUF models, omit --type entirely to use the model's native quantization
        }

        // Force CPU usage if device is set to CPU to avoid Vulkan memory issues
        if (Settings.Device.ToLowerInvariant() == "cpu")
        {
            // These flags force CPU usage and prevent GPU acceleration
            args.Add("--vae-on-cpu");
            args.Add("--clip-on-cpu");
            // Enable VAE tiling to reduce memory usage on CPU
            args.Add("--vae-tiling");
        }

        // Multi-component models (Flux, SD3) use diffusion_model parameter
        // Standard models use model parameter
        if (parameters.ContainsKey("diffusion_model"))
        {
            // Multi-component architecture (Flux, SD3, etc.)
            if (parameters.TryGetValue("diffusion_model", out var diffusionModel) && !string.IsNullOrEmpty(diffusionModel.ToString()))
                args.Add($"--diffusion-model \"{diffusionModel}\"");

            // CLIP-G - Required for SD3
            if (parameters.TryGetValue("clip_g", out var clipG) && !string.IsNullOrEmpty(clipG.ToString()))
                args.Add($"--clip_g \"{clipG}\"");

            // CLIP-L - Required for Flux and SD3
            if (parameters.TryGetValue("clip_l", out var clipL) && !string.IsNullOrEmpty(clipL.ToString()))
                args.Add($"--clip_l \"{clipL}\"");

            // T5-XXL - Required for Flux and SD3
            if (parameters.TryGetValue("t5xxl", out var t5xxl) && !string.IsNullOrEmpty(t5xxl.ToString()))
                args.Add($"--t5xxl \"{t5xxl}\"");

            // VAE - Required for Flux, optional for SD3
            if (parameters.TryGetValue("vae", out var multiVae) && !string.IsNullOrEmpty(multiVae.ToString()))
                args.Add($"--vae \"{multiVae}\"");
        }
        else
        {
            // Standard SD model parameters (SD 1.5, SD 2.x, SDXL, etc.)
            if (parameters.TryGetValue("model", out var model) && !string.IsNullOrEmpty(model.ToString()))
                args.Add($"--model \"{model}\"");

            if (parameters.TryGetValue("vae", out var vae) && !string.IsNullOrEmpty(vae.ToString()))
                args.Add($"--vae \"{vae}\"");
        }


        if (parameters.TryGetValue("prompt", out var prompt))
            args.Add($"--prompt \"{prompt}\"");

        if (parameters.TryGetValue("negative_prompt", out var negPrompt) && !string.IsNullOrEmpty(negPrompt.ToString()))
            args.Add($"--negative-prompt \"{negPrompt}\"");

        if (parameters.TryGetValue("width", out var width))
            args.Add($"--width {width}");

        if (parameters.TryGetValue("height", out var height))
            args.Add($"--height {height}");

        if (parameters.TryGetValue("steps", out var steps))
            args.Add($"--steps {steps}");

        if (parameters.TryGetValue("cfg_scale", out var cfgScale))
            args.Add($"--cfg-scale {cfgScale}");

        if (parameters.TryGetValue("seed", out var seed))
            args.Add($"--seed {seed}");

        if (parameters.TryGetValue("sampling_method", out var sampler))
            args.Add($"--sampling-method {sampler}");

        if (parameters.TryGetValue("output", out var output))
            args.Add($"--output \"{output}\"");


        // Apply per-request runtime toggles only if not using CPU device (to avoid duplicates)
        if (Settings.Device.ToLowerInvariant() != "cpu")
        {
            if (vaeTiling)
                args.Add("--vae-tiling");

            if (vaeOnCpu)
                args.Add("--vae-on-cpu");

            if (clipOnCpu)
                args.Add("--clip-on-cpu");
        }

        if (flashAttention)
            args.Add("--diffusion-fa");


        if (parameters.TryGetValue("init_img", out var initImg) && !string.IsNullOrEmpty(initImg.ToString()))
            args.Add($"--init-img \"{initImg}\"");

        if (parameters.TryGetValue("strength", out var strength))
            args.Add($"--strength {strength}");

        // LoRA support
        if (parameters.TryGetValue("lora_model_dir", out var loraDir) && !string.IsNullOrEmpty(loraDir.ToString()))
            args.Add($"--lora-model-dir \"{loraDir}\"");

        if (Settings.DebugMode)
            args.Add("--verbose");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Executes SD.cpp with the provided parameters, capturing output and handling timeouts.
    /// Manages the full process lifecycle from start to completion, including error handling and cleanup.
    /// </summary>
    /// <param name="parameters">Generation parameters to pass to SD.cpp</param>
    /// <param name="isFluxModel">Whether this is a Flux model</param>
    /// <returns>Tuple containing success status, stdout output, and stderr output</returns>
    public async Task<(bool Success, string Output, string Error)> ExecuteAsync(Dictionary<string, object> parameters, bool isFluxModel = false)
    {
        if (!ValidateExecutable())
            return (false, "", "SD.cpp executable validation failed");

        string commandLine = BuildCommandLine(parameters, isFluxModel);
        
        try
        {
            ProcessStartInfo processInfo = new()
            {
                FileName = Settings.ExecutablePath,
                Arguments = commandLine,
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Force CPU-only mode by disabling GPU backends via environment variables
            if (Settings.Device.ToLowerInvariant() == "cpu")
            {
                processInfo.EnvironmentVariables["GGML_USE_VULKAN"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_CUDA"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_METAL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_OPENCL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_SYCL"] = "0";
                if (Settings.DebugMode)
                {
                    Logs.Debug("[SDcpp] Forcing CPU-only mode via environment variables");
                }
            }
            // Add CUDA bin directory to PATH for CUDA device to find runtime DLLs
            else if (Settings.Device.ToLowerInvariant() == "cuda")
            {
                string cudaBinPath = FindCudaBinDirectory();
                if (!string.IsNullOrEmpty(cudaBinPath))
                {
                    string currentPath = processInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
                    processInfo.EnvironmentVariables["PATH"] = $"{cudaBinPath};{currentPath}";
                    if (Settings.DebugMode)
                    {
                        Logs.Debug($"[SDcpp] Added CUDA bin to PATH: {cudaBinPath}");
                    }
                }
                else
                {
                    Logs.Warning("[SDcpp] CUDA device selected but CUDA Toolkit installation not found. SD.cpp may fail if CUDA runtime DLLs are not in PATH.");
                }
            }

            if (Settings.DebugMode)
            {
                Logs.Debug($"[SDcpp] Executing: {Settings.ExecutablePath} {commandLine}");
                Logs.Debug($"[SDcpp] Working directory: {WorkingDirectory}");
            }

            Process = Process.Start(processInfo);
            if (Process == null)
                return (false, "", "Failed to start SD.cpp process");

            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();

            Task outputTask = Task.Run(async () =>
            {
                while (!Process.StandardOutput.EndOfStream)
                {
                    string line = await Process.StandardOutput.ReadLineAsync();
                    if (line != null)
                    {
                        outputBuilder.AppendLine(line);
                        if (Settings.DebugMode)
                            Logs.Debug($"[SDcpp] Output: {line}");
                    }
                }
            });

            Task errorTask = Task.Run(async () =>
            {
                while (!Process.StandardError.EndOfStream)
                {
                    string line = await Process.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        errorBuilder.AppendLine(line);
                        if (Settings.DebugMode)
                            Logs.Debug($"[SDcpp] Error: {line}");
                    }
                }
            });

            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(Settings.ProcessTimeoutSeconds));
            Task processTask = Task.Run(() => Process.WaitForExit());

            Task completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logs.Warning($"[SDcpp] Process timed out after {Settings.ProcessTimeoutSeconds} seconds");
                Process.Kill();
                return (false, outputBuilder.ToString(), "Process timed out");
            }

            await Task.WhenAll(outputTask, errorTask);

            bool success = Process.ExitCode == 0;
            string output = outputBuilder.ToString();
            string error = errorBuilder.ToString();

            if (Settings.DebugMode)
                Logs.Debug($"[SDcpp] Process completed with exit code: {Process.ExitCode}");

            return (success, output, error);
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error executing process: {ex.Message}");
            return (false, "", ex.Message);
        }
        finally
        {
            Process?.Dispose();
            Process = null;
        }
    }

    /// <summary>
    /// Executes SD.cpp with live progress updates via callback.
    /// </summary>
    /// <param name="parameters">Generation parameters to pass to SD.cpp</param>
    /// <param name="isFluxModel">Whether this is a Flux model</param>
    /// <param name="progressCallback">Callback for progress updates (0.0 to 1.0)</param>
    /// <returns>Tuple containing success status, stdout output, and stderr output</returns>
    public async Task<(bool Success, string Output, string Error)> ExecuteWithProgressAsync(
        Dictionary<string, object> parameters,
        bool isFluxModel,
        Action<float> progressCallback)
    {
        if (!ValidateExecutable())
            return (false, "", "SD.cpp executable validation failed");

        string commandLine = BuildCommandLine(parameters, isFluxModel);
        Logs.Info($"[SDcpp] Executing SD.cpp with command: {Settings.ExecutablePath} {commandLine}");

        try
        {
            ProcessStartInfo processInfo = new()
            {
                FileName = Settings.ExecutablePath,
                Arguments = commandLine,
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Force CPU-only mode by disabling GPU backends via environment variables
            if (Settings.Device.ToLowerInvariant() == "cpu")
            {
                processInfo.EnvironmentVariables["GGML_USE_VULKAN"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_CUDA"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_METAL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_OPENCL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_SYCL"] = "0";
            }
            // Add CUDA bin directory to PATH for CUDA device to find runtime DLLs
            else if (Settings.Device.ToLowerInvariant() == "cuda")
            {
                string cudaBinPath = FindCudaBinDirectory();
                if (!string.IsNullOrEmpty(cudaBinPath))
                {
                    string currentPath = processInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
                    processInfo.EnvironmentVariables["PATH"] = $"{cudaBinPath};{currentPath}";
                    if (Settings.DebugMode)
                    {
                        Logs.Debug($"[SDcpp] Added CUDA bin to PATH: {cudaBinPath}");
                    }
                }
                else
                {
                    Logs.Warning("[SDcpp] CUDA device selected but CUDA Toolkit installation not found. SD.cpp may fail if CUDA runtime DLLs are not in PATH.");
                }
            }

            if (Settings.DebugMode)
            {
                Logs.Debug($"[SDcpp] Executing: {Settings.ExecutablePath} {commandLine}");
            }

            Process = Process.Start(processInfo);
            if (Process == null)
                return (false, "", "Failed to start SD.cpp process");

            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();

            // Parse progress from stdout
            Task outputTask = Task.Run(async () =>
            {
                while (!Process.StandardOutput.EndOfStream)
                {
                    string line = await Process.StandardOutput.ReadLineAsync();
                    if (line != null)
                    {
                        outputBuilder.AppendLine(line);

                        // Parse progress: "Step X/Y" or "sampling: X/Y"
                        if (line.Contains("Step ") || line.Contains("sampling"))
                        {
                            try
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)/(\d+)");
                                if (match.Success && int.TryParse(match.Groups[1].Value, out int current) && int.TryParse(match.Groups[2].Value, out int total))
                                {
                                    float progress = (float)current / total;
                                    progressCallback?.Invoke(progress);
                                }
                            }
                            catch { /* Ignore parse errors */ }
                        }

                        if (Settings.DebugMode)
                            Logs.Debug($"[SDcpp] Output: {line}");
                    }
                }
            });

            Task errorTask = Task.Run(async () =>
            {
                while (!Process.StandardError.EndOfStream)
                {
                    string line = await Process.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        errorBuilder.AppendLine(line);
                        if (Settings.DebugMode)
                            Logs.Debug($"[SDcpp] Error: {line}");
                    }
                }
            });

            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(Settings.ProcessTimeoutSeconds));
            Task processTask = Task.Run(() => Process.WaitForExit());

            Task completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logs.Warning($"[SDcpp] Process timed out after {Settings.ProcessTimeoutSeconds} seconds");
                Process.Kill();
                return (false, outputBuilder.ToString(), "Process timed out");
            }

            await Task.WhenAll(outputTask, errorTask);

            // Final progress update
            progressCallback?.Invoke(1.0f);

            bool success = Process.ExitCode == 0;
            string output = outputBuilder.ToString();
            string error = errorBuilder.ToString();

            if (Settings.DebugMode)
                Logs.Debug($"[SDcpp] Process completed with exit code: {Process.ExitCode}");

            return (success, output, error);
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error executing process: {ex.Message}");
            return (false, "", ex.Message);
        }
        finally
        {
            Process?.Dispose();
            Process = null;
        }
    }

    /// <summary>
    /// Determines if the SD.cpp process is currently active and has not exited.
    /// Used to check process state before attempting operations or cleanup.
    /// </summary>
    /// <returns>True if process exists and is running, false if null or has exited</returns>
    public bool IsProcessRunning()
    {
        return Process != null && !Process.HasExited;
    }

    /// <summary>
    /// Forcibly terminates the SD.cpp process if it's currently running.
    /// Used for cleanup during shutdown or when a process needs to be cancelled.
    /// Waits up to 5 seconds for graceful exit before forcing termination.
    /// </summary>
    public void KillProcess()
    {
        try
        {
            if (Process != null && !Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000); // Wait up to 5 seconds for graceful exit
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error killing process: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!Disposed)
        {
            KillProcess();
            Process?.Dispose();
            Disposed = true;
        }
    }
}
