using SwarmUI.Utils;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Text;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>Manages downloading and installing stable-diffusion.cpp prebuilt binaries from GitHub releases.</summary>
public static class SDcppDownloadManager
{
    private static readonly string[] WindowsExecutableCandidates = [
        "sd.exe",
        "stable-diffusion.exe",
        "stable-diffusion-cpp.exe",
        "stable-diffusion.cpp.exe"
    ];

    private static readonly string[] UnixExecutableCandidates = [
        "sd",
        "stable-diffusion",
        "stable-diffusion.cpp",
        "stable-diffusion-cpp"
    ];

    /// <summary>GitHub API endpoint for latest SD.cpp releases</summary>
    private const string GITHUB_API_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";

    /// <summary>GitHub API endpoint for listing SD.cpp releases (newest-first)</summary>
    private const string GITHUB_RELEASES_LIST_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases?per_page=20";

    /// <summary>Known working release tag for fallback when API is unavailable</summary>
    private const string FALLBACK_RELEASE_TAG = "master-471-7010bb4";

    /// <summary>Version info file to track installed SD.cpp version</summary>
    private const string VERSION_INFO_FILE = "sdcpp_version.json";

    /// <summary>Resolves the CUDA version to use based on user setting.</summary>
    /// <param name="cudaVersionSetting">User's CUDA version preference: "auto", "11", or "12"</param>
    /// <returns>Resolved CUDA version string ("11" or "12"), defaults to "12" for auto</returns>
    public static string ResolveCudaVersion(string cudaVersionSetting)
    {
        cudaVersionSetting = (cudaVersionSetting ?? "auto").ToLowerInvariant().Trim();
        if (cudaVersionSetting == "11" || cudaVersionSetting == "12")
        {
            Logs.Info($"[SDcpp] Using explicitly configured CUDA version: {cudaVersionSetting}");
            return cudaVersionSetting;
        }
        Logs.Info("[SDcpp] Auto mode: defaulting to CUDA 12 build");
        return "12";
    }

