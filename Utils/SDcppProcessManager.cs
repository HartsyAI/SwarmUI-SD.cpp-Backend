using Hartsy.Extensions.SDcppExtension.Config;
using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>
/// Manages the lifecycle of SD.cpp CLI processes, handling command-line argument construction,
/// process execution, output capture, and cleanup. Used by SDcppBackend to execute generation requests.
/// </summary>
public class SDcppProcessManager : IDisposable
{
    public Process Process;
    public readonly SDcppSettings Settings;
    public readonly string WorkingDirectory;
    public bool Disposed = false;

    public SDcppProcessManager(SDcppSettings settings)
    {
        Settings = settings;
        WorkingDirectory = string.IsNullOrEmpty(settings.WorkingDirectory) 
            ? Path.GetTempPath() 
            : settings.WorkingDirectory;
        
        // Ensure working directory exists
        Directory.CreateDirectory(WorkingDirectory);
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

    /// <summary>
    /// Constructs SD.cpp command-line arguments from generation parameters.
    /// Handles model paths, generation settings, memory optimization flags, and input images.
    /// </summary>
    /// <param name="parameters">Dictionary containing generation parameters like prompt, model, dimensions, etc.</param>
    /// <returns>Complete command-line argument string ready for process execution</returns>
    public string BuildCommandLine(Dictionary<string, object> parameters)
    {
        List<string> args = [];

        if (Settings.Threads > 0)
            args.Add($"--threads {Settings.Threads}");

        if (!string.IsNullOrEmpty(Settings.WeightType))
            args.Add($"--type {Settings.WeightType}");
        if (parameters.TryGetValue("model", out var model) && !string.IsNullOrEmpty(model.ToString()))
            args.Add($"--model \"{model}\"");
        else if (!string.IsNullOrEmpty(Settings.DefaultModelPath))
            args.Add($"--model \"{Settings.DefaultModelPath}\"");

        if (parameters.TryGetValue("vae", out var vae) && !string.IsNullOrEmpty(vae.ToString()))
            args.Add($"--vae \"{vae}\"");
        else if (!string.IsNullOrEmpty(Settings.DefaultVAEPath))
            args.Add($"--vae \"{Settings.DefaultVAEPath}\"");


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


        if (Settings.VAETiling)
            args.Add("--vae-tiling");

        if (Settings.VAEOnCPU)
            args.Add("--vae-on-cpu");

        if (Settings.CLIPOnCPU)
            args.Add("--clip-on-cpu");

        if (Settings.FlashAttention)
            args.Add("--diffusion-fa");


        if (parameters.TryGetValue("init_img", out var initImg) && !string.IsNullOrEmpty(initImg.ToString()))
            args.Add($"--init-img \"{initImg}\"");

        if (parameters.TryGetValue("strength", out var strength))
            args.Add($"--strength {strength}");


        if (Settings.DebugMode)
            args.Add("--verbose");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Executes SD.cpp with the provided parameters, capturing output and handling timeouts.
    /// Manages the full process lifecycle from start to completion, including error handling and cleanup.
    /// </summary>
    /// <param name="parameters">Generation parameters to pass to SD.cpp</param>
    /// <returns>Tuple containing success status, stdout output, and stderr output</returns>
    public async Task<(bool Success, string Output, string Error)> ExecuteAsync(Dictionary<string, object> parameters)
    {
        if (!ValidateExecutable())
            return (false, "", "SD.cpp executable validation failed");

        string commandLine = BuildCommandLine(parameters);
        
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
