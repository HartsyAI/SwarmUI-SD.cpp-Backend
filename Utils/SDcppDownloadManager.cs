using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Manages downloading and installing stable-diffusion.cpp prebuilt binaries from GitHub releases.</summary>
public static class SDcppDownloadManager
{
    public const string GITHUB_API_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";
    public const string FALLBACK_RELEASE_TAG = "master-505-c5eb1e4";
    /// <summary>Short commit hash used in filenames (release tags are "master-NUM-SHA" but filenames are "sd-master-SHA-bin-...").</summary>
    public const string FALLBACK_COMMIT_SHA = "c5eb1e4";
    public const string VERSION_FILE = "sdcpp_version.json";

    public static string BuildFromSourceMessage(string deviceType)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && deviceType is "cuda")
        {
            return "No prebuilt CUDA binary is available for Linux. Options:\n" +
                "  1. Use 'GPU (Vulkan)' device — requires libvulkan1 (apt-get install libvulkan1 libvulkan-dev)\n" +
                "  2. Use 'CPU' device — works everywhere but is slower\n" +
                "  3. Build stable-diffusion.cpp from source with CUDA support and configure the executable path manually";
        }
        return $"No prebuilt SD.cpp binary is available for the requested device '{deviceType}' on this platform. If you want to use that backend, you will need to build stable-diffusion.cpp from source and configure the executable path manually.";
    }

    /// <summary>Ensures SD.cpp binary is available, downloading if needed.</summary>
    public static async Task<string> EnsureSDcppAvailable(string currentPath, string deviceType, string cudaVersion, bool autoUpdate)
    {
        deviceType = (deviceType ?? "cpu").ToLowerInvariant();
        deviceType = deviceType is "cpu" ? ResolveCpuDeviceType() : deviceType;
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
            if (!autoUpdate)
            {
                await WarnIfNotLatest(versionPath, deviceType, resolvedCuda);
            }
            if (autoUpdate && await ShouldUpdate(versionPath, deviceType, resolvedCuda))
            {
                string updated = await DownloadBinary(deviceType, resolvedCuda, deviceDir, versionPath);
                return updated ?? executable;
            }
            return executable;
        }
        if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath) && currentPath.StartsWith(deviceDir, StringComparison.OrdinalIgnoreCase))
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
            Logs.Warning($"[SDcpp] Could not determine download URL for device '{deviceType}'. {BuildFromSourceMessage(deviceType)}");
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
                string[] patterns = GetAssetPatterns(deviceType, cudaVersion);
                string patternsText = patterns is null || patterns.Length == 0 ? "<none>" : string.Join(", ", patterns);
                Logs.Warning($"[SDcpp] No matching prebuilt asset found in latest release for device '{deviceType}'. Patterns tried: {patternsText}");
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

    public static async Task WarnIfNotLatest(string versionPath, string deviceType, string cudaVersion)
    {
        VersionInfo current = LoadVersionInfo(versionPath);
        if (current is null)
        {
            return;
        }
        if (DateTime.UtcNow - current.LastUpdateCheck < TimeSpan.FromHours(24))
        {
            return;
        }
        try
        {
            DownloadInfo latest = await GetDownloadInfo(deviceType, cudaVersion);
            current.LastUpdateCheck = DateTime.UtcNow;
            SaveVersionInfo(versionPath, current);
            if (latest?.TagName is null)
            {
                return;
            }
            if (current.TagName != latest.TagName)
            {
                Logs.Warning($"[SDcpp] Auto-update is disabled. Installed SD.cpp version is '{current.TagName}', latest is '{latest.TagName}'. Enable Auto-Update to download the latest prebuilt binary.");
            }
        }
        catch
        {
            // ignore update check failures
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

    public static string ResolveCpuDeviceType()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return "cpu";
        }
        if (Avx512F.IsSupported)
        {
            return "cpu-avx512";
        }
        if (Avx2.IsSupported)
        {
            return "cpu-avx2";
        }
        if (Avx.IsSupported)
        {
            return "cpu-avx";
        }
        return "cpu-noavx";
    }

    public static string GetDeviceDirectory(string deviceType, string cudaVersion)
    {
        string baseDir = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, "dlbackend", "sdcpp");
        return deviceType is "cuda" ? Path.Combine(baseDir, $"cuda{cudaVersion}") : Path.Combine(baseDir, deviceType);
    }

    public static JToken FindMatchingAsset(JArray assets, string deviceType, string cudaVersion)
    {
        string[] patterns = GetAssetPatterns(deviceType, cudaVersion);
        if (patterns is null || patterns.Length == 0) return null;
        foreach (string pattern in patterns)
        {
            foreach (JToken asset in assets)
            {
                string name = asset["name"]?.ToString();
                if (name is not null && name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }
        }
        return null;
    }

    public static string[] GetAssetPatterns(string deviceType, string cudaVersion)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                // Modern releases (2026-01) use sd-master-<sha>-bin-win-cuda12-x64.zip.
                // Some releases also ship cudart-sd-bin-win-cu12-x64.zip.
                "cuda" => cudaVersion switch
                {
                    "11" => [$"bin-win-cuda11-x64", $"cudart-sd-bin-win-cu11-x64"],
                    _ => [$"bin-win-cuda12-x64", $"cudart-sd-bin-win-cu12-x64"]
                },
                "vulkan" => ["bin-win-vulkan-x64"],
                "rocm" => ["bin-win-rocm-x64"],
                "metal" => ["bin-win-metal-x64"],
                "opencl" => ["bin-win-opencl-x64"],
                "sycl" => ["bin-win-sycl-x64"],
                "cpu-avx512" => ["bin-win-avx512-x64"],
                "cpu-avx2" => ["bin-win-avx2-x64"],
                "cpu-avx" => ["bin-win-avx-x64"],
                "cpu-noavx" => ["bin-win-noavx-x64"],
                _ => ["bin-win-avx2-x64"]
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return deviceType switch
            {
                // No prebuilt CUDA binary exists for Linux — sd.cpp releases only provide Vulkan and ROCm GPU builds.
                // Return empty so the caller gets a clear "not available" message instead of silently using a CPU binary.
                "cuda" => [],
                "vulkan" => ["bin-Linux-Ubuntu-24.04-x86_64-vulkan"],
                "rocm" => ["bin-Linux-Ubuntu-24.04-x86_64-rocm"],
                // OpenCL and SYCL have no prebuilt Linux binaries — return empty for clear error messaging
                "opencl" => [],
                "sycl" => [],
                _ => ["bin-Linux-Ubuntu-24.04-x86_64"]
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS releases include version and arch in the filename (e.g. sd-master-<sha>-bin-Darwin-macOS-15.7.3-arm64.zip)
            // Match on the stable prefix.
            return ["bin-Darwin-macOS"]; 
        }
        return [];
    }

    public static DownloadInfo GetFallbackInfo(string deviceType, string cudaVersion)
    {
        string baseUrl = $"https://github.com/leejet/stable-diffusion.cpp/releases/download/{FALLBACK_RELEASE_TAG}/";
        (string fileName, long size) = GetFallbackAsset(deviceType, cudaVersion);
        if (fileName is null)
        {
            Logs.Warning($"[SDcpp] No fallback prebuilt binary is known for device '{deviceType}'. {BuildFromSourceMessage(deviceType)}");
            return null;
        }
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
        // NOTE: Release tags use format "master-NUM-SHA" but filenames use "sd-master-SHA-bin-..."
        string sha = FALLBACK_COMMIT_SHA;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                "cuda" => ($"sd-master-{sha}-bin-win-cuda{cudaVersion}-x64.zip", 326_000_000),
                "vulkan" => ($"sd-master-{sha}-bin-win-vulkan-x64.zip", 24_000_000),
                "rocm" => ($"sd-master-{sha}-bin-win-rocm-x64.zip", 344_000_000),
                "cpu-avx512" => ($"sd-master-{sha}-bin-win-avx512-x64.zip", 8_400_000),
                "cpu-avx" => ($"sd-master-{sha}-bin-win-avx-x64.zip", 8_400_000),
                "cpu-noavx" => ($"sd-master-{sha}-bin-win-noavx-x64.zip", 8_400_000),
                _ => ($"sd-master-{sha}-bin-win-avx2-x64.zip", 8_400_000)
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return deviceType switch
            {
                "cuda" => (null, 0), // No prebuilt CUDA binary for Linux
                "vulkan" => ($"sd-master-{sha}-bin-Linux-Ubuntu-24.04-x86_64-vulkan.zip", 22_700_000),
                "rocm" => ($"sd-master-{sha}-bin-Linux-Ubuntu-24.04-x86_64-rocm.zip", 868_000_000),
                _ => ($"sd-master-{sha}-bin-Linux-Ubuntu-24.04-x86_64.zip", 9_800_000)
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return ($"sd-master-{sha}-bin-Darwin-macOS-15.7.3-arm64.zip", 18_700_000);
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
