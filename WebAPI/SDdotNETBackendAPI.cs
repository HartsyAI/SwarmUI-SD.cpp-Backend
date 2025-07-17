using Hartsy.Extensions.SDdotNETExtension.SwarmBackends;
using Hartsy.Extensions.VoiceAssistant.SwarmBackends;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.SDdotNETExtension.WebAPI;

/// <summary>Permission definitions for the API endpoints.</summary>
public static class SDdotNETBackendPermissions
{
    // Define permission groups and individual permissions here. These should match the functionality provided by the API.
    // 
    public static readonly PermInfoGroup SDdotNETBackendAPIPermGroup = new("SDdotNETBackend", "Permissions for accessing and managing the StableDiffusion.NET backend API endpoints.");
    //public static readonly PermInfo PermProcessAudio = Permissions.Register(new("voice_process_audio", "Process Audio", "Allows processing of audio through STT, TTS, and workflows.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    //public static readonly PermInfo PermManageBackends = Permissions.Register(new("voice_manage_backends", "Manage Voice Backends", "Allows starting and stopping voice processing backends.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    //public static readonly PermInfo PermCheckStatus = Permissions.Register(new("voice_check_status", "Check Voice Status", "Allows checking the status and health of voice services.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
}

/// <summary></summary>
[API.APIClass("SDdotNETBackendAPIAPI for interacting with teh StableDiffusion.NET backend for image generation.")]
public static class SDdotNETBackendAPI
{
    /// <summary>Registers all API endpoints with appropriate permissions.</summary>
    public static void Register()
    {
        try
        {
            // Register all core endpoints. Each endpoint should have appropriate permission checks and be registered here.
            //API.RegisterAPICall(ProcessSTT, false, SDdotNETBackendPermissions.PermProcessAudio);

            // Register StableDiffusion.NET backend with SwarmUI
            Program.Backends.RegisterBackendType<StableDiffusionDotNETBackend>("sd-dot-net", "StableDiffusion.NET",
                "", false, true);
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDdotNETExtension] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }
    // TODO: Add all methods here to interact with the StableDiffusion.NET backend.
    // Example: StartGeneration, StopGeneration, GetStatus, ListModels, etc.
    // Each method should have appropriate API attributes and permission checks.
    // The endpoints should not do any real work, just call into the backend classes to do the actual processing.

}