    /// <summary>Ensures SD.cpp is available for the current platform by downloading the prebuilt binary. Checks for updates and automatically downloads newer versions if enabled in settings.</summary>
    /// <param name="currentExecutablePath">Current configured executable path (if any)</param>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which binary to download</param>
    /// <param name="cudaVersion">CUDA version preference: "auto", "11", or "12" (only used when deviceType is "cuda")</param>
    /// <param name="autoUpdate">Whether to automatically check for and download updates (default: true)</param>
    /// <returns>Path to the SD.cpp executable</returns>
    public static async Task<string> EnsureSDcppAvailable(string currentExecutablePath, string deviceType, string cudaVersion, bool autoUpdate)
    {
        try
        {
            deviceType = (deviceType ?? "cpu").ToLowerInvariant();
            string resolvedCudaVersion = null;
            if (deviceType == "cuda")
            {
                resolvedCudaVersion = ResolveCudaVersion(cudaVersion);
                Logs.Info($"[SDcpp] Resolved CUDA version: {resolvedCudaVersion}");
            }
            string sdcppDir = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, "dlbackend", "sdcpp");
            string deviceDir = deviceType == "cuda" 
                ? Path.Combine(sdcppDir, $"cuda{resolvedCudaVersion}") 
                : Path.Combine(sdcppDir, deviceType);
            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sd.exe" : "sd";
            string expectedExecutable = Path.Combine(deviceDir, executableName);
            string versionInfoPath = Path.Combine(deviceDir, VERSION_INFO_FILE);
            if (File.Exists(expectedExecutable))
            {
                Logs.Info($"[SDcpp] Found existing SD.cpp executable: {expectedExecutable}");
                if (autoUpdate)
                {
                    bool shouldUpdate = await ShouldUpdateBinary(versionInfoPath, deviceType, resolvedCudaVersion);
                    if (shouldUpdate)
                    {
                        Logs.Info("[SDcpp] Newer version available, downloading update...");
                        try
                        {
                            string updatedExecutable = await DownloadLatestVersion(deviceType, resolvedCudaVersion, deviceDir, versionInfoPath);
                            if (!string.IsNullOrEmpty(updatedExecutable))
                            {
                                return updatedExecutable;
                            }
                        }
                        catch
                        {
                            Logs.Warning("[SDcpp] Update failed, continuing with existing version");
                            return expectedExecutable;
                        }
                    }
                }
                else
                {
                    Logs.Debug("[SDcpp] Auto-update disabled, skipping update check");
                }
                return expectedExecutable;
            }
            if (Directory.Exists(deviceDir))
            {
                string existing = FindBestExecutableInDirectory(deviceDir);
                if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
                {
                    Logs.Info($"[SDcpp] Found existing SD.cpp executable (non-standard name): {existing}");
                    return existing;
                }
            }
            if (!string.IsNullOrEmpty(currentExecutablePath) && File.Exists(currentExecutablePath))
            {
                string execDir = Path.GetDirectoryName(currentExecutablePath) ?? "";
                string expectedDirName = deviceType == "cuda" ? $"cuda{resolvedCudaVersion}" : deviceType;
                bool isDeviceMatch = execDir.EndsWith(expectedDirName, StringComparison.OrdinalIgnoreCase) ||
                                     !execDir.Contains("sdcpp");
                if (isDeviceMatch)
                {
                    Logs.Info($"[SDcpp] Using user-specified executable: {currentExecutablePath}");
                    return currentExecutablePath;
                }
                else
                {
                    Logs.Info($"[SDcpp] Ignoring cached executable (wrong device/CUDA version): {currentExecutablePath}");
                    Logs.Info($"[SDcpp] Need to download {deviceType} binary...");
                }
            }
            Directory.CreateDirectory(deviceDir);
            string deviceDesc = deviceType == "cuda" ? $"{deviceType} {resolvedCudaVersion}" : deviceType;
            Logs.Info($"[SDcpp] SD.cpp not found. Downloading prebuilt binary for {deviceDesc}...");
            string downloadedExecutable = await DownloadLatestVersion(deviceType, resolvedCudaVersion, deviceDir, versionInfoPath);
            if (string.IsNullOrEmpty(downloadedExecutable))
            {
                Logs.Error("[SDcpp] Failed to download SD.cpp binary.");
                return null;
            }
            Logs.Info($"[SDcpp] SD.cpp installed successfully: {downloadedExecutable}");
            return downloadedExecutable;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error ensuring SD.cpp availability: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gets download information for the current platform from GitHub releases. Falls back to querying the GitHub /releases list and selecting the newest release asset matching the requested device/platform when /releases/latest fails or has no matching assets.</summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    /// <param name="etag">ETag from previous release query, if any</param>
    /// <returns>Tuple of download info, whether the release data is unchanged, and the new ETag (if available).</returns>
    public static async Task<(DownloadInfo Info, bool NotModified, string ETag)> GetDownloadInfoWithCache(string deviceType, string cudaVersion = "12", string etag = null)
    {
        try
        {
            deviceType = (deviceType ?? "cpu").ToLowerInvariant();
            cudaVersion = (cudaVersion ?? "12").ToLowerInvariant();

            Logs.Info($"[SDcpp] Fetching latest release from GitHub (device={deviceType}, cuda={cudaVersion})...");
            using HttpRequestMessage request = new(HttpMethod.Get, GITHUB_API_URL);
            request.Headers.Add("User-Agent", "SwarmUI-SDcpp-Extension");
            if (!string.IsNullOrEmpty(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            using HttpResponseMessage responseMessage = await Utilities.UtilWebClient.SendAsync(request);
            Logs.Debug($"[SDcpp] GitHub latest release HTTP status: {(int)responseMessage.StatusCode} {responseMessage.StatusCode}");
            if (responseMessage.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                Logs.Debug("[SDcpp] GitHub release data unchanged (ETag match)");
                return (null, true, etag);
            }
            if (responseMessage.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                string remaining = responseMessage.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string> values)
                    ? values.FirstOrDefault()
                    : null;
                if (remaining == "0")
                {
                    Logs.Warning("[SDcpp] GitHub API rate limit exceeded, skipping update check");
                    return (null, false, etag);
                }

                Logs.Warning($"[SDcpp] GitHub latest release query forbidden (rate-limit remaining: {remaining ?? "unknown"}) - attempting fallback release list query");
                (DownloadInfo forbiddenFallbackInfo, bool forbiddenFallbackNotModified, string forbiddenFallbackEtag) = await TrySelectFromReleasesList(deviceType, cudaVersion, etag);
                if (forbiddenFallbackNotModified)
                {
                    return (null, true, etag);
                }
                if (forbiddenFallbackInfo is not null)
                {
                    return (forbiddenFallbackInfo, false, forbiddenFallbackEtag ?? etag);
                }

                Logs.Warning("[SDcpp] Fallback release list query did not yield a usable asset; using hardcoded fallback download info");
                return (GetFallbackDownloadInfo(deviceType, cudaVersion), false, etag);
            }

            responseMessage.EnsureSuccessStatusCode();
            string response = await responseMessage.Content.ReadAsStringAsync();
            string responseEtag = responseMessage.Headers.ETag?.Tag ?? etag;

            JObject releaseData = JObject.Parse(response);
            string tagName = releaseData["tag_name"]?.ToString();
            JArray assets = releaseData["assets"] as JArray;
            if (assets == null || assets.Count == 0)
            {
                Logs.Warning("[SDcpp] No assets found in latest release; attempting fallback release list query...");
                (DownloadInfo listFallbackInfo, _, string listFallbackEtag) = await TrySelectFromReleasesList(deviceType, cudaVersion, responseEtag);
                if (listFallbackInfo is not null)
                {
                    return (listFallbackInfo, false, listFallbackEtag ?? responseEtag);
                }
                Logs.Warning("[SDcpp] Fallback release list query did not yield a usable asset; using hardcoded fallback");
                return (GetFallbackDownloadInfo(deviceType, cudaVersion), false, responseEtag);
            }

            // Try to pick the right asset out of the latest release.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                JToken linuxAsset = FindLinuxAsset(assets, deviceType, cudaVersion);
                if (linuxAsset != null)
                {
                    DownloadInfo info = CreateDownloadInfoFromAsset(linuxAsset, tagName, responseEtag);
                    Logs.Info($"[SDcpp] Selected SD.cpp asset from latest release: tag={info.TagName}, asset={info.FileName}");
                    return (info, false, responseEtag);
                }

                Logs.Warning("[SDcpp] No matching Linux asset in latest release; attempting fallback release list query...");
                (DownloadInfo listFallbackInfo, _, string listFallbackEtag) = await TrySelectFromReleasesList(deviceType, cudaVersion, responseEtag);
                if (listFallbackInfo is not null)
                {
                    return (listFallbackInfo, false, listFallbackEtag ?? responseEtag);
                }
                Logs.Warning("[SDcpp] No matching Linux asset found from release list; using hardcoded fallback");
                return (GetFallbackDownloadInfo(deviceType, cudaVersion), false, responseEtag);
            }

            string assetPattern = GetPlatformAssetPattern(deviceType, cudaVersion);
            if (string.IsNullOrEmpty(assetPattern))
            {
                Logs.Warning("[SDcpp] Unsupported platform for automatic download (no asset pattern)");
                return (null, false, responseEtag);
            }

            foreach (JToken asset in assets)
            {
                string name = asset["name"]?.ToString();
                if (name != null && name.Contains(assetPattern, StringComparison.OrdinalIgnoreCase))
                {
                    DownloadInfo info = CreateDownloadInfoFromAsset(asset, tagName, responseEtag);
                    Logs.Info($"[SDcpp] Selected SD.cpp asset from latest release: tag={info.TagName}, asset={info.FileName}");
                    return (info, false, responseEtag);
                }
            }

            Logs.Warning($"[SDcpp] No matching asset for pattern '{assetPattern}' in latest release; attempting fallback release list query...");
            (DownloadInfo listFallbackInfo2, _, string listFallbackEtag2) = await TrySelectFromReleasesList(deviceType, cudaVersion, responseEtag);
            if (listFallbackInfo2 is not null)
            {
                return (listFallbackInfo2, false, listFallbackEtag2 ?? responseEtag);
            }
            Logs.Warning("[SDcpp] No matching asset found from release list; using hardcoded fallback");
            return (GetFallbackDownloadInfo(deviceType, cudaVersion), false, responseEtag);
        }
        catch (HttpRequestException ex)
        {
            Logs.Warning($"[SDcpp] GitHub latest-release API request failed ({ex.Message}), attempting fallback release list query...");
            (DownloadInfo fallbackInfo, _, string fallbackEtag) = await TrySelectFromReleasesList((deviceType ?? "cpu").ToLowerInvariant(), (cudaVersion ?? "12").ToLowerInvariant(), etag);
            if (fallbackInfo is not null)
            {
                return (fallbackInfo, false, fallbackEtag ?? etag);
            }
            Logs.Warning("[SDcpp] Fallback release list query failed; using hardcoded fallback URLs...");
            return (GetFallbackDownloadInfo(deviceType, cudaVersion), false, etag);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error fetching latest release info ({ex.Message}), attempting fallback release list query...");
            (DownloadInfo fallbackInfo, _, string fallbackEtag) = await TrySelectFromReleasesList((deviceType ?? "cpu").ToLowerInvariant(), (cudaVersion ?? "12").ToLowerInvariant(), etag);
            if (fallbackInfo is not null)
            {
                return (fallbackInfo, false, fallbackEtag ?? etag);
            }
            Logs.Warning("[SDcpp] Fallback release list query failed; using hardcoded fallback URLs...");
            return (GetFallbackDownloadInfo(deviceType, cudaVersion), false, etag);
        }
    }

    /// <summary>Gets download information for the current platform from GitHub releases.</summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    public static async Task<DownloadInfo> GetDownloadInfo(string deviceType, string cudaVersion = "12")
    {
        (DownloadInfo info, _, _) = await GetDownloadInfoWithCache(deviceType, cudaVersion, null);
        return info;
    }

    private static async Task<(DownloadInfo Info, bool NotModified, string ETag)> TrySelectFromReleasesList(string deviceType, string cudaVersion, string etag)
    {
        try
        {
            Logs.Info($"[SDcpp] Fetching release list from GitHub for fallback selection (device={deviceType}, cuda={cudaVersion})...");
            using HttpRequestMessage request = new(HttpMethod.Get, GITHUB_RELEASES_LIST_URL);
            request.Headers.Add("User-Agent", "SwarmUI-SDcpp-Extension");
            if (!string.IsNullOrEmpty(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            using HttpResponseMessage responseMessage = await Utilities.UtilWebClient.SendAsync(request);
            Logs.Debug($"[SDcpp] GitHub releases list HTTP status: {(int)responseMessage.StatusCode} {responseMessage.StatusCode}");
            if (responseMessage.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                Logs.Debug("[SDcpp] GitHub release list data unchanged (ETag match)");
                return (null, true, etag);
            }
            if (responseMessage.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                string remaining = responseMessage.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string> values)
                    ? values.FirstOrDefault()
                    : null;
                Logs.Warning($"[SDcpp] GitHub release list query forbidden (rate-limit remaining: {remaining ?? "unknown"})");
                return (null, false, etag);
            }
            responseMessage.EnsureSuccessStatusCode();
            string response = await responseMessage.Content.ReadAsStringAsync();
            string responseEtag = responseMessage.Headers.ETag?.Tag ?? etag;
            JArray releases = JArray.Parse(response);
            if (releases is null || releases.Count == 0)
            {
                Logs.Warning("[SDcpp] GitHub releases list returned no releases");
                return (null, false, responseEtag);
            }

            foreach (JToken rel in releases)
            {
                string tagName = rel["tag_name"]?.ToString();
                if (string.IsNullOrEmpty(tagName))
                {
                    continue;
                }

                JArray assets = rel["assets"] as JArray;
                if (assets is null || assets.Count == 0)
                {
                    Logs.Debug($"[SDcpp] Release '{tagName}' has no assets, skipping");
                    continue;
                }

                JToken asset = null;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    asset = FindLinuxAsset(assets, deviceType, cudaVersion);
                }
                else
                {
                    string assetPattern = GetPlatformAssetPattern(deviceType, cudaVersion);
                    if (string.IsNullOrEmpty(assetPattern))
                    {
                        Logs.Warning("[SDcpp] Unsupported platform for automatic download (no asset pattern)");
                        return (null, false, responseEtag);
                    }
                    foreach (JToken a in assets)
                    {
                        string name = a["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name) && name.Contains(assetPattern, StringComparison.OrdinalIgnoreCase))
                        {
                            asset = a;
                            break;
                        }
                    }
                }

                if (asset is not null)
                {
                    DownloadInfo info = CreateDownloadInfoFromAsset(asset, tagName, responseEtag);
                    Logs.Info($"[SDcpp] Selected SD.cpp asset from release list: tag={info.TagName}, asset={info.FileName}");
                    return (info, false, responseEtag);
                }

                Logs.Debug($"[SDcpp] Release '{tagName}' did not contain a matching asset for device={deviceType}, cuda={cudaVersion}");
            }

            Logs.Warning($"[SDcpp] No matching assets found in the newest {releases.Count} releases (device={deviceType}, cuda={cudaVersion})");
            return (null, false, responseEtag);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error querying release list: {ex.Message}");
            return (null, false, etag);
        }
    }

    /// <summary>Returns the asset name pattern to search for based on platform and device type.</summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    public static string GetPlatformAssetPattern(string deviceType, string cudaVersion = "12")
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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return deviceType switch
            {
                "cuda" => "Linux",  // Will need to match cuda in name too
                _ => "Linux"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "Darwin-macOS";
        }
        return null;
    }

    private static JToken FindLinuxAsset(JArray assets, string deviceType, string cudaVersion)
    {
        string normalizedDevice = (deviceType ?? "cpu").ToLowerInvariant();
        List<JToken> linuxAssets = [.. assets
            .Where(asset =>
            {
                string name = asset["name"]?.ToString();
                return !string.IsNullOrEmpty(name) && name.Contains("linux", StringComparison.OrdinalIgnoreCase);
            })];
        if (!linuxAssets.Any())
        {
            Logs.Debug("[SDcpp] No Linux assets found in release");
            return null;
        }

        Logs.Debug($"[SDcpp] Linux assets in release: {string.Join(", ", linuxAssets.Select(a => a[\"name\"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)))}");

        if (normalizedDevice == "cuda")
        {
            string target = $"cuda{cudaVersion}";
            JToken best = linuxAssets.FirstOrDefault(asset =>
            {
                string name = asset["name"]?.ToString();
                return !string.IsNullOrEmpty(name) && name.Contains(target, StringComparison.OrdinalIgnoreCase);
            });
            if (best is not null)
            {
                Logs.Debug($"[SDcpp] Matched Linux CUDA asset by exact token '{target}'");
                return best;
            }
            best = linuxAssets.FirstOrDefault(asset =>
            {
                string name = asset["name"]?.ToString();
                return !string.IsNullOrEmpty(name) && name.Contains("cuda", StringComparison.OrdinalIgnoreCase);
            });

            if (best is not null)
            {
                Logs.Debug("[SDcpp] Matched Linux CUDA asset by generic token 'cuda'");
                return best;
            }

            Logs.Warning($"[SDcpp] No Linux CUDA asset found (requested cuda{cudaVersion}); falling back to Vulkan Linux build if available");
            best = linuxAssets.FirstOrDefault(asset =>
            {
                string name = asset["name"]?.ToString();
                return !string.IsNullOrEmpty(name) && name.Contains("vulkan", StringComparison.OrdinalIgnoreCase);
            });
            if (best is not null)
            {
                Logs.Warning("[SDcpp] Selected Linux Vulkan asset as fallback for CUDA request");
                return best;
            }
        }

        if (normalizedDevice == "vulkan")
        {
            JToken best = linuxAssets.FirstOrDefault(asset =>
            {
                string name = asset["name"]?.ToString();
                return !string.IsNullOrEmpty(name) && name.Contains("vulkan", StringComparison.OrdinalIgnoreCase);
            });
            if (best is not null)
            {
                Logs.Debug("[SDcpp] Matched Linux Vulkan asset");
                return best;
            }
        }

        JToken cpuCandidate = linuxAssets.FirstOrDefault(asset =>
        {
            string name = asset["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return !name.Contains("vulkan", StringComparison.OrdinalIgnoreCase) && !name.Contains("cuda", StringComparison.OrdinalIgnoreCase);
        });
        return cpuCandidate ?? linuxAssets.FirstOrDefault();
    }

    private static DownloadInfo CreateDownloadInfoFromAsset(JToken asset, string tagName, string etag)
    {
        return new DownloadInfo
        {
            FileName = asset["name"]?.ToString(),
            DownloadUrl = asset["browser_download_url"]?.ToString(),
            Size = asset["size"]?.ToObject<long>() ?? 0,
            TagName = tagName,
            ETag = etag
        };
    }

    /// <summary>Fallback download info when GitHub API is unavailable or rate-limited. Uses known working release URLs.</summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    public static DownloadInfo GetFallbackDownloadInfo(string deviceType, string cudaVersion = "12")
    {
        string baseUrl = $"https://github.com/leejet/stable-diffusion.cpp/releases/download/{FALLBACK_RELEASE_TAG}/";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                "cuda" => new DownloadInfo
                {
                    FileName = $"sd-{FALLBACK_RELEASE_TAG}-bin-win-cuda{cudaVersion}-x64.zip",
                    DownloadUrl = baseUrl + $"sd-{FALLBACK_RELEASE_TAG}-bin-win-cuda{cudaVersion}-x64.zip",
                    Size = 48_000_000,
                    TagName = FALLBACK_RELEASE_TAG
                },
                "vulkan" => new DownloadInfo
                {
                    FileName = $"sd-{FALLBACK_RELEASE_TAG}-bin-win-vulkan-x64.zip",
                    DownloadUrl = baseUrl + $"sd-{FALLBACK_RELEASE_TAG}-bin-win-vulkan-x64.zip",
                    Size = 7_000_000,
                    TagName = FALLBACK_RELEASE_TAG
                },
                _ => new DownloadInfo
                {
                    FileName = $"sd-{FALLBACK_RELEASE_TAG}-bin-win-avx2-x64.zip",
                    DownloadUrl = baseUrl + $"sd-{FALLBACK_RELEASE_TAG}-bin-win-avx2-x64.zip",
                    Size = 2_000_000,
                    TagName = FALLBACK_RELEASE_TAG
                }
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new DownloadInfo
            {
                FileName = "sd-master--bin-Linux-Ubuntu-24.04-x86_64.zip",
                DownloadUrl = baseUrl + "sd-master--bin-Linux-Ubuntu-24.04-x86_64.zip",
                Size = 2_400_000,
                TagName = FALLBACK_RELEASE_TAG
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new DownloadInfo
            {
                FileName = "sd-master--bin-Darwin-macOS-14.7.6-arm64.zip",
                DownloadUrl = baseUrl + "sd-master--bin-Darwin-macOS-14.7.6-arm64.zip",
                Size = 4_300_000,
                TagName = FALLBACK_RELEASE_TAG
            };
        }
        return null;
    }

    /// <summary>Downloads and extracts the SD.cpp binary to the specified directory.</summary>
    /// <param name="downloadInfo">Download information</param>
    /// <param name="targetDir">Directory to extract files to</param>
    /// <returns>Path to the extracted executable</returns>
    public static async Task<string> DownloadAndExtract(DownloadInfo downloadInfo, string targetDir)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "SwarmUI-SDcpp");
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, downloadInfo.FileName);
            string tempExtractDir = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(downloadInfo.FileName));
            double sizeMB = downloadInfo.Size / 1024.0 / 1024.0;
            Logs.Info($"[SDcpp] Downloading {downloadInfo.FileName} ({sizeMB:F1} MB)...");
            Logs.Info($"[SDcpp] URL: {downloadInfo.DownloadUrl}");
            await Utilities.DownloadFile(downloadInfo.DownloadUrl, zipPath, (_, __, ___) => { });
            if (!File.Exists(zipPath))
            {
                Logs.Error("[SDcpp] Download failed: archive missing");
                return null;
            }
            long actualSize = new FileInfo(zipPath).Length;
            if (actualSize <= 0)
            {
                Logs.Error("[SDcpp] Download failed: archive empty");
                return null;
            }
            if (downloadInfo.Size > 0 && actualSize < downloadInfo.Size * 0.8)
            {
                Logs.Error($"[SDcpp] Download size mismatch: expected {downloadInfo.Size} bytes, got {actualSize} bytes");
                return null;
            }
            Logs.Info("[SDcpp] Download completed, extracting...");
            if (Directory.Exists(tempExtractDir))
            {
                Directory.Delete(tempExtractDir, true);
            }
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);
            string foundExecutable = FindBestExecutableInDirectory(tempExtractDir);
            if (string.IsNullOrEmpty(foundExecutable))
            {
                Logs.Error("[SDcpp] Could not find SD.cpp executable in extracted files");
                return null;
            }
            CopyDirectoryContents(tempExtractDir, targetDir);
            string finalExecutable = FindBestExecutableInDirectory(targetDir);
            if (string.IsNullOrEmpty(finalExecutable))
            {
                Logs.Error("[SDcpp] Executable missing after copy to target directory");
                return null;
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using Process process = Process.Start("chmod", $"+x \"{finalExecutable}\"");
                    process?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[SDcpp] Could not set executable permissions: {ex.Message}");
                }
            }
            try
            {
                File.Delete(zipPath);
                Directory.Delete(tempExtractDir, true);
            }
            catch
            {
            }
            Logs.Info($"[SDcpp] Extraction complete. Executable: {finalExecutable}");
            return finalExecutable;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error downloading and extracting: {ex.Message}");
            return null;
        }
    }

    public static string FindBestExecutableInDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (string exeName in WindowsExecutableCandidates)
                {
                    string found = FindExecutableInDirectory(directory, exeName);
                    if (!string.IsNullOrEmpty(found))
                    {
                        return found;
                    }
                }
                foreach (string exe in Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories))
                {
                    string file = Path.GetFileName(exe).ToLowerInvariant();
                    if (file.Contains("unins") || file.Contains("setup"))
                    {
                        continue;
                    }
                    return exe;
                }
                return null;
            }
            foreach (string exe in UnixExecutableCandidates)
            {
                string found = FindExecutableInDirectory(directory, exe);
                if (!string.IsNullOrEmpty(found))
                {
                    return found;
                }
            }

            foreach (string file in Directory.GetFiles(directory))
            {
                string fileName = Path.GetFileName(file);
                string extension = Path.GetExtension(fileName).ToLowerInvariant();
                if (string.IsNullOrEmpty(fileName) || fileName.StartsWith("lib") || fileName.Contains(".so"))
                {
                    continue;
                }
                if (extension is ".json" or ".txt" or ".md")
                {
                    continue;
                }
                return file;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error while searching for executable in '{directory}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies all files from source directory to target directory.
    /// </summary>
    public static void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryContents(subDir, destSubDir);
        }
    }

    /// <summary>
    /// Recursively searches for the executable file in the extracted directory
    /// </summary>
    public static string FindExecutableInDirectory(string directory, string executableName)
    {
        try
        {
            string directPath = Path.Combine(directory, executableName);
            if (File.Exists(directPath))
            {
                return directPath;
            }
            foreach (string subDir in Directory.GetDirectories(directory))
            {
                string result = FindExecutableInDirectory(subDir, executableName);
                if (!string.IsNullOrEmpty(result))
                {
                    return result;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error searching for executable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Checks if the installed binary should be updated by comparing with the latest GitHub release. Implements smart update checking with rate limiting (checks at most once per day).</summary>
    /// <param name="versionInfoPath">Path to version info file</param>
    /// <param name="deviceType">Device type for logging</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    /// <returns>True if update is available and should be downloaded</returns>
    public static async Task<bool> ShouldUpdateBinary(string versionInfoPath, string deviceType, string cudaVersion = null)
    {
        try
        {
            VersionInfo currentVersion = LoadVersionInfo(versionInfoPath);
            if (currentVersion == null)
            {
                Logs.Info("[SDcpp] No version info found, will check for updates");
                currentVersion = new VersionInfo { TagName = "unknown", InstalledDate = DateTime.MinValue };
            }
            if (!string.IsNullOrEmpty(currentVersion.ExecutablePath) && !File.Exists(currentVersion.ExecutablePath))
            {
                Logs.Warning("[SDcpp] Version info executable missing, forcing update check");
                currentVersion.TagName = "unknown";
            }
            if (!string.IsNullOrEmpty(currentVersion.DeviceType) && !string.Equals(currentVersion.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase))
            {
                Logs.Info($"[SDcpp] Version info device mismatch ({currentVersion.DeviceType} != {deviceType}), forcing update check");
                currentVersion.TagName = "unknown";
            }
            TimeSpan updateInterval = TimeSpan.FromHours(24);
            if (currentVersion.LastUpdateCheck > DateTime.MinValue && DateTime.UtcNow - currentVersion.LastUpdateCheck < updateInterval && currentVersion.TagName is not "unknown")
            {
                Logs.Debug($"[SDcpp] Skipping update check (last checked {currentVersion.LastUpdateCheck:O})");
                return false;
            }
            Logs.Info($"[SDcpp] Checking for updates (current version: {currentVersion.TagName})...");
            (DownloadInfo latestRelease, bool notModified, string etag) = await GetDownloadInfoWithCache(deviceType, cudaVersion, currentVersion.ETag);
            if (notModified)
            {
                currentVersion.LastUpdateCheck = DateTime.UtcNow;
                SaveVersionInfo(versionInfoPath, currentVersion);
                Logs.Debug("[SDcpp] Update check skipped (release data not modified)");
                return false;
            }
            if (latestRelease == null || string.IsNullOrEmpty(latestRelease.TagName))
            {
                Logs.Warning("[SDcpp] Could not determine latest version from GitHub");
                currentVersion.LastUpdateCheck = DateTime.UtcNow;
                SaveVersionInfo(versionInfoPath, currentVersion);
                return false;
            }
            currentVersion.LastUpdateCheck = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(etag))
            {
                currentVersion.ETag = etag;
            }
            SaveVersionInfo(versionInfoPath, currentVersion);
            bool isNewer = IsNewerVersion(currentVersion.TagName, latestRelease.TagName);
            if (isNewer)
            {
                Logs.Info($"[SDcpp] Update available: {currentVersion.TagName} -> {latestRelease.TagName}");
                return true;
            }
            else
            {
                Logs.Info($"[SDcpp] Already running latest version: {currentVersion.TagName}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error checking for updates: {ex.Message}");
            return false;
        }
    }

    /// <summary>Downloads and installs the latest version of SD.cpp binary.</summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    /// <param name="targetDir">Directory to install to</param>
    /// <param name="versionInfoPath">Path to save version info</param>
    /// <returns>Path to installed executable</returns>
    public static async Task<string> DownloadLatestVersion(string deviceType, string cudaVersion, string targetDir, string versionInfoPath)
    {
        try
        {
            DownloadInfo downloadInfo = await GetDownloadInfo(deviceType, cudaVersion);
            if (downloadInfo == null)
            {
                Logs.Error("[SDcpp] Could not determine download URL for SD.cpp binary.");
                return null;
            }
            Logs.Info($"[SDcpp] Downloading version {downloadInfo.TagName}...");
            string extractedExecutable = await DownloadAndExtract(downloadInfo, targetDir);
            if (string.IsNullOrEmpty(extractedExecutable) || !File.Exists(extractedExecutable))
            {
                Logs.Error("[SDcpp] Failed to download and extract SD.cpp binary.");
                return null;
            }
            VersionInfo versionInfo = new()
            {
                TagName = downloadInfo.TagName,
                InstalledDate = DateTime.UtcNow,
                LastUpdateCheck = DateTime.UtcNow,
                DeviceType = deviceType,
                ExecutablePath = extractedExecutable,
                ETag = downloadInfo.ETag
            };
            SaveVersionInfo(versionInfoPath, versionInfo);
            Logs.Info($"[SDcpp] Successfully installed version {downloadInfo.TagName}");
            return extractedExecutable;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error downloading latest version: {ex.Message}");
            return null;
        }
    }

    /// <summary>Compares two version tags to determine if the second is newer. Handles SD.cpp's tag format like "master-abc1234" or semantic versions.</summary>
    public static bool IsNewerVersion(string currentTag, string newTag)
    {
        if (string.IsNullOrEmpty(currentTag) || currentTag == "unknown")
        {
            return true; // Any version is newer than unknown
        }
        if (currentTag == newTag)
        {
            return false;
        }
        string currentHash = ExtractCommitHash(currentTag);
        string newHash = ExtractCommitHash(newTag);
        if (!string.IsNullOrEmpty(currentHash) && !string.IsNullOrEmpty(newHash))
        {
            return currentHash != newHash;
        }
        if (Version.TryParse(CleanVersionString(currentTag), out Version currentVer) &&
            Version.TryParse(CleanVersionString(newTag), out Version newVer))
        {
            return newVer > currentVer;
        }
        // Fallback: different tags = assume newer
        return true;
    }

    /// <summary>Extracts commit hash from tags like "master-abc1234" or "v1.0-abc1234"</summary>
    public static string ExtractCommitHash(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;
        int dashIndex = tag.LastIndexOf('-');
        if (dashIndex > 0 && dashIndex < tag.Length - 1)
        {
            return tag[(dashIndex + 1)..];
        }
        return null;
    }

    /// <summary>Cleans version string for parsing (removes 'v' prefix, extracts numeric parts)</summary>
    public static string CleanVersionString(string version)
    {
        if (string.IsNullOrEmpty(version)) return "0.0.0";
        version = version.TrimStart('v', 'V');
        int dashIndex = version.IndexOf('-');
        if (dashIndex > 0)
        {
            version = version[..dashIndex];
        }
        return version;
    }

    /// <summary>Loads version information from JSON file</summary>
    public static VersionInfo LoadVersionInfo(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return JObject.Parse(json).ToObject<VersionInfo>();
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error loading version info: {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves version information to JSON file</summary>
    public static void SaveVersionInfo(string path, VersionInfo versionInfo)
    {
        try
        {
            string json = JObject.FromObject(versionInfo).ToString(Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error saving version info: {ex.Message}");
        }
    }

    /// <summary>Version tracking information for installed SD.cpp binary</summary>
    public class VersionInfo
    {
        public string TagName { get; set; }
        public DateTime InstalledDate { get; set; }
        public DateTime LastUpdateCheck { get; set; }
        public string DeviceType { get; set; }
        public string ExecutablePath { get; set; }
        public string ETag { get; set; }
    }

    /// <summary>Information about a downloadable SD.cpp release asset</summary>
    public class DownloadInfo
    {
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }
        public string TagName { get; set; }
        public string ETag { get; set; }
    }
}
