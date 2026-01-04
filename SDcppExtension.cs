using Hartsy.Extensions.SDcppExtension.SwarmBackends;
using Hartsy.Extensions.SDcppExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.SDcppExtension;

/// <summary>Main extension class that integrates stable-diffusion.cpp with SwarmUI.
/// Registers the SD.cpp backend type and API endpoints during SwarmUI initialization.</summary>
public class SDcppExtension : Extension
{
    public static new readonly string Version = "0.1.0";

    public static T2IRegisteredParam<string> SamplerParam;
    public static T2IRegisteredParam<string> SchedulerParam;
    public static T2IRegisteredParam<bool> VAETilingParam;
    public static T2IRegisteredParam<bool> VAEOnCPUParam;
    public static T2IRegisteredParam<bool> CLIPOnCPUParam;
    public static T2IRegisteredParam<bool> FlashAttentionParam;
    public static T2IRegisteredParam<T2IModel> TAESDParam;
    public static T2IRegisteredParam<T2IModel> UpscaleModelParam;
    public static T2IRegisteredParam<int> UpscaleRepeatsParam;
    public static T2IRegisteredParam<bool> ColorProjectionParam;
    public static T2IRegisteredParam<bool> MemoryMapParam;
    public static T2IRegisteredParam<bool> VAEConvDirectParam;
    public static T2IRegisteredParam<string> CacheModeParam;
    public static T2IRegisteredParam<string> CachePresetParam;

    /// <inheritdoc/>
    public override void OnPreInit()
    {
        try
        {
            Program.ModelRefreshEvent += OnModelRefresh;
            Program.ModelPathsChangedEvent += OnModelPathsChanged;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Critical error during pre-initialization: {ex.Message}");
        }
    }

