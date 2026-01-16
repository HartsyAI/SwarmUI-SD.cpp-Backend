using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Manages downloading and installing stable-diffusion.cpp prebuilt binaries from GitHub releases.</summary>
public static class SDcppDownloadManager
{
    public const string GITHUB_API_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";
    public const string FALLBACK_RELEASE_TAG = "master-471-7010bb4";
    public const string VERSION_FILE = "sdcpp_version.json";

    /// <summary>Ensures SD.cpp binary is available, downloading if needed.</summary>
    public static async Task<string> EnsureSDcppAvailable(string currentPath, string deviceType, string cudaVersion, bool autoUpdate)
    {
        deviceType = (deviceType ?? "cpu").ToLowerInvariant();
        string resolvedCuda = deviceType is "cuda" ? ResolveCudaVersion(cudaVersion) : null;
        string deviceDir = GetDeviceDirectory(deviceType, resolvedCuda);
        string versionPath = Path.Combine(deviceDir, VERSION_FILE);
        VersionInfo versionInfo = LoadVersionInfo(versionPath);
        string executable = null;
        if (!string.IsNullOrEmpty(versionInfo?.ExecutablePath) && File.Exists(versionInfo.ExecutablePath))
        {
            executable = versionInfo.ExecutablePath;
        }
        if (executable is not null)
        {
            if (autoUpdate && await ShouldUpdate(versionPath, deviceType, resolvedCuda))
            {
                string updated = await DownloadBinary(deviceType, resolvedCuda, deviceDir, versionPath);
                return updated ?? executable;
            }
            return executable;
        }
        if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
        {
            return currentPath;
        }
        Directory.CreateDirectory(deviceDir);
        Logs.Info($"[SDcpp] Downloading binary for {deviceType}{(resolvedCuda != null ? $" cuda{resolvedCuda}" : "")}...");
        return await DownloadBinary(deviceType, resolvedCuda, deviceDir, versionPath);
    }

    /// <summary>Downloads and installs the SD.cpp binary.</summary>
    public static async Task<string> DownloadBinary(string deviceType, string cudaVersion, string targetDir, string versionPath)
    {
        DownloadInfo info = await GetDownloadInfo(deviceType, cudaVersion);
        if (info is null)
        {
            Logs.Error("[SDcpp] Could not determine download URL");
            return null;
        }
        string executable = await DownloadAndExtract(info, targetDir);
        if (executable is null) return null;
        SaveVersionInfo(versionPath, new VersionInfo
        {
            TagName = info.TagName,
            DeviceType = deviceType,
            ExecutablePath = executable,
            InstalledDate = DateTime.UtcNow,
            LastUpdateCheck = DateTime.UtcNow
        });
        Logs.Info($"[SDcpp] Installed version {info.TagName}");
        return executable;
    }

