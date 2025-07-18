using Hartsy.Extensions.SDcppExtension.SwarmBackends;
using Hartsy.Extensions.SDcppExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.SDcppExtension;

/// <summary>
/// Main extension class that integrates stable-diffusion.cpp with SwarmUI.
/// Registers the SD.cpp backend type and API endpoints during SwarmUI initialization.
/// Provides a direct CLI-based integration replacing previous wrapper approaches.
/// </summary>
public class SDcppExtension : Extension
{
    /// <summary>Extension version for compatibility tracking</summary>
    public static new readonly string Version = "0.1.0";

    /// <summary>
    /// Pre-initialization phase - registers web assets before SwarmUI core initialization.
    /// This runs before the main UI is ready, so we only register static assets here.
    /// </summary>
    public override void OnPreInit()
    {
        try
        {
            Logs.Debug($"[SDcppExtension] Pre-initializing extension v{Version}");
            // Register any CSS/JS assets here if needed
            // ScriptFiles.Add("Assets/sdcpp-frontend.js");
            // StyleSheetFiles.Add("Assets/sdcpp-styles.css");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcppExtension] Critical error during pre-initialization: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes the SD.cpp extension by registering the backend type and API endpoints.
    /// Called once during SwarmUI startup to make SD.cpp functionality available.
    /// </summary>
    public override void OnInit()
    {
        try
        {
            Logs.Info($"[SDcppExtension] Initializing SD.cpp extension v{Version}");

            // Register the SD.cpp backend type
            Program.Backends.RegisterBackendType<SDcppBackend>("sdcpp", "SD.cpp Backend", 
                "A backend powered by stable-diffusion.cpp for fast, efficient image generation.", true);

            // Register API endpoints
            SDcppAPI.Register();

            Logs.Info("[SDcppExtension] Extension initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcppExtension] Failed to initialize extension: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Shutdown cleanup
    /// </summary>
    public override void OnShutdown()
    {
        try
        {
            Logs.Info("[SDcppExtension] Shutting down extension");
            // Cleanup resources if needed
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcppExtension] Error during shutdown: {ex.Message}");
        }
    }

    /// <summary>Creates a standardized error response for API endpoints</summary>
    public static JObject CreateErrorResponse(string message, string errorCode = null, Exception exception = null)
    {
        JObject response = new()
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (!string.IsNullOrEmpty(errorCode))
        {
            response["error_code"] = errorCode;
        }
        if (exception != null)
        {
            Logs.Error($"[SDcppExtension] Exception details: {exception}");
            response["error_type"] = exception.GetType().Name;
        }
        return response;
    }

    /// <summary>Creates a standardized success response for API endpoints</summary>
    public static JObject CreateSuccessResponse(object data = null, string message = null)
    {
        JObject response = new()
        {
            ["success"] = true,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (!string.IsNullOrEmpty(message))
        {
            response["message"] = message;
        }
        if (data != null)
        {
            if (data is JObject jObject)
            {
                foreach (JProperty property in jObject.Properties())
                {
                    response[property.Name] = property.Value;
                }
            }
            else
            {
                response["data"] = JToken.FromObject(data);
            }
        }
        return response;
    }
}
