using Hartsy.Extensions.SDcppExtension.SwarmBackends;
using Hartsy.Extensions.SDcppExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
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

            // Subscribe to model events for automatic updates
            Program.ModelRefreshEvent += OnModelRefresh;
            Program.ModelPathsChangedEvent += OnModelPathsChanged;

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
    /// Called when SwarmUI refreshes its model list
    /// </summary>
    private void OnModelRefresh()
    {
        try
        {
            Logs.Debug("[SDcppExtension] Model refresh event received");
            // Backend will re-scan models on next init/generation
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcppExtension] Error handling model refresh: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when model paths are changed in settings
    /// </summary>
    private void OnModelPathsChanged()
    {
        try
        {
            Logs.Debug("[SDcppExtension] Model paths changed event received");
            // Update Flux component search paths if needed
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcppExtension] Error handling model paths change: {ex.Message}");
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

            // Register SD.cpp-specific parameters
            RegisterParameters();

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
    /// Registers SD.cpp-specific parameters with SwarmUI's parameter system.
    /// These parameters will be available in the UI when using the SD.cpp backend.
    /// </summary>
    private void RegisterParameters()
    {
        try
        {
            Logs.Debug("[SDcppExtension] Registering parameters");

            // Create parameter groups for organization
            T2IParamGroup sdcppGroup = new("SD.cpp", Toggles: true, Open: false, OrderPriority: 5);
            T2IParamGroup fluxGroup = new("Flux", Toggles: false, Open: false, OrderPriority: 6);

            // SD.cpp general parameters
            T2IParamTypes.Register<bool>(new(
                "VAE Tiling",
                "Enable VAE tiling to reduce memory usage during generation. Recommended for limited VRAM systems.",
                "true",
                Group: sdcppGroup,
                FeatureFlag: "sdcpp",
                OrderPriority: 1
            ));

            T2IParamTypes.Register<bool>(new(
                "VAE on CPU",
                "Run VAE decoder on CPU instead of GPU. Useful if running out of VRAM.",
                "false",
                Group: sdcppGroup,
                FeatureFlag: "sdcpp",
                OrderPriority: 2
            ));

            T2IParamTypes.Register<bool>(new(
                "CLIP on CPU",
                "Run CLIP text encoder on CPU instead of GPU. Useful if running out of VRAM.",
                "false",
                Group: sdcppGroup,
                FeatureFlag: "sdcpp",
                OrderPriority: 3
            ));

            T2IParamTypes.Register<bool>(new(
                "Flash Attention",
                "Enable Flash Attention optimization. May reduce quality slightly but saves memory.",
                "false",
                Group: sdcppGroup,
                FeatureFlag: "sdcpp",
                OrderPriority: 4
            ));

            // Flux-specific parameters
            T2IParamTypes.Register<string>(new(
                "Flux Quantization",
                "Quantization level for Flux models. q8_0=best quality (12GB VRAM), q4_0=balanced (6-8GB), q2_k=low VRAM (4GB).",
                "q8_0",
                GetValues: (_) => ["q8_0", "q4_0", "q4_k", "q3_k", "q2_k"],
                Group: fluxGroup,
                FeatureFlag: "flux",
                OrderPriority: 1
            ));

            T2IParamTypes.Register<bool>(new(
                "Auto Convert Flux to GGUF",
                "Automatically convert Flux models to GGUF format if needed. Conversion takes 5-15 minutes but only happens once.",
                "true",
                Group: fluxGroup,
                FeatureFlag: "flux",
                OrderPriority: 2
            ));

            T2IParamTypes.Register<int>(new(
                "Flux Dev Steps",
                "Default sampling steps for Flux-dev models. 20+ recommended for quality.",
                "20",
                Min: 1, Max: 100, Step: 1,
                ViewType: ParamViewType.SLIDER,
                Group: fluxGroup,
                FeatureFlag: "flux",
                OrderPriority: 3
            ));

            T2IParamTypes.Register<int>(new(
                "Flux Schnell Steps",
                "Default sampling steps for Flux-schnell models. 4 recommended for speed.",
                "4",
                Min: 1, Max: 20, Step: 1,
                ViewType: ParamViewType.SLIDER,
                Group: fluxGroup,
                FeatureFlag: "flux",
                OrderPriority: 4
            ));

            Logs.Debug("[SDcppExtension] Parameters registered successfully");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcppExtension] Error registering parameters: {ex.Message}");
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
