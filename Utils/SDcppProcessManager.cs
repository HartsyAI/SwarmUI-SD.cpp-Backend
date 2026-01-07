using SwarmUI.Utils;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using static Hartsy.Extensions.SDcppExtension.SwarmBackends.SDcppBackend;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Manages the lifecycle of SD.cpp CLI processes, handling command-line argument construction, process execution, output capture, and cleanup. Used by SDcppBackend to execute generation requests.</summary>
public class SDcppProcessManager : IDisposable
{
    public Process Process;
    public readonly SDcppBackendSettings Settings;
    public readonly string WorkingDirectory;
    public bool Disposed = false;

    private const string REQUIRED_CUDA_VERSION = "12";

    public SDcppProcessManager(SDcppBackendSettings settings)
    {
        Settings = settings;
        WorkingDirectory = string.IsNullOrEmpty(settings.WorkingDirectory)
            ? Path.GetTempPath()
            : settings.WorkingDirectory;
        Directory.CreateDirectory(WorkingDirectory);
    }

    /// <summary>Gets detailed system information for debugging purposes. Includes GPU info, CUDA version, driver version, and OS details.</summary>
    public static string GetSystemDebugInfo()
    {
        StringBuilder sb = new();
        sb.AppendLine("=== SD.cpp System Debug Info ===");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine("\n--- NVIDIA GPU(s) Detected ---");
        NvidiaUtil.NvidiaInfo[] nvidiaInfo = NvidiaUtil.QueryNvidia();
        if (nvidiaInfo != null && nvidiaInfo.Length > 0)
        {
            foreach (NvidiaUtil.NvidiaInfo gpu in nvidiaInfo)
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
            sb.AppendLine("  No NVIDIA GPU detected via nvidia-smi");
        }
        sb.AppendLine($"\n--- CUDA Installation ---");
        (string cudaVersion, string cudaPath) = DetectInstalledCudaVersion();
        if (!string.IsNullOrEmpty(cudaVersion))
        {
            sb.AppendLine($"  Installed CUDA Version: {cudaVersion}");
            sb.AppendLine($"  CUDA Path: {cudaPath}");
            if (!cudaVersion.StartsWith(REQUIRED_CUDA_VERSION))
            {
                sb.AppendLine($"  ⚠️ WARNING: SD.cpp CUDA build requires CUDA {REQUIRED_CUDA_VERSION}.x");
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
        string cudaPathEnv = Environment.GetEnvironmentVariable("CUDA_PATH");
        sb.AppendLine($"\n--- Environment Variables ---");
        sb.AppendLine($"  CUDA_PATH: {cudaPathEnv ?? "(not set)"}");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sb.AppendLine($"\n--- CUDA Runtime DLL Check ---");
            string[] criticalDlls = ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];
            string systemPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dll in criticalDlls)
            {
                bool found = false;
                if (!string.IsNullOrEmpty(cudaPath))
                {
                    string dllPath = Path.Combine(cudaPath, "bin", dll);
                    if (File.Exists(dllPath))
                    {
                        sb.AppendLine($"  ✓ {dll} found at: {dllPath}");
                        found = true;
                    }
                }
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

    /// <summary>Detects the installed CUDA version by checking environment variables and common install paths.</summary>
    /// <returns>Tuple of (version string, installation path) or (null, null) if not found</returns>
    public static (string Version, string Path) DetectInstalledCudaVersion()
    {
        try
        {
            string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath) && Directory.Exists(cudaPath))
            {
                string version = ExtractCudaVersionFromPath(cudaPath);
                if (!string.IsNullOrEmpty(version))
                {
                    return (version, cudaPath);
                }
            }
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
                    System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(output, @"release\s+(\d+\.\d+)");
                    if (match.Success)
                    {
                        return (match.Groups[1].Value, cudaPath ?? "nvcc in PATH");
                    }
                }
            }
            catch
            {
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
                if (Directory.Exists(cudaRoot))
                {
                    List<VersionDir> versionDirs = Directory.GetDirectories(cudaRoot)
                        .Select(d => new VersionDir { Path = d, Version = ExtractCudaVersionFromPath(d) })
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

    /// <summary>Extracts CUDA version number from a path like "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.2"</summary>
    public static string ExtractCudaVersionFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(path, @"v?(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Attempts to find the CUDA Toolkit installation directory and returns the bin path. Checks common installation locations and environment variables.</summary>
    public static string FindCudaBinDirectory()
    {
        try
        {
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
                if (Directory.Exists(cudaRoot))
                {
                    List<string> versionDirs = Directory.GetDirectories(cudaRoot)
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
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string[] defaultLinuxBins =
                [
                    "/usr/local/cuda/bin",
                    "/opt/cuda/bin"
                ];
                foreach (string path in defaultLinuxBins)
                {
                    if (Directory.Exists(path))
                    {
                        Logs.Debug($"[SDcpp] Found CUDA bin directory: {path}");
                        return path;
                    }
                }
                string binFromVersioned = FindCudaBinFromVersionedDirs(new[] { "/usr/local", "/opt" });
                if (!string.IsNullOrEmpty(binFromVersioned))
                {
                    Logs.Debug($"[SDcpp] Found CUDA bin directory from versioned install: {binFromVersioned}");
                    return binFromVersioned;
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

    private static string FindCudaBinFromVersionedDirs(string[] roots)
    {
        List<VersionDir> versionDirs = [];
        foreach (string root in roots.Where(r => Directory.Exists(r)))
        {
            foreach (string dir in Directory.GetDirectories(root))
            {
                string version = ExtractCudaVersionFromPath(dir);
                if (!string.IsNullOrEmpty(version))
                {
                    versionDirs.Add(new VersionDir { Path = dir, Version = version });
                }
                else if (Path.GetFileName(dir).StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
                {
                    string binPath = Path.Combine(dir, "bin");
                    if (Directory.Exists(binPath))
                    {
                        return binPath;
                    }
                }
            }
        }

        VersionDir best = versionDirs
            .OrderByDescending(v => ParseVersion(v.Version))
            .FirstOrDefault();
        if (best is not null)
        {
            string binPath = Path.Combine(best.Path, "bin");
            if (Directory.Exists(binPath))
            {
                return binPath;
            }
        }
        return null;
    }

    private static double ParseVersion(string version)
    {
        if (double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }
        return 0;
    }

    /// <summary>Validates that the SD.cpp executable exists at the configured path and is accessible. Called during backend initialization to ensure the backend can function properly.</summary>
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

    /// <summary>Validates runtime environment for SD.cpp execution, checking for CUDA compatibility and DLL availability.</summary>
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
            if (isCuda)
            {
                (string cudaVersion, string cudaPath) = DetectInstalledCudaVersion();
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
                    if (int.TryParse(cudaVersion.Split('.')[0], out int majorVersion) && majorVersion >= 13)
                    {
                        Logs.Info($"[SDcpp] CUDA {cudaVersion} detected. Backward compatible with CUDA {REQUIRED_CUDA_VERSION}.x binaries.");
                    }
                    else
                    {
                        Logs.Warning($"[SDcpp] CUDA version mismatch: installed {cudaVersion}, required {REQUIRED_CUDA_VERSION}.x or higher");
                    }
                }
            }
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
                int quickCheckMs = isCuda ? 5000 : 2000;
                bool exited = testProcess.WaitForExit(quickCheckMs);
                if (!exited)
                {
                    if (isCuda)
                    {
                        Logs.Info("[SDcpp] CUDA binary started successfully (process is initializing)");
                        Logs.Debug("[SDcpp] Killing validation process - we've confirmed it can start");
                        try { testProcess.Kill(); } catch { }
                        Logs.Info("[SDcpp] Runtime validation successful - CUDA runtime is available");
                        return true;
                    }
                    exited = testProcess.WaitForExit(13000);
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
                    Logs.Error($"[SDcpp] Runtime validation failed with exit code {code} (0x{code:X8})");
                    if (!string.IsNullOrEmpty(stderrText))
                    {
                        Logs.Error($"[SDcpp] Error output: {stderrText}");
                    }
                    string debugInfo = GetSystemDebugInfo();
                    foreach (string line in debugInfo.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Logs.Info($"[SDcpp] {line.TrimEnd()}");
                        }
                    }
                    if (code == -1073741515)
                    {
                        if (isCuda)
                        {
                            (string installedVersion, string _) = DetectInstalledCudaVersion();
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
                        (string installedVersion, string _) = DetectInstalledCudaVersion();
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
                    (string installedVersion, string _) = DetectInstalledCudaVersion();
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

    /// <summary>Builds a detailed error message for CUDA runtime issues.</summary>
    public static string BuildCudaErrorMessage(string installedVersion, string downloadUrl)
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

    /// <summary>Constructs SD.cpp command-line arguments from generation parameters. Handles model paths, generation settings, memory optimization flags, and input images. Supports both standard SD models and Flux models with their multi-component architecture.</summary>
    /// <param name="parameters">Dictionary containing generation parameters like prompt, model, dimensions, etc.</param>
    /// <param name="isFluxModel">Whether this is a Flux model (uses different parameter names)</param>
    /// <returns>Complete command-line argument string ready for process execution</returns>
    public string BuildCommandLine(Dictionary<string, object> parameters, bool isFluxModel = false)
    {
        List<string> args = [];
        if (Settings.Threads > 0)
            args.Add($"--threads {Settings.Threads}");
        object enablePreview;
        if (parameters.TryGetValue("enable_preview", out enablePreview) && enablePreview is bool previewEnabled && previewEnabled)
        {
            args.Add("--preview tae");
            args.Add("--preview-interval 1");
            object previewPath;
            if (parameters.TryGetValue("preview_path", out previewPath) && !string.IsNullOrEmpty(previewPath.ToString()))
            {
                args.Add($"--preview-path \"{previewPath}\"");
            }
        }
        bool vaeTiling = parameters.TryGetValue("vae_tiling", out object vaeTilingRaw) && vaeTilingRaw is bool vaeTilingVal && vaeTilingVal;
        bool vaeOnCpu = parameters.TryGetValue("vae_on_cpu", out object vaeOnCpuRaw) && vaeOnCpuRaw is bool vaeOnCpuVal && vaeOnCpuVal;
        bool clipOnCpu = parameters.TryGetValue("clip_on_cpu", out object clipOnCpuRaw) && clipOnCpuRaw is bool clipOnCpuVal && clipOnCpuVal;
        bool flashAttention = parameters.TryGetValue("flash_attention", out object flashAttnRaw) && flashAttnRaw is bool flashAttnVal && flashAttnVal;
        bool diffusionConvDirect = parameters.TryGetValue("diffusion_conv_direct", out object diffConvRaw) && diffConvRaw is bool diffConvVal && diffConvVal;
        bool offloadToCpu = parameters.TryGetValue("offload_to_cpu", out object offloadRaw) && offloadRaw is bool offloadVal && offloadVal;
        if (Settings.Device.ToLowerInvariant() == "cpu")
        {
            args.Add("--vae-on-cpu");
            args.Add("--clip-on-cpu");
            args.Add("--vae-tiling");
        }
        bool isMultiComponent = parameters.ContainsKey("diffusion_model");
        if (isMultiComponent && isFluxModel)
        {
            bool isGgufModel = parameters.TryGetValue("diffusion_model", out object dm) && 
                               dm?.ToString()?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true;
            if (!isGgufModel)
            {
                args.Add("--type f16");
            }
        }
        if (parameters.ContainsKey("diffusion_model"))
        {
            object diffusionModel;
            if (parameters.TryGetValue("diffusion_model", out diffusionModel) && !string.IsNullOrEmpty(diffusionModel.ToString()))
                args.Add($"--diffusion-model \"{diffusionModel}\"");
            object clipG;
            if (parameters.TryGetValue("clip_g", out clipG) && !string.IsNullOrEmpty(clipG.ToString()))
                args.Add($"--clip_g \"{clipG}\"");
            object clipL;
            if (parameters.TryGetValue("clip_l", out clipL) && !string.IsNullOrEmpty(clipL.ToString()))
                args.Add($"--clip_l \"{clipL}\"");
            object t5xxl;
            if (parameters.TryGetValue("t5xxl", out t5xxl) && !string.IsNullOrEmpty(t5xxl.ToString()))
                args.Add($"--t5xxl \"{t5xxl}\"");
            object multiVae;
            if (parameters.TryGetValue("vae", out multiVae) && !string.IsNullOrEmpty(multiVae.ToString()))
                args.Add($"--vae \"{multiVae}\"");
        }
        else
        {
            object model;
            if (parameters.TryGetValue("model", out model) && !string.IsNullOrEmpty(model.ToString()))
                args.Add($"--model \"{model}\"");
            object vae;
            if (parameters.TryGetValue("vae", out vae) && !string.IsNullOrEmpty(vae.ToString()))
                args.Add($"--vae \"{vae}\"");
        }
        object prompt;
        if (parameters.TryGetValue("prompt", out prompt))
            args.Add($"--prompt \"{prompt}\"");
        object negPrompt;
        if (parameters.TryGetValue("negative_prompt", out negPrompt) && !string.IsNullOrEmpty(negPrompt.ToString()))
            args.Add($"--negative-prompt \"{negPrompt}\"");
        object width;
        if (parameters.TryGetValue("width", out width))
            args.Add($"--width {width}");
        object height;
        if (parameters.TryGetValue("height", out height))
            args.Add($"--height {height}");
        object steps;
        if (parameters.TryGetValue("steps", out steps))
            args.Add($"--steps {steps}");
        object cfgScale;
        if (parameters.TryGetValue("cfg_scale", out cfgScale))
            args.Add($"--cfg-scale {cfgScale}");
        object seed;
        if (parameters.TryGetValue("seed", out seed))
            args.Add($"--seed {seed}");
        object sampler;
        if (parameters.TryGetValue("sampling_method", out sampler))
            args.Add($"--sampling-method {sampler}");
        object scheduler;
        if (parameters.TryGetValue("scheduler", out scheduler))
            args.Add($"--scheduler {scheduler}");
        object clipSkip;
        if (parameters.TryGetValue("clip_skip", out clipSkip))
            args.Add($"--clip-skip {clipSkip}");
        object batchCount;
        if (parameters.TryGetValue("batch_count", out batchCount))
            args.Add($"--batch-count {batchCount}");
        object output;
        if (parameters.TryGetValue("output", out output))
            args.Add($"--output \"{output}\"");
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
        if (diffusionConvDirect)
            args.Add("--diffusion-conv-direct");
        if (offloadToCpu)
            args.Add("--offload-to-cpu");
        object mmap;
        if (parameters.TryGetValue("mmap", out mmap) && mmap is bool mmapVal && mmapVal)
            args.Add("--mmap");
        object vaeConvDirect;
        if (parameters.TryGetValue("vae_conv_direct", out vaeConvDirect) && vaeConvDirect is bool vaeConvVal && vaeConvVal)
            args.Add("--vae-conv-direct");
        object cacheMode;
        if (parameters.TryGetValue("cache_mode", out cacheMode) && !string.IsNullOrEmpty(cacheMode.ToString()))
        {
            args.Add($"--cache-mode {cacheMode}");
            object cacheOption;
            if (parameters.TryGetValue("cache_option", out cacheOption) && !string.IsNullOrEmpty(cacheOption.ToString()))
            {
                args.Add($"--cache-option \"{cacheOption}\"");
            }
            object cachePreset;
            if (parameters.TryGetValue("cache_preset", out cachePreset) && !string.IsNullOrEmpty(cachePreset.ToString()))
            {
                args.Add($"--cache-preset {cachePreset}");
            }
        }
        object initImg;
        if (parameters.TryGetValue("init_img", out initImg) && !string.IsNullOrEmpty(initImg.ToString()))
            args.Add($"--init-img \"{initImg}\"");
        object strength;
        if (parameters.TryGetValue("strength", out strength))
            args.Add($"--strength {strength}");
        object mask;
        if (parameters.TryGetValue("mask", out mask) && !string.IsNullOrEmpty(mask.ToString()))
            args.Add($"--mask \"{mask}\"");
        object controlNet;
        if (parameters.TryGetValue("control_net", out controlNet) && !string.IsNullOrEmpty(controlNet.ToString()))
            args.Add($"--control-net \"{controlNet}\"");
        object controlImage;
        if (parameters.TryGetValue("control_image", out controlImage) && !string.IsNullOrEmpty(controlImage.ToString()))
            args.Add($"--control-image \"{controlImage}\"");
        object controlStrength;
        if (parameters.TryGetValue("control_strength", out controlStrength))
            args.Add($"--control-strength {controlStrength}");
        object guidance;
        if (parameters.TryGetValue("guidance", out guidance))
            args.Add($"--guidance {guidance}");
        object taesd;
        if (parameters.TryGetValue("taesd", out taesd) && !string.IsNullOrEmpty(taesd.ToString()))
            args.Add($"--taesd \"{taesd}\"");
        object upscaleModel;
        if (parameters.TryGetValue("upscale_model", out upscaleModel) && !string.IsNullOrEmpty(upscaleModel.ToString()))
            args.Add($"--upscale-model \"{upscaleModel}\"");
        object upscaleRepeats;
        if (parameters.TryGetValue("upscale_repeats", out upscaleRepeats))
            args.Add($"--upscale-repeats {upscaleRepeats}");
        object color;
        if (parameters.TryGetValue("color", out color) && color.ToString().ToLowerInvariant() == "true")
            args.Add("--color");
        object videoFrames;
        if (parameters.TryGetValue("video_frames", out videoFrames))
        {
            args.Add("-M vid_gen");
            args.Add($"--video-frames {videoFrames}");
        }
        object videoFPS;
        if (parameters.TryGetValue("video_fps", out videoFPS))
            args.Add($"--video-fps {videoFPS}");
        object flowShift;
        if (parameters.TryGetValue("flow_shift", out flowShift))
            args.Add($"--flow-shift {flowShift}");
        object highNoiseModel;
        if (parameters.TryGetValue("high_noise_diffusion_model", out highNoiseModel) && !string.IsNullOrEmpty(highNoiseModel.ToString()))
            args.Add($"--high-noise-diffusion-model \"{highNoiseModel}\"");
        object videoSwapPercent;
        if (parameters.TryGetValue("video_swap_percent", out videoSwapPercent))
            args.Add($"--video-swap-percent {videoSwapPercent}");
        object loraDir;
        if (parameters.TryGetValue("lora_model_dir", out loraDir) && !string.IsNullOrEmpty(loraDir.ToString()))
            args.Add($"--lora-model-dir \"{loraDir}\"");
        if (Settings.DebugMode)
            args.Add("--verbose");
        return string.Join(" ", args);
    }

    /// <summary>Executes SD.cpp with the provided parameters, capturing output and handling timeouts. Manages the full process lifecycle from start to completion, including error handling and cleanup.</summary>
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

    /// <summary>Executes SD.cpp with live progress updates via callback.</summary>
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
            if (Settings.Device.ToLowerInvariant() == "cpu")
            {
                processInfo.EnvironmentVariables["GGML_USE_VULKAN"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_CUDA"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_METAL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_OPENCL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_SYCL"] = "0";
            }
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
            Task outputTask = Task.Run(async () =>
            {
                while (!Process.StandardOutput.EndOfStream)
                {
                    string line = await Process.StandardOutput.ReadLineAsync();
                    if (line != null)
                    {
                        outputBuilder.AppendLine(line);
                        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)/(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int current) && int.TryParse(match.Groups[2].Value, out int total))
                        {
                            float progress = (float)current / total;
                            progressCallback?.Invoke(progress);
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

    /// <summary>Determines if the SD.cpp process is currently active and has not exited. Used to check process state before attempting operations or cleanup.</summary>
    /// <returns>True if process exists and is running, false if null or has exited</returns>
    public bool IsProcessRunning()
    {
        return Process != null && !Process.HasExited;
    }

    /// <summary>Forcibly terminates the SD.cpp process if it's currently running. Used for cleanup during shutdown or when a process needs to be cancelled. Waits up to 5 seconds for graceful exit before forcing termination.</summary>
    public void KillProcess()
    {
        try
        {
            if (Process != null && !Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error killing process: {ex.Message}");
        }
    }

    /// <summary>Disposes the SDcppProcessManager, killing any running process and cleaning up resources.</summary>
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

public class VersionDir
{
    public string Path { get; set; }
    public string Version { get; set; }
}
