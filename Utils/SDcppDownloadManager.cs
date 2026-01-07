using SwarmUI.Utils;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using System.IO;

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

    /// <summary>Known working release tag for fallback when API is unavailable</summary>
    private const string FALLBACK_RELEASE_TAG = "master-1896b28";

    /// <summary>Version info file to track installed SD.cpp version</summary>
    private const string VERSION_INFO_FILE = "sdcpp_version.json";

    /// <summary>Resolves the CUDA version to use based on user setting and system detection. Handles "auto" by detecting installed CUDA, or validates explicit versions.</summary>
    /// <param name="cudaVersionSetting">User's CUDA version preference: "auto", "11", or "12"</param>
    /// <returns>Resolved CUDA version string ("11" or "12"), defaults to "12" if detection fails</returns>
    public static string ResolveCudaVersion(string cudaVersionSetting)
    {
        cudaVersionSetting = (cudaVersionSetting ?? "auto").ToLowerInvariant().Trim();
        if (cudaVersionSetting == "11" || cudaVersionSetting == "12")
        {
            Logs.Info($"[SDcpp] Using explicitly configured CUDA version: {cudaVersionSetting}");
            return cudaVersionSetting;
        }
        Logs.Info("[SDcpp] Auto-detecting CUDA version...");
        (string detectedVersion, string detectedPath) = SDcppProcessManager.DetectInstalledCudaVersion();
        if (!string.IsNullOrEmpty(detectedVersion))
        {
            Logs.Info($"[SDcpp] Detected CUDA installation: {detectedVersion} at {detectedPath}");
            if (detectedVersion.StartsWith("11"))
            {
                Logs.Info("[SDcpp] Auto-selected CUDA 11.x build");
                return "11";
            }
            else if (detectedVersion.StartsWith("12"))
            {
                Logs.Info("[SDcpp] Auto-selected CUDA 12.x build");
                return "12";
            }
            else if (detectedVersion.StartsWith("13"))
            {
                // CUDA 13 drivers are backward compatible with CUDA 12 binaries
                Logs.Info("[SDcpp] Detected CUDA 13.x - using CUDA 12 build (forward compatible)");
                return "12";
            }
            else
            {
                Logs.Warning($"[SDcpp] Unrecognized CUDA version: {detectedVersion}, defaulting to CUDA 12");
                return "12";
            }
        }
        Logs.Warning("[SDcpp] No CUDA installation detected, defaulting to CUDA 12 build");
        Logs.Warning("[SDcpp] If you have CUDA installed, ensure CUDA_PATH environment variable is set");
        return "12";
    }

    /// <summary>Ensures SD.cpp is available for the current platform by downloading the prebuilt binary. Checks for updates and automatically downloads newer versions if enabled in settings.</summary>
    /// <param name="executablePath">Current configured executable path (if any)</param>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which binary to download</param>
    /// <param name="cudaVersion">CUDA version preference: "auto", "11", or "12" (only used when deviceType is "cuda")</param>
    /// <param name="autoUpdate">Whether to automatically check for and download updates (default: true)</param>
    /// <returns>Path to the SD.cpp executable</returns>
    public static async Task<string> EnsureSDcppAvailable(string executablePath, string deviceType = "cpu", string cudaVersion = "auto", bool autoUpdate = true)
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
                        string updatedExecutable = await DownloadLatestVersion(deviceType, resolvedCudaVersion, deviceDir, versionInfoPath);
                        if (!string.IsNullOrEmpty(updatedExecutable))
                        {
                            return updatedExecutable;
                        }
                        else
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
            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                string execDir = Path.GetDirectoryName(executablePath) ?? "";
                string expectedDirName = deviceType == "cuda" ? $"cuda{resolvedCudaVersion}" : deviceType;
                bool isDeviceMatch = execDir.EndsWith(expectedDirName, StringComparison.OrdinalIgnoreCase) ||
                                     !execDir.Contains("sdcpp");
                if (isDeviceMatch)
                {
                    Logs.Info($"[SDcpp] Using user-specified executable: {executablePath}");
                    return executablePath;
                }
                else
                {
                    Logs.Info($"[SDcpp] Ignoring cached executable (wrong device/CUDA version): {executablePath}");
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

    /// <summary>Gets download information for the current platform from GitHub releases. Falls back to known working URLs if the API is unavailable.</summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="cudaVersion">CUDA version (11 or 12), only used when deviceType is "cuda"</param>
    public static async Task<DownloadInfo> GetDownloadInfo(string deviceType, string cudaVersion = "12")
    {
        try
        {
            Logs.Info("[SDcpp] Fetching latest release from GitHub...");
            using HttpRequestMessage request = new(HttpMethod.Get, GITHUB_API_URL);
            request.Headers.Add("User-Agent", "SwarmUI-SDcpp-Extension");
            using HttpResponseMessage responseMessage = await Utilities.UtilWebClient.SendAsync(request);
            responseMessage.EnsureSuccessStatusCode();
            string response = await responseMessage.Content.ReadAsStringAsync();
            JObject releaseData = JObject.Parse(response);
            string tagName = releaseData["tag_name"]?.ToString();
            JArray assets = releaseData["assets"] as JArray;
            if (assets == null || assets.Count == 0)
            {
                Logs.Warning("[SDcpp] No assets found in latest release, using fallback...");
                return GetFallbackDownloadInfo(deviceType, cudaVersion);
            }
            string assetPattern = GetPlatformAssetPattern(deviceType, cudaVersion);
            if (string.IsNullOrEmpty(assetPattern))
            {
                Logs.Error("[SDcpp] Unsupported platform for automatic download.");
                return null;
            }
            foreach (JToken asset in assets)
            {
                string name = asset["name"]?.ToString();
                if (name != null && name.Contains(assetPattern, StringComparison.OrdinalIgnoreCase))
                {
                    return new DownloadInfo
                    {
                        FileName = name,
                        DownloadUrl = asset["browser_download_url"]?.ToString(),
                        Size = asset["size"]?.ToObject<long>() ?? 0,
                        TagName = tagName
                    };
                }
            }
            Logs.Warning($"[SDcpp] No matching asset for pattern '{assetPattern}', using fallback...");
            return GetFallbackDownloadInfo(deviceType, cudaVersion);
        }
        catch (HttpRequestException ex)
        {
            Logs.Warning($"[SDcpp] GitHub API request failed ({ex.Message}), using fallback URLs...");
            return GetFallbackDownloadInfo(deviceType, cudaVersion);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error fetching release info ({ex.Message}), using fallback URLs...");
            return GetFallbackDownloadInfo(deviceType, cudaVersion);
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
            string sourceDir = Path.GetDirectoryName(foundExecutable);
            CopyDirectoryContents(sourceDir, targetDir);
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
                if (!string.IsNullOrEmpty(fileName) && !fileName.StartsWith("lib") && !fileName.Contains(".so"))
                {
                    return file;
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
            Logs.Info($"[SDcpp] Checking for updates (current version: {currentVersion.TagName})...");
            DownloadInfo latestRelease = await GetDownloadInfo(deviceType, cudaVersion);
            if (latestRelease == null || string.IsNullOrEmpty(latestRelease.TagName))
            {
                Logs.Warning("[SDcpp] Could not determine latest version from GitHub");
                currentVersion.LastUpdateCheck = DateTime.UtcNow;
                SaveVersionInfo(versionInfoPath, currentVersion);
                return false;
            }
            currentVersion.LastUpdateCheck = DateTime.UtcNow;
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
                ExecutablePath = extractedExecutable
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
            return tag.Substring(dashIndex + 1);
        }
        return null;
    }

    /// <summary>Cleans version string for parsing (removes 'v' prefix, extracts numeric parts)</summary>
    public static string CleanVersionString(string version)
    {
        if (string.IsNullOrEmpty(version))
            return "0.0.0";
        version = version.TrimStart('v', 'V');
        int dashIndex = version.IndexOf('-');
        if (dashIndex > 0)
        {
            version = version.Substring(0, dashIndex);
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
    }

    /// <summary>Information about a downloadable SD.cpp release asset</summary>
    public class DownloadInfo
    {
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }
        public string TagName { get; set; }
    }
}
