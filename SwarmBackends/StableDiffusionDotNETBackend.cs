using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;
using System.Text;

namespace Hartsy.Extensions.SDdotNETExtension.SwarmBackends;

/// <summary>StableDiffusion.NET backend for SwarmUI integration.</summary>
public class StableDiffusionDotNETBackend : SDdotNETBackends
{
    /// <summary>Configuration settings for Backends</summary>
    public class StableDiffusionDotNETBackendSettings : AutoConfiguration
    {
        /// <summary>Debug mode setting</summary>
        [ConfigComment("Enable debug logging")]
        public bool DebugMode = false;
    }

    /// <summary>Initialize the backend</summary>
    public override async Task Init()
    {
        try
        {
            BackendStatusResponse startResult = await StartAsync();
            if (!startResult.Success)
            {
                Logs.Warning($"[] backend service failed to start: {startResult.Message}");
                Status = BackendStatus.ERRORED;
                return;
            }
            Status = BackendStatus.RUNNING;
            Logs.Info("[] backend initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Failed to initialize: {ex.Message}");
            Status = BackendStatus.ERRORED;
            throw;
        }
    }

    /// <summary>Starts the backend.</summary>
    /// <param name="forceRestart">Whether to restart if already running</param>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> StartAsync(bool forceRestart = false)
    {
        try
        {
            Logs.Info("[] backend started successfully");
            return new BackendStatusResponse
            {
                Success = true,
                Message = "backend started successfully",
                Status = "running",
                BackendType = "TTS",
                IsRunning = true
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Failed to start backend: {ex.Message}");
            return new BackendStatusResponse
            {
                Success = false,
                Message = $"backend startup failed: {ex.Message}",
                Status = "error",
                BackendType = "TTS",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Stops the backend gracefully.</summary>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> StopAsync()
    {
        try
        {
            Logs.Info("[] Stopping backend service");
            await Shutdown();
            return new BackendStatusResponse
            {
                Success = true,
                Message = "backend stopped successfully",
                Status = "stopped",
                BackendType = "TTS",
                IsRunning = false
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Failed to stop backend: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"Failed to stop TTS backend: {ex.Message}",
                Status = "error",
                BackendType = "TTS",
                ErrorDetails = ex.ToString()
            };
        }
    }

    public override IEnumerable<string> SupportedFeatures => throw new NotImplementedException();

    /// <summary>Gets the current status of the backend.</summary>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> GetStatusAsync()
    {
        try
        {
            // TODO: Check actual running status and create BackendStatusResponse for SD.NET backend instead of using VoiceAssistant example
            bool isRunning = true;
            string status = isRunning ? "running" : "stopped";

            BackendStatusResponse response = new()
            {
                Success = true,
                Status = status,
                BackendType = "TTS",
                IsRunning = isRunning
            };
            // Add health information if running
            if (isRunning)
            {
                try
                {
                    bool isHealthy = true; // TODO: Implement actual health check logic
                    response.IsHealthy = isHealthy;
                    response.Message = isHealthy ? "backend running and healthy" : "backend running but unhealthy";
                }
                catch (Exception ex)
                {
                    response.IsHealthy = false;
                    response.Message = $"backend status check failed: {ex.Message}";
                }
            }
            else
            {
                response.Message = "backend not running";
            }
            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Failed to get backend status: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"Failed to get backend status: {ex.Message}",
                Status = "error",
                BackendType = "TTS",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Load model</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Info($"[] Loading TTS model: {model.Name}");
            // Validate model compatibility
            if (!IsModelCompatible(model))
            {
                Logs.Error($"[] Model not compatible: {model.Name}");
                return false;
            }
            CurrentModelName = model.Name;
            Logs.Info($"[] Successfully loaded: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Error loading model {model.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Process text input and generate speech audio</summary>
    public override async Task<Image[]> Generate(T2IParamInput input)
    {
        try
        {
            // TODO: Call generate methods using StableDiffusion.NET
            Image[] result = [];
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[] generation failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Create an image containing metadata for display</summary>
    public static async Task<Image> CreateMetadataImageAsync(Dictionary<string, object> metadata, string filePath, string originalText)
    {
        try
        {
            // TODO: Create an Image object so it can be processed. How does Comfy backend handle this? This and the CreateImageAsync method can be combined?
            string metadataContent = $"";
            byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataContent);
            metadata = new()
            {
                ["type"] = "metadata",
            };
            return new Image("", Image.ImageType.IMAGE, "");
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Error creating metadata image: {ex.Message}");
            throw;
        }
    }

    /// <summary>Create an image </summary>
    public static async Task<Image> CreateImageAsync(Dictionary<string, object> metadata, string filePath)
    {
        try
        {
            // TODO: Create an Image object so it can be processed. How does Comfy backend handle this?
            metadata = new()
            {
                ["type"] = "image",
            };
            return new Image("", Image.ImageType.IMAGE, "");
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error creating audio image: {ex.Message}");
            throw;
        }
    }

    /// <summary>Free memory and resources</summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        try
        {
            // TODO: Cleanup resources. Is this needed or is this handled by Swarm?
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[] Failed to free memory: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shutdown the TTS backend</summary>
    public override async Task Shutdown()
    {
        try
        {
            Logs.Info("[] Shutting down backend");
            await StopAsync();
            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[] Error during shutdown: {ex.Message}");
        }
    }
}