    /// <summary>Gets download info from GitHub, falling back to hardcoded URL on failure.</summary>
    public static async Task<DownloadInfo> GetDownloadInfo(string deviceType, string cudaVersion)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, GITHUB_API_URL);
            request.Headers.Add("User-Agent", "SwarmUI-SDcpp-Extension");
            using HttpResponseMessage response = await Utilities.UtilWebClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logs.Warning($"[SDcpp] GitHub API returned {response.StatusCode}, using fallback");
                return GetFallbackInfo(deviceType, cudaVersion);
            }
            JObject release = JObject.Parse(await response.Content.ReadAsStringAsync());
            string tag = release["tag_name"]?.ToString();
            JArray assets = release["assets"] as JArray;
            if (assets is null || assets.Count is 0)
            {
                Logs.Warning("[SDcpp] No assets in release, using fallback");
                return GetFallbackInfo(deviceType, cudaVersion);
            }
            JToken asset = FindMatchingAsset(assets, deviceType, cudaVersion);
            if (asset is null)
            {
                Logs.Warning("[SDcpp] No matching asset found, using fallback");
                return GetFallbackInfo(deviceType, cudaVersion);
            }
            return new DownloadInfo
            {
                FileName = asset["name"]?.ToString(),
                DownloadUrl = asset["browser_download_url"]?.ToString(),
                Size = asset["size"]?.ToObject<long>() ?? 0,
                TagName = tag
            };
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] GitHub API error: {ex.Message}, using fallback");
            return GetFallbackInfo(deviceType, cudaVersion);
        }
    }

    /// <summary>Downloads and extracts the binary archive.</summary>
    public static async Task<string> DownloadAndExtract(DownloadInfo info, string targetDir)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SwarmUI-SDcpp");
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, info.FileName);
        string extractDir = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(info.FileName));
        string expectedExe = Path.Combine(targetDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sd-cli.exe" : "sd-cli");
        try
        {
            Logs.Info($"[SDcpp] Downloading {info.FileName} ({info.Size / 1024.0 / 1024.0:F1} MB)...");
            await Utilities.DownloadFile(info.DownloadUrl, zipPath, (_, __, ___) => { });
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            {
                Logs.Error("[SDcpp] Download failed");
                return null;
            }
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            CopyDirectory(extractDir, targetDir);
            if (!File.Exists(expectedExe))
            {
                Logs.Error($"[SDcpp] Expected executable missing after extraction: {expectedExe}");
                return null;
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { Process.Start("chmod", $"+x \"{expectedExe}\"")?.WaitForExit(); } catch { }
            }
            return expectedExe;
        }
        finally
        {
            try { File.Delete(zipPath); Directory.Delete(extractDir, true); } catch { }
        }
    }

    /// <summary>Checks if an update is available (once per 24h).</summary>
    public static async Task<bool> ShouldUpdate(string versionPath, string deviceType, string cudaVersion)
    {
        VersionInfo current = LoadVersionInfo(versionPath);
        if (current is null) return true;
        if (DateTime.UtcNow - current.LastUpdateCheck < TimeSpan.FromHours(24)) return false;
        try
        {
            DownloadInfo latest = await GetDownloadInfo(deviceType, cudaVersion);
            current.LastUpdateCheck = DateTime.UtcNow;
            SaveVersionInfo(versionPath, current);
            if (latest?.TagName is null) return false;
            bool newer = current.TagName != latest.TagName;
            if (newer) Logs.Info($"[SDcpp] Update available: {current.TagName} -> {latest.TagName}");
            return newer;
        }
        catch { return false; }
    }

    #region Helpers

    public static string ResolveCudaVersion(string setting)
    {
        setting = (setting ?? "auto").ToLowerInvariant().Trim();
        return setting is "11" or "12" ? setting : "12";
    }

    public static string GetDeviceDirectory(string deviceType, string cudaVersion)
    {
        string baseDir = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, "dlbackend", "sdcpp");
        return deviceType is "cuda" ? Path.Combine(baseDir, $"cuda{cudaVersion}") : Path.Combine(baseDir, deviceType);
    }

    public static JToken FindMatchingAsset(JArray assets, string deviceType, string cudaVersion)
    {
        string pattern = GetAssetPattern(deviceType, cudaVersion);
        if (pattern is null) return null;
        foreach (JToken asset in assets)
        {
            string name = asset["name"]?.ToString();
            if (name is not null && name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return asset;
        }
        return null;
    }

    public static string GetAssetPattern(string deviceType, string cudaVersion)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                "cuda" => $"win-cuda{cudaVersion}-x64",
                "vulkan" => "win-vulkan-x64",
                _ => "win-avx2-x64"
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "Darwin-macOS";
        return null;
    }

    public static DownloadInfo GetFallbackInfo(string deviceType, string cudaVersion)
    {
        string baseUrl = $"https://github.com/leejet/stable-diffusion.cpp/releases/download/{FALLBACK_RELEASE_TAG}/";
        (string fileName, long size) = GetFallbackAsset(deviceType, cudaVersion);
        if (fileName is null) return null;
        return new DownloadInfo
        {
            FileName = fileName,
            DownloadUrl = baseUrl + fileName,
            Size = size,
            TagName = FALLBACK_RELEASE_TAG
        };
    }

    public static (string fileName, long size) GetFallbackAsset(string deviceType, string cudaVersion)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                "cuda" => ($"sd-{FALLBACK_RELEASE_TAG}-bin-win-cuda{cudaVersion}-x64.zip", 48_000_000),
                "vulkan" => ($"sd-{FALLBACK_RELEASE_TAG}-bin-win-vulkan-x64.zip", 7_000_000),
                _ => ($"sd-{FALLBACK_RELEASE_TAG}-bin-win-avx2-x64.zip", 2_000_000)
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return ("sd-master--bin-Linux-Ubuntu-24.04-x86_64.zip", 2_400_000);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return ("sd-master--bin-Darwin-macOS-14.7.6-arm64.zip", 4_300_000);
        return (null, 0);
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (string dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    public static VersionInfo LoadVersionInfo(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path)).ToObject<VersionInfo>();
        }
        catch { return null; }
    }

    public static void SaveVersionInfo(string path, VersionInfo info)
    {
        try { File.WriteAllText(path, JObject.FromObject(info).ToString(Newtonsoft.Json.Formatting.Indented)); }
        catch (Exception ex) { Logs.Warning($"[SDcpp] Failed to save version info: {ex.Message}"); }
    }

    #endregion

    public class VersionInfo
    {
        public string TagName { get; set; }
        public string DeviceType { get; set; }
        public string ExecutablePath { get; set; }
        public DateTime InstalledDate { get; set; }
        public DateTime LastUpdateCheck { get; set; }
    }

    public class DownloadInfo
    {
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }
        public string TagName { get; set; }
    }
}