    /// <summary>Called when SwarmUI refreshes its model list</summary>
    public void OnModelRefresh()
    {
        try
        {
            Logs.Verbose("[SDcpp] Model refresh event received");
            foreach (SDcppBackend backend in Program.Backends.RunningBackendsOfType<SDcppBackend>())
            {
                backend.RefreshModels();
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error handling model refresh: {ex.Message}");
        }
    }

    /// <summary>Called when model paths are changed in settings</summary>
    public void OnModelPathsChanged()
    {
        try
        {
            Logs.Verbose("[SDcpp] Model paths changed event received");
            foreach (SDcppBackend backend in Program.Backends.RunningBackendsOfType<SDcppBackend>())
            {
                backend.RefreshModels();
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error handling model paths change: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override void OnInit()
    {
        try
        {
            Logs.Info($"[SDcpp] Initializing Hartsy's SD.cpp backend extension v{Version}");
            RegisterParameters();
            Program.Backends.RegisterBackendType<SDcppBackend>("sdcpp", "SD.cpp Backend", "A backend powered by stable-diffusion.cpp for fast, efficient image generation.", true);
            SDcppAPI.Register();
            Logs.Info("[SDcpp] Extension initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Failed to initialize extension: {ex.Message}");
            throw;
        }
    }

    /// <summary>Registers SD.cpp specific parameters with SwarmUI's parameter system.
    /// These parameters will be available in the UI when using the SD.cpp backend.</summary>
    public void RegisterParameters()
    {
        try
        {
            Logs.Verbose("[SDcpp] Registering parameters");
            T2IParamGroup sdcppGroup = new("SD.cpp", Toggles: true, Open: false, OrderPriority: 5);
            VAETilingParam = T2IParamTypes.Register<bool>(new("VAE Tiling", "Enable VAE tiling to reduce memory usage during generation. Recommended for limited VRAM systems.",
                "true", Group: sdcppGroup, FeatureFlag: "sdcpp", OrderPriority: 1));
            VAEOnCPUParam = T2IParamTypes.Register<bool>(new("VAE on CPU", "Run VAE decoder on CPU instead of GPU. Useful if running out of VRAM.",
                "false", Group: sdcppGroup, FeatureFlag: "sdcpp", OrderPriority: 2));
            CLIPOnCPUParam = T2IParamTypes.Register<bool>(new("CLIP on CPU", "Run CLIP text encoder on CPU instead of GPU. Useful if running out of VRAM.",
                "false", Group: sdcppGroup, FeatureFlag: "sdcpp", OrderPriority: 3));
            FlashAttentionParam = T2IParamTypes.Register<bool>(new("Flash Attention", "Enable Flash Attention optimization. May reduce quality slightly but saves memory.",
                "false", Group: sdcppGroup, FeatureFlag: "sdcpp", OrderPriority: 4));
            SamplerParam = T2IParamTypes.Register<string>(new("SD.cpp Sampler", "Sampling method for SD.cpp backend. Euler is recommended for Flux, euler_a for SD/SDXL.",
                "euler", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupSampling, OrderPriority: -5,
                GetValues: (_) => ["euler", "euler_a", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "ipndm", "ipndm_v", "lcm", "tcd"]));
            SchedulerParam = T2IParamTypes.Register<string>(new("SD.cpp Scheduler", "Scheduler type for SD.cpp backend. Karras is popular for quality. Goes with the Sampler parameter. Leave empty to use SD.cpp's default scheduler.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupSampling, OrderPriority: -4, IgnoreIf: "",
                GetValues: (_) =>["discrete", "karras", "exponential", "ays", "gits"]));
            TAESDParam = T2IParamTypes.Register<T2IModel>(new("TAESD Preview Decoder", "Tiny AutoEncoder for fast preview decoding. Use for quick previews during generation (lower quality but much faster).",
                "(None)", Toggleable: true, FeatureFlag: "sdcpp", Group: sdcppGroup, Subtype: "VAE", OrderPriority: 5));
            UpscaleModelParam = T2IParamTypes.Register<T2IModel>(new("ESRGAN Upscale Model", "ESRGAN model for upscaling generated images. Currently supports RealESRGAN_x4plus_anime_6B and similar models.",
                "(None)", Toggleable: true, FeatureFlag: "sdcpp", Group: sdcppGroup, Subtype: "upscale_model", OrderPriority: 6));
            UpscaleRepeatsParam = T2IParamTypes.Register<int>(new("Upscale Repeats", "Number of times to run the ESRGAN upscaler. Higher values = more upscaling but longer processing time.",
                "1", Min: 1, Max: 4, Toggleable: true, FeatureFlag: "sdcpp", Group: sdcppGroup, OrderPriority: 7));
            ColorProjectionParam = T2IParamTypes.Register<bool>(new("Color Projection", "Apply color correction to project the generated image colors towards the init image. Useful for maintaining color consistency.",
                "false", Toggleable: true, FeatureFlag: "sdcpp", Group: sdcppGroup, OrderPriority: 8));
            T2IParamGroup performanceGroup = new("SD.cpp Performance", Toggles: true, Open: false, OrderPriority: 6, IsAdvanced: true);
            MemoryMapParam = T2IParamTypes.Register<bool>(new("Memory Map Models", "Memory-map model files for faster loading and reduced RAM usage. Recommended for most systems. Requires SD.cpp master-1896b28 or newer.",
                "true", Group: performanceGroup, FeatureFlag: "sdcpp", OrderPriority: 1, Toggleable: true));
            VAEConvDirectParam = T2IParamTypes.Register<bool>(new("VAE Direct Convolution", "Use direct convolution in VAE decoder for faster processing. Significantly improves decoding speed. Requires SD.cpp master-1896b28 or newer.",
                "true", Group: performanceGroup, FeatureFlag: "sdcpp", OrderPriority: 2, Toggleable: true));
            CacheModeParam = T2IParamTypes.Register<string>(new("Cache Mode", "Inference caching mode for faster generation. 'auto' detects best mode (cache-dit for Flux/DiT, ucache for SD/SDXL). Can reduce generation time by 5-10x. Requires SD.cpp master-1896b28 or newer.",
                "auto", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 3, GetValues: (_) => ["none", "auto", "easycache", "ucache", "dbcache", "taylorseer", "cache-dit"]));
            CachePresetParam = T2IParamTypes.Register<string>(new("Cache Preset", "Cache quality/speed preset. 'fast' or 'ultra' modes can reduce generation time by 5-10x. Only applies when Cache Mode is enabled.",
                "fast", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 4, GetValues: (_) =>["slow", "medium", "fast", "ultra"]));
            Logs.Verbose("[SDcpp] Parameters registered successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error registering parameters: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override void OnShutdown()
    {
        Logs.Info("[SDcpp] Shutting down extension");
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
        if (exception is not null)
        {
            Logs.Error($"[SDcpp] Exception details: {exception}");
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
        if (data is not null)
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
