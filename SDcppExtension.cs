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
    public static new readonly string Version = "0.1.5";

    public static T2IRegisteredParam<string> SamplerParam;
    public static T2IRegisteredParam<string> SchedulerParam;
    public static T2IRegisteredParam<bool> VAETilingParam;
    public static T2IRegisteredParam<bool> VAEOnCPUParam;
    public static T2IRegisteredParam<bool> CLIPOnCPUParam;
    public static T2IRegisteredParam<bool> OffloadToCPUParam;
    public static T2IRegisteredParam<bool> ControlNetOnCPUParam;
    public static T2IRegisteredParam<T2IModel> TAESDParam;
    public static T2IRegisteredParam<T2IModel> UpscaleModelParam;
    public static T2IRegisteredParam<int> UpscaleRepeatsParam;
    public static T2IRegisteredParam<bool> MemoryMapParam;
    public static T2IRegisteredParam<bool> VAEConvDirectParam;
    public static T2IRegisteredParam<bool> FlashAttentionParam;
    public static T2IRegisteredParam<bool> DiffusionConvDirectParam;
    public static T2IRegisteredParam<string> CacheModeParam;
    public static T2IRegisteredParam<string> CachePresetParam;
    public static T2IRegisteredParam<string> CacheOptionParam;

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
            T2IParamGroup vramGroup = new("SD.cpp VRAM / Memory", Toggles: true, Open: false, OrderPriority: 6, IsAdvanced: true);
            VAETilingParam = T2IParamTypes.Register<bool>(new("VAE Tiling", "Enables SD.cpp's VAE tiling mode (`--vae-tiling`).\nThis reduces VRAM usage by decoding the VAE in smaller tiles, at the cost of some speed.",
                "true", Group: vramGroup, FeatureFlag: "sdcpp", OrderPriority: 1));
            VAEOnCPUParam = T2IParamTypes.Register<bool>(new("VAE on CPU", "Runs the VAE decoder on CPU (`--vae-on-cpu`).\nThis saves VRAM but is significantly slower. Useful when you otherwise hit out-of-memory errors.",
                "false", Group: vramGroup, FeatureFlag: "sdcpp", OrderPriority: 2));
            CLIPOnCPUParam = T2IParamTypes.Register<bool>(new("CLIP on CPU", "Runs text encoder(s) on CPU (`--clip-on-cpu`).\nThis saves VRAM but can slow down prompt processing.",
                "false", Group: vramGroup, FeatureFlag: "sdcpp", OrderPriority: 3));
            OffloadToCPUParam = T2IParamTypes.Register<bool>(new("Offload Model Weights to CPU", "Enables SD.cpp weight offloading (`--offload-to-cpu`).\nThis keeps weights in RAM and moves them into VRAM only when needed. It saves VRAM, but can be slower.",
                "false", Toggleable: true, FeatureFlag: "sdcpp", Group: vramGroup, OrderPriority: 4, IgnoreIf: "false", IsAdvanced: true));
            ControlNetOnCPUParam = T2IParamTypes.Register<bool>(new("ControlNet on CPU", "Keeps the ControlNet model on CPU (`--control-net-cpu`).\nOnly affects jobs that have ControlNet enabled. Saves VRAM but is slower.",
                "false", Toggleable: true, FeatureFlag: "sdcpp", Group: vramGroup, OrderPriority: 5, IgnoreIf: "false", IsAdvanced: true));
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
            T2IParamGroup performanceGroup = new("SD.cpp Performance / Caching", Toggles: true, Open: false, OrderPriority: 7, IsAdvanced: true);
            MemoryMapParam = T2IParamTypes.Register<bool>(new("Memory Map Models", "Enables memory-mapping (`--mmap`).\nThis usually reduces RAM usage and can speed up model load times. Recommended for most systems.",
                "true", Group: performanceGroup, FeatureFlag: "sdcpp", OrderPriority: 1, Toggleable: true));
            VAEConvDirectParam = T2IParamTypes.Register<bool>(new("VAE Direct Convolution", "Enables SD.cpp's direct VAE convolution path (`--vae-conv-direct`).\nThis usually speeds up VAE decoding. If you see artifacts or crashes, disable it.",
                "true", Group: performanceGroup, FeatureFlag: "sdcpp", OrderPriority: 2, Toggleable: true));
            FlashAttentionParam = T2IParamTypes.Register<bool>(new("Flash Attention", "Enables flash-attention in SD.cpp diffusion (`--diffusion-fa`).\nThis can reduce VRAM usage and sometimes improve speed, but may not help on all builds/devices.",
                "true", Group: T2IParamTypes.GroupAdvancedSampling, FeatureFlag: "sdcpp", OrderPriority: 30, Toggleable: true, IsAdvanced: true));
            DiffusionConvDirectParam = T2IParamTypes.Register<bool>(new("Diffusion Direct Convolution", "Enables SD.cpp's direct convolution path in the diffusion model (`--diffusion-conv-direct`).\nThis can improve performance. If you see issues on your device/backend build, disable it.",
                "true", Group: T2IParamTypes.GroupAdvancedSampling, FeatureFlag: "sdcpp", OrderPriority: 31, Toggleable: true, IsAdvanced: true));
            CacheModeParam = T2IParamTypes.Register<string>(new("Cache Mode", "Selects SD.cpp's caching strategy (`--cache-mode`).\nCaching can dramatically speed up repeated generations, but may reduce quality depending on mode/options.\nUse 'auto' to let the backend choose a reasonable cache mode based on the model type.",
                "auto", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 10, IgnoreIf: "auto",
                GetValues: (_) => ["auto", "disabled", "easycache", "ucache", "dbcache", "taylorseer", "cache-dit"]));
            CachePresetParam = T2IParamTypes.Register<string>(new("Cache Preset", "Preset for cache-dit caching (`--cache-preset`).\nOnly applies when Cache Mode is set to 'cache-dit'.",
                "ultra", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 11, IgnoreIf: "ultra", IsAdvanced: true, DependNonDefault: CacheModeParam.Type.ID,
                GetValues: (_) => ["slow", "medium", "fast", "ultra"]));
            CacheOptionParam = T2IParamTypes.Register<string>(new("Cache Option", "Raw cache options string passed to SD.cpp (`--cache-option`).\nThis is an advanced escape hatch for tuning cache behavior.\nExample formats depend on cache mode (eg ucache/easycache vs cache-dit/dbcache).",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 12, IgnoreIf: "", IsAdvanced: true, ValidateValues: false, DependNonDefault: CacheModeParam.Type.ID));
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
