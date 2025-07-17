using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using FreneticUtilities.FreneticDataSyntax;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.SDdotNETExtension.SwarmBackends;

/// <summary>Base class for Voice Assistant backends. Provides common functionality for STT and TTS backends with direct Python integration.</summary>
public abstract class SDdotNETBackends : AbstractT2IBackend
{
    /// <summary>Collection of supported features for this backend</summary>
    protected readonly HashSet<string> SupportedFeatureSet = [];

    /// <summary>Gets the supported features for this backend (implemented by derived classes)</summary>
    public abstract override IEnumerable<string> SupportedFeatures { get; }

    /// <summary>Initialize the voice backend (implemented by derived classes)</summary>
    public abstract override Task Init();

    /// <summary>Load a model for Generation</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Verbose($"[SDdotNETExtension] {GetType().Name} - Loading model: {model.Name}");
            // Validate model compatibility
            if (!IsModelCompatible(model))
            {
                Logs.Warning($"[SDdotNETExtension] {GetType().Name} - Model not compatible: {model.Name}");
                return false;
            }
            CurrentModelName = model.Name;
            Logs.Verbose($"[SDdotNETExtension] {GetType().Name} - Successfully loaded model: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDdotNETExtension] {GetType().Name} - Error loading model: {ex.Message}");
            return false;
        }
    }

    /// <summary>Generate</summary>
    public abstract override Task<Image[]> Generate(T2IParamInput input);

    /// <summary>Check if this backend can handle the specified model</summary>
    public virtual bool IsModelCompatible(T2IModel model)
    {
        if (model == null) return false;
        string modelName = model.Name?.ToLowerInvariant();
        return true; // TODO: Implement actual compatibility checks based on model metadata or naming conventions
    }

    /// <summary>Free memory and resources</summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        try
        {
            Logs.Debug($"[SDdotNETExtension] {GetType().Name} - Freeing memory (systemRam: {systemRam})");
            bool success = true; // TODO: Implement actual memory cleanup logic if needed. Does Swarm handle this?
            return success;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDdotNETExtension] {GetType().Name} - Failed to free memory: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shutdown the backend</summary>
    public override async Task Shutdown()
    {
        try
        {
            Logs.Info($"[SDdotNETExtension] {GetType().Name} - Shutting down backend");
            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDdotNETExtension] {GetType().Name} - Error during shutdown: {ex.Message}");
        }
    }

    /// <summary>Get current backend status information</summary>
    public virtual async Task<Dictionary<string, object>> GetBackendStatusAsync()
    {
        try
        {
            return [];
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDdotNETExtension] {GetType().Name} - Error getting backend status: {ex.Message}");
            return [];
        }
    }
}
