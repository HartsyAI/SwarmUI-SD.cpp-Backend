using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
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
    public bool PreviewArgsSupported { get; private set; } = false;
    public bool OutputArgSupported { get; private set; } = false;

    public SDcppProcessManager(SDcppBackendSettings settings)
    {
        Settings = settings;
        string exeDir = string.IsNullOrEmpty(settings.ExecutablePath) ? null : Path.GetDirectoryName(settings.ExecutablePath);
        WorkingDirectory = string.IsNullOrEmpty(exeDir) ? Path.GetTempPath() : exeDir;
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
        if (nvidiaInfo is not null && nvidiaInfo.Length > 0)
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
        sb.AppendLine("================================");
        return sb.ToString();
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
        bool isCuda = Settings.Device.Equals("cuda", StringComparison.InvariantCultureIgnoreCase);
        try
        {
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
            try
            {
                using Process testProcess = Process.Start(psi);
                if (testProcess is null)
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
                if (code is not 0)
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
                    errorMessage = $"SD.cpp returned non-zero exit code {code} during startup test.\nError output: {stderrText}";
                    return false;
                }
                string stdoutTextFinal = stdout.ToString();
                PreviewArgsSupported = stdoutTextFinal.Contains("--preview");
                OutputArgSupported = stdoutTextFinal.Contains("--output");
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
                errorMessage = $"Failed to launch SD.cpp executable: {ex.Message}";
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

    /// <summary>Constructs SD.cpp command-line arguments from generation parameters. Handles model paths, generation settings, memory optimization flags, and input images. Supports both standard SD models and Flux models with their multi-component architecture.</summary>
    /// <param name="parameters">Dictionary containing generation parameters like prompt, model, dimensions, etc.</param>
    /// <param name="isFluxModel">Whether this is a Flux model (uses different parameter names)</param>
    /// <returns>Complete command-line argument string ready for process execution</returns>
    public string BuildCommandLine(Dictionary<string, object> parameters, bool isFluxModel = false)
    {
        List<string> args = [];
        if (Settings.Threads > 0) args.Add($"--threads {Settings.Threads}");
        if (parameters.TryGetValue("enable_preview", out object enablePreview) && enablePreview is bool previewEnabled && previewEnabled)
        {
            if (PreviewArgsSupported)
            {
                // Per-job override: preview_method / preview_interval
                string previewMode = parameters.TryGetValue("preview_method", out object pmOverride) && !string.IsNullOrWhiteSpace(pmOverride?.ToString())
                    ? pmOverride.ToString()
                    : (parameters.TryGetValue("preview_mode", out object pm) ? pm?.ToString() ?? "tae" : "tae");
                args.Add($"--preview {previewMode}");

                int interval = 1;
                if (parameters.TryGetValue("preview_interval", out object intervalObj) && int.TryParse(intervalObj?.ToString(), out int parsedInterval) && parsedInterval > 0)
                {
                    interval = parsedInterval;
                }
                args.Add($"--preview-interval {interval}");

                if (parameters.TryGetValue("preview_path", out object previewPath) && !string.IsNullOrEmpty(previewPath.ToString()))
                {
                    args.Add($"--preview-path \"{previewPath}\"");
                }

                if (parameters.TryGetValue("preview_noisy", out object previewNoisy) && previewNoisy is bool pn && pn)
                {
                    args.Add("--preview-noisy");
                }

                if (parameters.TryGetValue("taesd_preview_only", out object taesdPreviewOnly) && taesdPreviewOnly is bool tpo && tpo)
                {
                    args.Add("--taesd-preview-only");
                }
            }
            else
            {
                Logs.Warning("[SDcpp] Preview requested but executable does not support --preview arguments");
            }
        }
        bool vaeTiling = parameters.TryGetValue("vae_tiling", out object vaeTilingRaw) && vaeTilingRaw is bool vaeTilingVal && vaeTilingVal;
        bool vaeOnCpu = parameters.TryGetValue("vae_on_cpu", out object vaeOnCpuRaw) && vaeOnCpuRaw is bool vaeOnCpuVal && vaeOnCpuVal;
        bool clipOnCpu = parameters.TryGetValue("clip_on_cpu", out object clipOnCpuRaw) && clipOnCpuRaw is bool clipOnCpuVal && clipOnCpuVal;
        bool controlNetOnCpu = parameters.TryGetValue("control_net_cpu", out object controlNetCpuRaw) && controlNetCpuRaw is bool controlNetCpuVal && controlNetCpuVal;
        bool flashAttention = parameters.TryGetValue("flash_attention", out object flashAttnRaw) && flashAttnRaw is bool flashAttnVal && flashAttnVal;
        bool diffusionConvDirect = parameters.TryGetValue("diffusion_conv_direct", out object diffConvRaw) && diffConvRaw is bool diffConvVal && diffConvVal;
        bool offloadToCpu = parameters.TryGetValue("offload_to_cpu", out object offloadRaw) && offloadRaw is bool offloadVal && offloadVal;
        if (Settings.Device.Equals("cpu", StringComparison.InvariantCultureIgnoreCase))
        {
            args.Add("--vae-on-cpu");
            args.Add("--clip-on-cpu");
            args.Add("--vae-tiling");
        }
        bool isMultiComponent = parameters.ContainsKey("diffusion_model");
        if (isMultiComponent && isFluxModel)
        {
            bool isGgufModel = parameters.TryGetValue("diffusion_model", out object dm) && dm?.ToString()?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) is true;
            if (!isGgufModel)
            {
                args.Add("--type f16");
            }
        }
        if (parameters.TryGetValue("type", out object weightType) && !string.IsNullOrEmpty(weightType?.ToString()))
        {
            args.Add($"--type {weightType}");
        }
        if (parameters.ContainsKey("diffusion_model"))
        {
            if (parameters.TryGetValue("diffusion_model", out object diffusionModel) && !string.IsNullOrEmpty(diffusionModel.ToString())) args.Add($"--diffusion-model \"{diffusionModel}\"");
            if (parameters.TryGetValue("clip_g", out object clipG) && !string.IsNullOrEmpty(clipG.ToString())) args.Add($"--clip_g \"{clipG}\"");
            if (parameters.TryGetValue("clip_l", out object clipL) && !string.IsNullOrEmpty(clipL.ToString())) args.Add($"--clip_l \"{clipL}\"");
            if (parameters.TryGetValue("t5xxl", out object t5xxl) && !string.IsNullOrEmpty(t5xxl.ToString())) args.Add($"--t5xxl \"{t5xxl}\"");
            if (parameters.TryGetValue("llm", out object llm) && !string.IsNullOrEmpty(llm.ToString())) args.Add($"--llm \"{llm}\"");
            if (parameters.TryGetValue("llm_vision", out object llmVision) && !string.IsNullOrEmpty(llmVision.ToString())) args.Add($"--llm_vision \"{llmVision}\"");
            if (parameters.TryGetValue("vae", out object multiVae) && !string.IsNullOrEmpty(multiVae.ToString())) args.Add($"--vae \"{multiVae}\"");
        }
        else
        {
            if (parameters.TryGetValue("model", out object model) && !string.IsNullOrEmpty(model.ToString())) args.Add($"--model \"{model}\"");
            if (parameters.TryGetValue("vae", out object vae) && !string.IsNullOrEmpty(vae.ToString())) args.Add($"--vae \"{vae}\"");
        }

        if (parameters.TryGetValue("clip_vision", out object clipVision) && !string.IsNullOrEmpty(clipVision.ToString())) args.Add($"--clip_vision \"{clipVision}\"");
        if (parameters.TryGetValue("embd_dir", out object embdDir) && !string.IsNullOrEmpty(embdDir.ToString())) args.Add($"--embd-dir \"{embdDir}\"");
        if (parameters.TryGetValue("tensor_type_rules", out object tensorTypeRules) && !string.IsNullOrEmpty(tensorTypeRules.ToString())) args.Add($"--tensor-type-rules \"{tensorTypeRules}\"");
        if (parameters.TryGetValue("lora_apply_mode", out object loraApplyMode) && !string.IsNullOrEmpty(loraApplyMode.ToString())) args.Add($"--lora-apply-mode {loraApplyMode}");
        if (parameters.TryGetValue("photo_maker", out object photoMaker) && !string.IsNullOrEmpty(photoMaker.ToString())) args.Add($"--photo-maker \"{photoMaker}\"");
        if (parameters.TryGetValue("pm_id_images_dir", out object pmIdImagesDir) && !string.IsNullOrEmpty(pmIdImagesDir.ToString())) args.Add($"--pm-id-images-dir \"{pmIdImagesDir}\"");
        if (parameters.TryGetValue("pm_id_embed_path", out object pmIdEmbedPath) && !string.IsNullOrEmpty(pmIdEmbedPath.ToString())) args.Add($"--pm-id-embed-path \"{pmIdEmbedPath}\"");
        if (parameters.TryGetValue("pm_style_strength", out object pmStyleStrength)) args.Add($"--pm-style-strength {pmStyleStrength}");
        if (parameters.TryGetValue("prompt", out object prompt)) args.Add($"--prompt \"{prompt}\"");
        if (parameters.TryGetValue("negative_prompt", out object negPrompt) && !string.IsNullOrEmpty(negPrompt.ToString())) args.Add($"--negative-prompt \"{negPrompt}\"");
        if (parameters.TryGetValue("width", out object width)) args.Add($"--width {width}");
        if (parameters.TryGetValue("height", out object height)) args.Add($"--height {height}");
        if (parameters.TryGetValue("steps", out object steps)) args.Add($"--steps {steps}");
        if (parameters.TryGetValue("cfg_scale", out object cfgScale)) args.Add($"--cfg-scale {cfgScale}");
        if (parameters.TryGetValue("seed", out object seed)) args.Add($"--seed {seed}");
        if (parameters.TryGetValue("sampling_method", out object sampler)) args.Add($"--sampling-method {sampler}");
        if (parameters.TryGetValue("scheduler", out object scheduler)) args.Add($"--scheduler {scheduler}");
        if (parameters.TryGetValue("clip_skip", out object clipSkip)) args.Add($"--clip-skip {clipSkip}");
        if (parameters.TryGetValue("batch_count", out object batchCount)) args.Add($"--batch-count {batchCount}");

        if (parameters.TryGetValue("rng", out object rng)) args.Add($"--rng {rng}");
        if (parameters.TryGetValue("sampler_rng", out object samplerRng)) args.Add($"--sampler-rng {samplerRng}");
        if (parameters.TryGetValue("prediction", out object prediction)) args.Add($"--prediction {prediction}");
        if (parameters.TryGetValue("eta", out object eta)) args.Add($"--eta {eta}");
        if (parameters.TryGetValue("sigmas", out object sigmas) && !string.IsNullOrEmpty(sigmas?.ToString())) args.Add($"--sigmas \"{sigmas}\"");

        if (parameters.TryGetValue("slg_scale", out object slgScale)) args.Add($"--slg-scale {slgScale}");
        if (parameters.TryGetValue("skip_layer_start", out object skipLayerStart)) args.Add($"--skip-layer-start {skipLayerStart}");
        if (parameters.TryGetValue("skip_layer_end", out object skipLayerEnd)) args.Add($"--skip-layer-end {skipLayerEnd}");
        if (parameters.TryGetValue("skip_layers", out object skipLayers) && !string.IsNullOrEmpty(skipLayers?.ToString())) args.Add($"--skip-layers \"{skipLayers}\"");
        if (parameters.TryGetValue("timestep_shift", out object timestepShift)) args.Add($"--timestep-shift {timestepShift}");
        if (parameters.TryGetValue("output", out object output) && OutputArgSupported)
        {
            args.Add($"--output \"{output}\"");
        }
        if (!Settings.Device.Equals("cpu", StringComparison.InvariantCultureIgnoreCase))
        {
            if (vaeTiling) args.Add("--vae-tiling");
            if (vaeOnCpu) args.Add("--vae-on-cpu");
            if (clipOnCpu) args.Add("--clip-on-cpu");
            if (controlNetOnCpu) args.Add("--control-net-cpu");
        }
        if (parameters.TryGetValue("vae_tile_size", out object vaeTileSize) && !string.IsNullOrEmpty(vaeTileSize.ToString())) args.Add($"--vae-tile-size {vaeTileSize}");
        if (parameters.TryGetValue("vae_relative_tile_size", out object vaeRelTileSize) && !string.IsNullOrEmpty(vaeRelTileSize.ToString())) args.Add($"--vae-relative-tile-size {vaeRelTileSize}");
        if (parameters.TryGetValue("vae_tile_overlap", out object vaeTileOverlap)) args.Add($"--vae-tile-overlap {vaeTileOverlap}");
        if (parameters.TryGetValue("force_sdxl_vae_conv_scale", out object forceConvScale) && forceConvScale is bool fcs && fcs) args.Add("--force-sdxl-vae-conv-scale");
        if (flashAttention) args.Add("--diffusion-fa");
        if (diffusionConvDirect) args.Add("--diffusion-conv-direct");
        if (offloadToCpu) args.Add("--offload-to-cpu");
        if (parameters.TryGetValue("mmap", out object mmap) && mmap is bool mmapVal && mmapVal) args.Add("--mmap");
        if (parameters.TryGetValue("vae_conv_direct", out object vaeConvDirect) && vaeConvDirect is bool vaeConvVal && vaeConvVal) args.Add("--vae-conv-direct");
        if (parameters.TryGetValue("cache_mode", out object cacheMode) && !string.IsNullOrEmpty(cacheMode.ToString()))
        {
            args.Add($"--cache-mode {cacheMode}");
            if (parameters.TryGetValue("cache_option", out object cacheOption) && !string.IsNullOrEmpty(cacheOption.ToString()))
            {
                args.Add($"--cache-option \"{cacheOption}\"");
            }
            if (parameters.TryGetValue("cache_preset", out object cachePreset) && !string.IsNullOrEmpty(cachePreset.ToString()))
            {
                args.Add($"--cache-preset {cachePreset}");
            }
            if (parameters.TryGetValue("scm_mask", out object scmMask) && !string.IsNullOrEmpty(scmMask.ToString()))
            {
                args.Add($"--scm-mask \"{scmMask}\"");
            }
            if (parameters.TryGetValue("scm_policy", out object scmPolicy) && !string.IsNullOrEmpty(scmPolicy.ToString()))
            {
                args.Add($"--scm-policy {scmPolicy}");
            }
        }
        if (parameters.TryGetValue("canny", out object canny) && canny is bool c && c) args.Add("--canny");
        if (parameters.TryGetValue("init_img", out object initImg) && !string.IsNullOrEmpty(initImg.ToString())) args.Add($"--init-img \"{initImg}\"");
        if (parameters.TryGetValue("end_img", out object endImg) && !string.IsNullOrEmpty(endImg.ToString())) args.Add($"--end-img \"{endImg}\"");
        if (parameters.TryGetValue("strength", out object strength)) args.Add($"--strength {strength}");
        if (parameters.TryGetValue("mask", out object mask) && !string.IsNullOrEmpty(mask.ToString())) args.Add($"--mask \"{mask}\"");
        if (parameters.TryGetValue("control_net", out object controlNet) && !string.IsNullOrEmpty(controlNet.ToString())) args.Add($"--control-net \"{controlNet}\"");
        if (parameters.TryGetValue("control_image", out object controlImage) && !string.IsNullOrEmpty(controlImage.ToString())) args.Add($"--control-image \"{controlImage}\"");
        if (parameters.TryGetValue("control_video", out object controlVideo) && !string.IsNullOrEmpty(controlVideo.ToString())) args.Add($"--control-video \"{controlVideo}\"");
        if (parameters.TryGetValue("control_strength", out object controlStrength)) args.Add($"--control-strength {controlStrength}");
        if (parameters.TryGetValue("guidance", out object guidance)) args.Add($"--guidance {guidance}");
        if (parameters.TryGetValue("taesd", out object taesd) && !string.IsNullOrEmpty(taesd.ToString())) args.Add($"--taesd \"{taesd}\"");
        if (parameters.TryGetValue("upscale_model", out object upscaleModel) && !string.IsNullOrEmpty(upscaleModel.ToString())) args.Add($"--upscale-model \"{upscaleModel}\"");
        if (parameters.TryGetValue("upscale_repeats", out object upscaleRepeats)) args.Add($"--upscale-repeats {upscaleRepeats}");
        if (parameters.TryGetValue("video_frames", out object videoFrames))
        {
            args.Add("-M vid_gen");
            args.Add($"--video-frames {videoFrames}");
        }
        if (parameters.TryGetValue("fps", out object videoFPS)) args.Add($"--fps {videoFPS}");
        if (parameters.TryGetValue("flow_shift", out object flowShift)) args.Add($"--flow-shift {flowShift}");
        if (parameters.TryGetValue("high_noise_diffusion_model", out object highNoiseModel) && !string.IsNullOrEmpty(highNoiseModel.ToString())) args.Add($"--high-noise-diffusion-model \"{highNoiseModel}\"");
        if (parameters.TryGetValue("moe_boundary", out object moeBoundary)) args.Add($"--moe-boundary {moeBoundary}");
        if (parameters.TryGetValue("vace_strength", out object vaceStrength)) args.Add($"--vace-strength {vaceStrength}");
        if (parameters.TryGetValue("video_swap_percent", out object videoSwapPercent)) args.Add($"--video-swap-percent {videoSwapPercent}");
        if (parameters.TryGetValue("lora_model_dir", out object loraDir) && !string.IsNullOrEmpty(loraDir.ToString())) args.Add($"--lora-model-dir \"{loraDir}\"");
        if (Settings.DebugMode) args.Add("--verbose");
        // Append any extra args from backend settings (at the end so they can override defaults)
        if (parameters.TryGetValue("extra_args", out object extraArgs) && !string.IsNullOrWhiteSpace(extraArgs?.ToString()))
        {
            args.Add(extraArgs.ToString());
        }
        return string.Join(" ", args);
    }

    /// <summary>Executes SD.cpp with the provided parameters, capturing output and handling timeouts. Manages the full process lifecycle from start to completion, including error handling and cleanup.</summary>
    /// <param name="parameters">Generation parameters to pass to SD.cpp</param>
    /// <param name="isFluxModel">Whether this is a Flux model</param>
    /// <returns>Tuple containing success status, stdout output, and stderr output</returns>
    public async Task<(bool Success, string Output, string Error)> ExecuteAsync(Dictionary<string, object> parameters, bool isFluxModel = false)
    {
        if (!ValidateExecutable()) return (false, "", "SD.cpp executable validation failed");
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
            if (Settings.Device.Equals("cpu", StringComparison.InvariantCultureIgnoreCase))
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
            if (Settings.DebugMode)
            {
                if (Settings.DebugMode) Logs.Debug($"[SDcpp] Executing: {Settings.ExecutablePath} {commandLine}");
                Logs.Debug($"[SDcpp] Working directory: {WorkingDirectory}");
            }
            Process = Process.Start(processInfo);
            if (Process is null) return (false, "", "Failed to start SD.cpp process");
            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();
            bool logSdcpp = Logs.MinimumLevel <= Logs.LogLevel.Debug;
            Task outputTask = Task.Run(async () =>
            {
                while (!Process.StandardOutput.EndOfStream)
                {
                    string line = await Process.StandardOutput.ReadLineAsync();
                    if (line is not null)
                    {
                        outputBuilder.AppendLine(line);
                        if (logSdcpp) Logs.Debug($"[SDcpp] Output: {line}");
                    }
                }
            });
            Task errorTask = Task.Run(async () =>
            {
                while (!Process.StandardError.EndOfStream)
                {
                    string line = await Process.StandardError.ReadLineAsync();
                    if (line is not null)
                    {
                        errorBuilder.AppendLine(line);
                        if (logSdcpp) Logs.Debug($"[SDcpp] Error: {line}");
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
            if (Settings.DebugMode) Logs.Debug($"[SDcpp] Process completed with exit code: {Process.ExitCode}");
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
    public async Task<(bool Success, string Output, string Error)> ExecuteWithProgressAsync(Dictionary<string, object> parameters, bool isFluxModel, Action<float> progressCallback)
    {
        if (!ValidateExecutable()) return (false, "", "SD.cpp executable validation failed");
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
            if (Settings.Device.Equals("cpu", StringComparison.InvariantCultureIgnoreCase))
            {
                processInfo.EnvironmentVariables["GGML_USE_VULKAN"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_CUDA"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_METAL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_OPENCL"] = "0";
                processInfo.EnvironmentVariables["GGML_USE_SYCL"] = "0";
            }
            if (Settings.DebugMode)
            {
                Logs.Debug($"[SDcpp] Executing: {Settings.ExecutablePath} {commandLine}");
            }
            Process = Process.Start(processInfo);
            if (Process is null) return (false, "", "Failed to start SD.cpp process");
            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();
            Task outputTask = Task.Run(async () =>
            {
                while (!Process.StandardOutput.EndOfStream)
                {
                    string line = await Process.StandardOutput.ReadLineAsync();
                    if (line is not null)
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
                    if (line is not null)
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
            if (Settings.DebugMode) Logs.Debug($"[SDcpp] Process completed with exit code: {Process.ExitCode}");
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

    /// <summary>Forcibly terminates the SD.cpp process if it's currently running. Used for cleanup during shutdown or when a process needs to be cancelled. Waits up to 5 seconds for graceful exit before forcing termination.</summary>
    public void KillProcess()
    {
        try
        {
            if (Process is not null && !Process.HasExited)
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
            GC.SuppressFinalize(this);
        }
    }
}
