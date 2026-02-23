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

    public static T2IRegisteredParam<string> RNGParam;
    public static T2IRegisteredParam<string> SamplerRNGParam;
    public static T2IRegisteredParam<string> PredictionParam;
    public static T2IRegisteredParam<double> EtaParam;
    public static T2IRegisteredParam<string> SigmasParam;
    public static T2IRegisteredParam<double> SLGScaleParam;
    public static T2IRegisteredParam<double> SkipLayerStartParam;
    public static T2IRegisteredParam<double> SkipLayerEndParam;
    public static T2IRegisteredParam<string> SkipLayersParam;
    public static T2IRegisteredParam<int> TimestepShiftParam;

    public static T2IRegisteredParam<double> FlowShiftParam;
    public static T2IRegisteredParam<string> ControlVideoFramesDirParam;
    public static T2IRegisteredParam<double> MoeBoundaryParam;
    public static T2IRegisteredParam<double> VaceStrengthParam;

    public static T2IRegisteredParam<T2IModel> LlmVisionModelParam;
    public static T2IRegisteredParam<string> EmbeddingsDirParam;
    public static T2IRegisteredParam<string> TensorTypeParam;
    public static T2IRegisteredParam<string> TensorTypeRulesParam;
    public static T2IRegisteredParam<string> LoraApplyModeParam;
    public static T2IRegisteredParam<T2IModel> PhotoMakerModelParam;
    public static T2IRegisteredParam<string> PhotoMakerIdImagesDirParam;
    public static T2IRegisteredParam<string> PhotoMakerIdEmbedPathParam;
    public static T2IRegisteredParam<double> PhotoMakerStyleStrengthParam;

    public static T2IRegisteredParam<string> VAETileSizeParam;
    public static T2IRegisteredParam<string> VAERelativeTileSizeParam;
    public static T2IRegisteredParam<double> VAETileOverlapParam;
    public static T2IRegisteredParam<bool> ForceSDXLVAEConvScaleParam;

    public static T2IRegisteredParam<string> SCMMaskParam;
    public static T2IRegisteredParam<string> SCMPolicyParam;

    public static T2IRegisteredParam<string> PreviewMethodOverrideParam;
    public static T2IRegisteredParam<int> PreviewIntervalParam;
    public static T2IRegisteredParam<bool> PreviewNoisyParam;
    public static T2IRegisteredParam<bool> TAESDPreviewOnlyParam;

    public static T2IRegisteredParam<bool> CannyPreprocessorParam;

    /// <inheritdoc/>
    public override void OnPreInit()
    {
        Program.ModelRefreshEvent += OnModelRefresh;
        Program.ModelPathsChangedEvent += OnModelPathsChanged;
    }

    /// <summary>Called when SwarmUI refreshes its model list</summary>
    public void OnModelRefresh() => RefreshAllBackends("Model refresh event received");

    /// <summary>Called when model paths are changed in settings</summary>
    public void OnModelPathsChanged() => RefreshAllBackends("Model paths changed event received");

    /// <summary>Refreshes models on all running SD.cpp backends</summary>
    private void RefreshAllBackends(string reason)
    {
        try
        {
            Logs.Verbose($"[SDcpp] {reason}");
            foreach (SDcppBackend backend in Program.Backends.RunningBackendsOfType<SDcppBackend>())
            {
                backend.RefreshModels();
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error during backend refresh ({reason}): {ex.Message}");
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

            VAETileSizeParam = T2IParamTypes.Register<string>(new("SD.cpp VAE Tile Size", "Tile size for SD.cpp VAE tiling (`--vae-tile-size`).\nFormat is `XxY` in latent tile units (for example: `32x32`).\nOnly relevant when VAE Tiling is enabled.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: vramGroup, OrderPriority: 6, IgnoreIf: "", IsAdvanced: true, ValidateValues: false, DependNonDefault: VAETilingParam.Type.ID));
            VAERelativeTileSizeParam = T2IParamTypes.Register<string>(new("SD.cpp VAE Relative Tile Size", "Relative tile size for SD.cpp VAE tiling (`--vae-relative-tile-size`).\nFormat is `XxY`. Values < 1 are fractions of image size; values >= 1 are tiles-per-dimension.\nThis overrides `--vae-tile-size` if set.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: vramGroup, OrderPriority: 7, IgnoreIf: "", IsAdvanced: true, ValidateValues: false, DependNonDefault: VAETilingParam.Type.ID));
            VAETileOverlapParam = T2IParamTypes.Register<double>(new("SD.cpp VAE Tile Overlap", "Tile overlap fraction for SD.cpp VAE tiling (`--vae-tile-overlap`).\nHigher overlap can reduce seam artifacts but uses more compute.\n0.5 is SD.cpp's typical default.",
                "0.5", Min: 0, Max: 1, Step: 0.05, Toggleable: true, FeatureFlag: "sdcpp", Group: vramGroup, OrderPriority: 8, IgnoreIf: "0.5", IsAdvanced: true, ViewType: ParamViewType.SLIDER, DependNonDefault: VAETilingParam.Type.ID));
            ForceSDXLVAEConvScaleParam = T2IParamTypes.Register<bool>(new("Force SDXL VAE Conv Scale", "Forces use of conv scale in SDXL VAE (`--force-sdxl-vae-conv-scale`).\nOnly relevant for SDXL VAE behavior. If you don't know what this is, leave it off.",
                "false", IgnoreIf: "false", Toggleable: true, FeatureFlag: "sdcpp", Group: vramGroup, OrderPriority: 9, IsAdvanced: true));
            SamplerParam = T2IParamTypes.Register<string>(new("SD.cpp Sampler", "Sampling method for SD.cpp backend. Euler is recommended for Flux, euler_a for SD/SDXL.",
                "euler", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupSampling, OrderPriority: -5,
                GetValues: (_) => ["euler", "euler_a", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "ipndm", "ipndm_v", "lcm", "tcd"]));
            SchedulerParam = T2IParamTypes.Register<string>(new("SD.cpp Scheduler", "Scheduler type for SD.cpp backend. Karras is popular for quality. Goes with the Sampler parameter. Leave empty to use SD.cpp's default scheduler.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupSampling, OrderPriority: -4, IgnoreIf: "",
                GetValues: (_) =>["discrete", "karras", "exponential", "ays", "gits"]));
            TAESDParam = T2IParamTypes.Register<T2IModel>(new("TAESD Preview Decoder", "Tiny AutoEncoder for fast preview decoding. Use for quick previews during generation (lower quality but much faster).",
                "(None)", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, Subtype: "VAE", OrderPriority: 79, IsAdvanced: true));
            UpscaleModelParam = T2IParamTypes.Register<T2IModel>(new("ESRGAN Upscale Model", "ESRGAN model for upscaling generated images. Currently supports RealESRGAN_x4plus_anime_6B and similar models.",
                "(None)", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupRefiners, Subtype: "upscale_model", OrderPriority: 6));
            UpscaleRepeatsParam = T2IParamTypes.Register<int>(new("Upscale Repeats", "Number of times to run the ESRGAN upscaler. Higher values = more upscaling but longer processing time.",
                "1", Min: 1, Max: 4, Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupRefiners, OrderPriority: 7));
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

            SCMMaskParam = T2IParamTypes.Register<string>(new("SCM Mask", "SCM steps mask for cache-dit (`--scm-mask`).\nComma-separated 0/1 list (1=compute, 0=may cache). Only applies to cache-dit.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 13, IgnoreIf: "", IsAdvanced: true, ValidateValues: false, DependNonDefault: CacheModeParam.Type.ID));
            SCMPolicyParam = T2IParamTypes.Register<string>(new("SCM Policy", "SCM policy for cache-dit (`--scm-policy`).\n'dynamic' adapts automatically; 'static' uses SCM Mask exactly as provided.",
                "dynamic", Toggleable: true, FeatureFlag: "sdcpp", Group: performanceGroup, OrderPriority: 14, IgnoreIf: "dynamic", IsAdvanced: true, GetValues: (_) => ["dynamic", "static"], DependNonDefault: CacheModeParam.Type.ID));

            RNGParam = T2IParamTypes.Register<string>(new("RNG", "Selects the random number generator backend (`--rng`).\nThis affects how random numbers are produced for sampling.\nIf unsure, leave this disabled.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 40, IgnoreIf: "", IsAdvanced: true,
                GetValues: (_) => ["std_default", "cuda", "cpu"]));
            SamplerRNGParam = T2IParamTypes.Register<string>(new("Sampler RNG", "Selects the sampler RNG backend (`--sampler-rng`).\nIf not set, SD.cpp uses the value of `--rng`.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 41, IgnoreIf: "", IsAdvanced: true,
                GetValues: (_) => ["std_default", "cuda", "cpu"]));
            PredictionParam = T2IParamTypes.Register<string>(new("Prediction Override", "Overrides SD.cpp prediction type (`--prediction`).\nThis is model-specific. Only change this if you know the model expects a specific prediction mode.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 42, IgnoreIf: "", IsAdvanced: true,
                GetValues: (_) => ["eps", "v", "edm_v", "sd3_flow", "flux_flow", "flux2_flow"]));
            EtaParam = T2IParamTypes.Register<double>(new("Eta", "Eta value for DDIM/TCD sampling (`--eta`).\nOnly used by certain sampling methods.",
                "0", Min: 0, Max: 1, Step: 0.01, Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 43, IgnoreIf: "0", IsAdvanced: true, ViewType: ParamViewType.SLIDER));
            SigmasParam = T2IParamTypes.Register<string>(new("Custom Sigmas", "Custom sigma schedule (`--sigmas`).\nComma-separated list of sigma values (example: `14.61,7.8,3.5,0.0`).\nThis is very advanced and overrides normal scheduler behavior.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 44, IgnoreIf: "", IsAdvanced: true, ValidateValues: false));
            SLGScaleParam = T2IParamTypes.Register<double>(new("SLG Scale", "Skip Layer Guidance scale (`--slg-scale`).\nOnly applies to DiT models. Set to 0 to disable.",
                "0", Min: 0, Max: 10, Step: 0.05, Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 45, IgnoreIf: "0", IsAdvanced: true, ViewType: ParamViewType.SLIDER));
            SkipLayerStartParam = T2IParamTypes.Register<double>(new("SLG Start", "When to start applying SLG (`--skip-layer-start`).\nFraction of the denoising process (0.0 to 1.0).",
                "0.01", Min: 0, Max: 1, Step: 0.01, Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 46, IgnoreIf: "0.01", IsAdvanced: true, ViewType: ParamViewType.SLIDER, DependNonDefault: SLGScaleParam.Type.ID));
            SkipLayerEndParam = T2IParamTypes.Register<double>(new("SLG End", "When to stop applying SLG (`--skip-layer-end`).\nFraction of the denoising process (0.0 to 1.0).",
                "0.2", Min: 0, Max: 1, Step: 0.01, Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 47, IgnoreIf: "0.2", IsAdvanced: true, ViewType: ParamViewType.SLIDER, DependNonDefault: SLGScaleParam.Type.ID));
            SkipLayersParam = T2IParamTypes.Register<string>(new("SLG Skip Layers", "Which layers to skip for SLG steps (`--skip-layers`).\nFormat is a comma-separated list like `7,8,9`.\nOnly used when SLG is enabled.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 48, IgnoreIf: "", IsAdvanced: true, ValidateValues: false, DependNonDefault: SLGScaleParam.Type.ID));
            TimestepShiftParam = T2IParamTypes.Register<int>(new("Timestep Shift", "Shifts timesteps for NitroFusion models (`--timestep-shift`).\nThis is model-specific. If unsure, leave it disabled.",
                "0", Min: 0, Max: 2000, Step: 1, Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 49, IgnoreIf: "0", IsAdvanced: true, ViewType: ParamViewType.SLIDER, ViewMax: 500));

            FlowShiftParam = T2IParamTypes.Register<double>(new("Flow Shift", "Flow shift value for Flow-based models like SD3.x or Wan (`--flow-shift`).\nIf disabled, SD.cpp uses its default (or the backend may apply a model-specific default).",
                "3", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedVideo, OrderPriority: 20, IgnoreIf: "3", IsAdvanced: true, Min: -100, Max: 100, Step: 0.1, ViewType: ParamViewType.SLIDER));
            ControlVideoFramesDirParam = T2IParamTypes.Register<string>(new("Control Video Frames Directory", "Directory path containing control video frames for SD.cpp (`--control-video`).\nThis must be a folder containing images named in lexicographical order (eg `00.png`, `01.png`, ...).",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedVideo, OrderPriority: 21, IgnoreIf: "", IsAdvanced: true, ValidateValues: false));
            MoeBoundaryParam = T2IParamTypes.Register<double>(new("MoE Boundary", "Timestep boundary for Wan2.2 MoE models (`--moe-boundary`).\nThis is only meaningful for models/builds that support it.",
                "0.875", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedVideo, OrderPriority: 22, IgnoreIf: "0.875", IsAdvanced: true, Min: 0, Max: 1, Step: 0.001, ViewType: ParamViewType.SLIDER));
            VaceStrengthParam = T2IParamTypes.Register<double>(new("VACE Strength", "Wan VACE strength (`--vace-strength`).\nOnly relevant to Wan models/builds that support VACE.",
                "0", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedVideo, OrderPriority: 23, IgnoreIf: "0", IsAdvanced: true, Min: 0, Max: 2, Step: 0.01, ViewType: ParamViewType.SLIDER));

            LlmVisionModelParam = T2IParamTypes.Register<T2IModel>(new("LLM Vision Model", "Optional vision encoder for LLM-based image models (`--llm_vision`).\nOnly needed for architectures that require a separate vision backbone.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, Subtype: "Clip", IsAdvanced: true, OrderPriority: 80, IgnoreIf: ""));
            EmbeddingsDirParam = T2IParamTypes.Register<string>(new("Embeddings Directory", "Embeddings directory path (`--embd-dir`).\nThis is where SD.cpp can load embedding files from.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 81, IgnoreIf: "", ValidateValues: false));
            TensorTypeParam = T2IParamTypes.Register<string>(new("Weight Type", "Overrides SD.cpp weight type selection (`--type`).\nOnly applies to some model/component load paths. If unsure, leave this disabled.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 82, IgnoreIf: "",
                GetValues: (_) => ["f32", "f16", "q8_0", "q6_K", "q5_0", "q5_1", "q4_0", "q4_1", "q4_K", "q3_K", "q2_K"]));
            TensorTypeRulesParam = T2IParamTypes.Register<string>(new("Tensor Type Rules", "Weight type per tensor pattern (`--tensor-type-rules`).\nExample: `^vae\\.=f16,model\\.=q8_0`.\nThis is an advanced option for per-component quantization control.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 83, IgnoreIf: "", ValidateValues: false));
            LoraApplyModeParam = T2IParamTypes.Register<string>(new("LoRA Apply Mode", "Controls how SD.cpp applies LoRAs (`--lora-apply-mode`).\n'auto' lets SD.cpp decide; other modes trade speed vs compatibility, especially on quantized models.",
                "auto", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 84, IgnoreIf: "auto",
                GetValues: (_) => ["auto", "immediately", "at_runtime"]));
            PhotoMakerModelParam = T2IParamTypes.Register<T2IModel>(new("PhotoMaker Model", "Path to a PhotoMaker model (`--photo-maker`).\nOnly relevant when using PhotoMaker workflows.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, Subtype: "PhotoMaker", IsAdvanced: true, OrderPriority: 85, IgnoreIf: ""));
            PhotoMakerIdImagesDirParam = T2IParamTypes.Register<string>(new("PhotoMaker ID Images Directory", "Directory containing PhotoMaker ID images (`--pm-id-images-dir`).\nOnly relevant when PhotoMaker is enabled.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 86, IgnoreIf: "", ValidateValues: false, DependNonDefault: PhotoMakerModelParam.Type.ID));
            PhotoMakerIdEmbedPathParam = T2IParamTypes.Register<string>(new("PhotoMaker ID Embed Path", "Path to PhotoMaker v2 ID embed file (`--pm-id-embed-path`).\nOnly relevant when PhotoMaker is enabled.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 87, IgnoreIf: "", ValidateValues: false, DependNonDefault: PhotoMakerModelParam.Type.ID));
            PhotoMakerStyleStrengthParam = T2IParamTypes.Register<double>(new("PhotoMaker Style Strength", "PhotoMaker style strength (`--pm-style-strength`).\nOnly relevant when PhotoMaker is enabled.",
                "0", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedModelAddons, IsAdvanced: true, OrderPriority: 88, IgnoreIf: "0", Min: 0, Max: 2, Step: 0.01, ViewType: ParamViewType.SLIDER, DependNonDefault: PhotoMakerModelParam.Type.ID));

            PreviewMethodOverrideParam = T2IParamTypes.Register<string>(new("Preview Method Override", "Overrides SD.cpp preview method (`--preview`).\nIf disabled, the backend uses its own preview setting.\n'none' disables previews for this job.",
                "", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 60, IgnoreIf: "", IsAdvanced: true,
                GetValues: (_) => ["none", "proj", "tae", "vae"]));
            PreviewIntervalParam = T2IParamTypes.Register<int>(new("Preview Interval", "Interval in denoising steps between preview updates (`--preview-interval`).\nLower values update more frequently but may slow generation.",
                "1", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 61, IgnoreIf: "1", IsAdvanced: true, Min: 1, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER, DependNonDefault: PreviewMethodOverrideParam.Type.ID));
            PreviewNoisyParam = T2IParamTypes.Register<bool>(new("Preview Noisy", "Previews noisy model inputs rather than denoised outputs (`--preview-noisy`).\nOnly applies when previews are enabled.",
                "false", IgnoreIf: "false", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 62, IsAdvanced: true, DependNonDefault: PreviewMethodOverrideParam.Type.ID));
            TAESDPreviewOnlyParam = T2IParamTypes.Register<bool>(new("TAESD Preview Only", "Prevents using TAESD for the final image decode (`--taesd-preview-only`).\nTAESD will still be used for previews, but final decode uses the normal VAE.",
                "false", IgnoreIf: "false", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 63, IsAdvanced: true, DependNonDefault: PreviewMethodOverrideParam.Type.ID));

            // ControlNet extra: SD.cpp-side Canny preprocessor
            // Place in the first ControlNet group to match Swarm's ControlNet UI patterns.
            CannyPreprocessorParam = T2IParamTypes.Register<bool>(new("ControlNet Canny Preprocessor", "Applies SD.cpp's built-in Canny edge preprocessor (`--canny`).\nThis affects how the ControlNet input image is processed before being fed into ControlNet.\nOnly enable this if your ControlNet model expects a Canny-style conditioning image.",
                "false", IgnoreIf: "false", Toggleable: true, FeatureFlag: "sdcpp", Group: T2IParamTypes.Controlnets[0].Group, OrderPriority: 5, IsAdvanced: true));

            Logs.Verbose("[SDcpp] Parameters registered successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error registering parameters: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc/>
    public override void OnShutdown()
    {
        Logs.Info("[SDcpp] Shutting down extension");
        Program.ModelRefreshEvent -= OnModelRefresh;
        Program.ModelPathsChangedEvent -= OnModelPathsChanged;
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
