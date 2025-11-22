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
        try
        {
            const string CudaDownloadUrl = "https://developer.nvidia.com/cuda-downloads";
            if (!ValidateExecutable())
            {
                errorMessage = "SD.cpp executable is missing or not configured.";
                return false;
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

            // Add CUDA bin directory to PATH for CUDA device to find runtime DLLs
            if (Settings.Device.ToLowerInvariant() == "cuda")
            {
                string cudaBinPath = FindCudaBinDirectory();
                if (!string.IsNullOrEmpty(cudaBinPath))
                {
                    string currentPath = psi.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
                    psi.EnvironmentVariables["PATH"] = $"{cudaBinPath};{currentPath}";
                    Logs.Debug($"[SDcpp] Added CUDA bin to PATH for validation: {cudaBinPath}");
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

                bool exited = testProcess.WaitForExit(15000);
                if (!exited)
                {
                    try
                    {
                        testProcess.Kill();
                    }
                    catch
                    {
                    }
                    errorMessage = "SD.cpp test process did not exit in time (possible driver/runtime issue).";
                    return false;
                }

                int code = testProcess.ExitCode;
                string stderrText = stderr.ToString().Trim();

                if (code != 0)
                {
                    // Exit code -1073741515 (0xC0000135) = STATUS_DLL_NOT_FOUND
                    if (code == -1073741515)
                    {
                        if (Settings.Device.ToLowerInvariant() == "cuda")
                        {
                            errorMessage = $"SD.cpp CUDA binary failed to start (missing DLL error). This is likely due to missing CUDA runtime DLLs.\n\n" +
                                $"To fix this:\n" +
                                $"1. Install CUDA 12 runtime from: {CudaDownloadUrl}\n" +
                                $"   OR\n" +
                                $"2. Change SD.cpp backend device to 'CPU (Universal)' in backend settings\n\n" +
                                $"The CUDA runtime is separate from the CUDA Toolkit - even if you have the Toolkit installed, you may need the runtime.";
                            Logs.Info($"[SDcpp] CUDA runtime download page: {CudaDownloadUrl}");
                        }
                        else
                        {
                            errorMessage = $"SD.cpp binary failed to start (missing DLL error). This usually means you need to install:\n\n" +
                                $"Microsoft Visual C++ Redistributable (2015-2022)\n" +
                                $"Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                                $"After installing, restart SwarmUI.";
                            Logs.Error("[SDcpp] Missing Visual C++ Runtime - SD.cpp binary cannot run without it.");
                            Logs.Info("[SDcpp] Download Visual C++ Redistributable: https://aka.ms/vs/17/release/vc_redist.x64.exe");
                        }
                    }
                    else
                    {
                        string lowerErr = stderrText.ToLowerInvariant();
                        if (Settings.Device.ToLowerInvariant() == "cuda")
                        {
                            errorMessage = "Failed to start SD.cpp CUDA binary. This usually means the required CUDA runtime libraries for this build are not installed. Please install the matching CUDA runtime (for example CUDA 12 runtime on Windows for the current SD.cpp build) or switch the SD.cpp backend device to CPU or Vulkan. See NVIDIA CUDA downloads: " + CudaDownloadUrl + ".";
                            Logs.Info($"[SDcpp] CUDA runtime download page: {CudaDownloadUrl}");
                        }
                        else
                        {
                            errorMessage = $"SD.cpp returned non-zero exit code {code} during startup test. Error output: {stderrText}";
                        }
                    }
                    Logs.Error($"[SDcpp] Runtime validation failed with exit code {code}. Error: {stderrText}");
                    return false;
                }

                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (Settings.Device.ToLowerInvariant() == "cuda")
                {
                    errorMessage = "Failed to launch SD.cpp CUDA executable. This usually indicates missing CUDA runtime DLLs. Please install the appropriate CUDA runtime for this SD.cpp build (for example CUDA 12 runtime on Windows) and restart SwarmUI, or change the SD.cpp backend device to CPU or Vulkan. See NVIDIA CUDA downloads: " + CudaDownloadUrl + ".";
                    Logs.Info($"[SDcpp] CUDA runtime download page: {CudaDownloadUrl}");
                }
                else
                {
                    errorMessage = $"Failed to launch SD.cpp executable: {ex.Message}";
                }
                Logs.Error($"[SDcpp] Runtime validation error while starting SD.cpp: {ex}");
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

        if (!string.IsNullOrEmpty(Settings.WeightType))
            args.Add($"--type {Settings.WeightType}");

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


        // Apply individual settings only if not using CPU device (to avoid duplicates)
        if (Settings.Device.ToLowerInvariant() != "cpu")
        {
            if (Settings.VAETiling)
                args.Add("--vae-tiling");

            if (Settings.VAEOnCPU)
                args.Add("--vae-on-cpu");

            if (Settings.CLIPOnCPU)
                args.Add("--clip-on-cpu");
        }

        if (Settings.FlashAttention)
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
