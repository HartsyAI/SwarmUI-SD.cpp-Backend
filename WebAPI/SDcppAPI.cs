using Hartsy.Extensions.SDcppExtension.SwarmBackends;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.SDcppExtension.WebAPI;

/// <summary>
/// Provides HTTP API endpoints for managing and monitoring SD.cpp backend instances.
/// Handles backend status queries, model listing, and settings management for the SwarmUI web interface.
/// </summary>
public static class SDcppAPI
{
    /// <summary>
    /// Registers all SD.cpp API endpoints with SwarmUI's API system.
    /// Called during extension initialization to make endpoints available to the web interface.
    /// </summary>
    public static void Register()
    {
        try
        {
            Logs.Info("[SDcppAPI] Registering API endpoints");

            // Register API endpoints using the correct pattern
            API.RegisterAPICall(GetBackendStatus);
            API.RegisterAPICall(ListModels);
            API.RegisterAPICall(GetSettings);

            Logs.Info("[SDcppAPI] API endpoints registered successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcppAPI] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Retrieves the current status of all SD.cpp backend instances.
    /// Returns information about backend state, loaded models, and process health.
    /// Used by the web interface to display backend status and troubleshoot issues.
    /// </summary>
    /// <param name="session">Current user session for authentication and logging</param>
    /// <returns>JSON object containing status information for all SD.cpp backends</returns>
    public static async Task<JObject> GetBackendStatus(Session session)
    {
        try
        {
            List<SDcppBackend> backends = [.. Program.Backends.RunningBackendsOfType<SDcppBackend>()];
            JArray backendStatuses = [];

            foreach (SDcppBackend backend in backends)
            {
                JObject status = new()
                {
                    ["id"] = backend.HandlerTypeData.ID,
                    ["name"] = backend.HandlerTypeData.Name,
                    ["status"] = backend.Status.ToString(),
                    ["current_model"] = backend.CurrentModelName,
                    ["supported_features"] = new JArray(backend.SupportedFeatures.ToArray())
                };
                backendStatuses.Add(status);
            }

            return SDcppExtension.CreateSuccessResponse(new JObject
            {
                ["backends"] = backendStatuses,
                ["total_backends"] = backends.Count
            });
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcppAPI] Error getting backend status: {ex.Message}");
            return SDcppExtension.CreateErrorResponse("Failed to get backend status", "STATUS_ERROR", ex);
        }
    }

    /// <summary>
    /// Lists available models for SD.cpp backend instances.
    /// Returns information about model names, titles, file paths, and architectures.
    /// Used by the web interface to display model options and configure backend instances.
    /// </summary>
    /// <param name="session">Current user session for authentication and logging</param>
    /// <returns>JSON object containing model information</returns>
    public static async Task<JObject> ListModels(Session session)
    {
        try
        {
            JObject[] models = [.. Program.T2IModelSets["Stable-Diffusion"].Models.Values
                .Select(m => new JObject
                {
                    ["name"] = m.Name,
                    ["title"] = m.Title,
                    ["path"] = m.RawFilePath,
                    ["architecture"] = m.StandardWidth + "x" + m.StandardHeight,
                    ["type"] = m.ModelClass?.ID ?? "unknown"
                })];

            return SDcppExtension.CreateSuccessResponse(new JObject
            {
                ["models"] = new JArray(models),
                ["total_models"] = models.Length
            });
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcppAPI] Error listing models: {ex.Message}");
            return SDcppExtension.CreateErrorResponse("Failed to list models", "LIST_MODELS_ERROR", ex);
        }
    }

    /// <summary>
    /// Retrieves the current settings for SD.cpp backend instances.
    /// Returns information about executable paths, debug modes, and device configurations.
    /// Used by the web interface to display and configure backend settings.
    /// </summary>
    /// <param name="session">Current user session for authentication and logging</param>
    /// <returns>JSON object containing settings information</returns>
    public static async Task<JObject> GetSettings(Session session)
    {
        try
        {
            // This is a placeholder - in a full implementation, this would return current settings
            return SDcppExtension.CreateSuccessResponse(new JObject
            {
                ["settings"] = new JObject
                {
                    ["executable_path"] = "",
                    ["debug_mode"] = false,
                    ["device"] = "auto"
                }
            });
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcppAPI] Error getting settings: {ex.Message}");
            return SDcppExtension.CreateErrorResponse("Failed to get settings", "GET_SETTINGS_ERROR", ex);
        }
    }
}
