using SwarmUI.Utils;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>
/// Manages downloading and installing stable-diffusion.cpp prebuilt binaries from GitHub releases.
/// Handles platform detection and automatic extraction - no compilation required.
/// </summary>
public static class SDcppDownloadManager
{
    private static readonly HttpClient HttpClient = new();

    private static readonly string[] WindowsExecutableCandidates = [
        "sd.exe",
        "stable-diffusion.exe",
        "stable-diffusion-cpp.exe",
        "stable-diffusion.cpp.exe"
    ];

    /// <summary>GitHub API endpoint for latest SD.cpp releases</summary>
    private const string GITHUB_API_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";

    /// <summary>Known working release tag for fallback when API is unavailable</summary>
    private const string FALLBACK_RELEASE_TAG = "master-1896b28";

    /// <summary>Version info file to track installed SD.cpp version</summary>
    private const string VERSION_INFO_FILE = "sdcpp_version.json";

    /// <summary>
    /// Ensures SD.cpp is available for the current platform by downloading the prebuilt binary.
    /// Checks for updates and automatically downloads newer versions if enabled in settings.
    /// </summary>
    /// <param name="executablePath">Current configured executable path (if any)</param>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which binary to download</param>
    /// <param name="autoUpdate">Whether to automatically check for and download updates (default: true)</param>
    /// <returns>Path to the SD.cpp executable</returns>
    public static async Task<string> EnsureSDcppAvailable(string executablePath, string deviceType = "cpu", bool autoUpdate = true)
    {
        try
        {
            // Normalize device type
            deviceType = (deviceType ?? "cpu").ToLowerInvariant();

            // Define installation directory
            string sdcppDir = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, "dlbackend", "sdcpp");
            string deviceDir = Path.Combine(sdcppDir, deviceType);
            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sd.exe" : "sd";
            string expectedExecutable = Path.Combine(deviceDir, executableName);
            string versionInfoPath = Path.Combine(deviceDir, VERSION_INFO_FILE);

            // Check if we already have a valid executable for this device type
            if (File.Exists(expectedExecutable))
            {
                Logs.Info($"[SDcpp] Found existing SD.cpp executable: {expectedExecutable}");

                // Check for updates if auto-update is enabled
                if (autoUpdate)
                {
                    bool shouldUpdate = await ShouldUpdateBinary(versionInfoPath, deviceType);
                    if (shouldUpdate)
                    {
                        Logs.Info("[SDcpp] Newer version available, downloading update...");
                        string updatedExecutable = await DownloadLatestVersion(deviceType, deviceDir, versionInfoPath);
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

            // If sd.exe is not present, the upstream release may have changed naming.
            // Search for any suitable executable in the device directory.
            if (Directory.Exists(deviceDir))
            {
                string existing = FindBestExecutableInDirectory(deviceDir);
                if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
                {
                    Logs.Info($"[SDcpp] Found existing SD.cpp executable (non-standard name): {existing}");
                    return existing;
                }
            }

            // Check if user specified a custom path that exists AND is for the correct device type
            // Don't use a CUDA binary when Vulkan is requested, etc.
            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                // Only use user-specified path if it's in the correct device folder or a completely custom location
                string execDir = Path.GetDirectoryName(executablePath) ?? "";
                bool isDeviceMatch = execDir.EndsWith(deviceType, StringComparison.OrdinalIgnoreCase) ||
                                     !execDir.Contains("sdcpp"); // Custom location outside sdcpp folder
                
                if (isDeviceMatch)
                {
                    Logs.Info($"[SDcpp] Using user-specified executable: {executablePath}");
                    return executablePath;
                }
                else
                {
                    Logs.Info($"[SDcpp] Ignoring cached executable (wrong device type): {executablePath}");
                    Logs.Info($"[SDcpp] Need to download {deviceType} binary...");
                }
            }

            // Need to download - create directory structure
            Directory.CreateDirectory(deviceDir);
            Logs.Info($"[SDcpp] SD.cpp not found. Downloading prebuilt binary for {deviceType}...");

            // Download latest version
            string downloadedExecutable = await DownloadLatestVersion(deviceType, deviceDir, versionInfoPath);
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

    /// <summary>
    /// Gets download information for the current platform from GitHub releases.
    /// Falls back to known working URLs if the API is unavailable.
    /// </summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    private static async Task<DownloadInfo> GetDownloadInfo(string deviceType)
    {
        try
        {
            // Set up HTTP client with required headers for GitHub API
            HttpClient.DefaultRequestHeaders.Clear();
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "SwarmUI-SDcpp-Extension");

            Logs.Info("[SDcpp] Fetching latest release from GitHub...");
            string response = await HttpClient.GetStringAsync(GITHUB_API_URL);
            JObject releaseData = JObject.Parse(response);

            string tagName = releaseData["tag_name"]?.ToString();
            JArray assets = releaseData["assets"] as JArray;

            if (assets == null || assets.Count == 0)
            {
                Logs.Warning("[SDcpp] No assets found in latest release, using fallback...");
                return GetFallbackDownloadInfo(deviceType);
            }

            // Find the matching asset for our platform/device
            string assetPattern = GetPlatformAssetPattern(deviceType);
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
            return GetFallbackDownloadInfo(deviceType);
        }
        catch (HttpRequestException ex)
        {
            Logs.Warning($"[SDcpp] GitHub API request failed ({ex.Message}), using fallback URLs...");
            return GetFallbackDownloadInfo(deviceType);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[SDcpp] Error fetching release info ({ex.Message}), using fallback URLs...");
            return GetFallbackDownloadInfo(deviceType);
        }
    }

    /// <summary>
    /// Returns the asset name pattern to search for based on platform and device type.
    /// </summary>
    private static string GetPlatformAssetPattern(string deviceType)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                "cuda" => "win-cuda12-x64",
                "vulkan" => "win-vulkan-x64",
                _ => "win-avx2-x64"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux CUDA builds have different naming
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

    /// <summary>
    /// Fallback download info when GitHub API is unavailable or rate-limited.
    /// Uses known working release URLs.
    /// </summary>
    private static DownloadInfo GetFallbackDownloadInfo(string deviceType)
    {
        string baseUrl = $"https://github.com/leejet/stable-diffusion.cpp/releases/download/{FALLBACK_RELEASE_TAG}/";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType switch
            {
                "cuda" => new DownloadInfo
                {
                    FileName = $"sd-{FALLBACK_RELEASE_TAG}-bin-win-cuda12-x64.zip",
                    DownloadUrl = baseUrl + $"sd-{FALLBACK_RELEASE_TAG}-bin-win-cuda12-x64.zip",
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

    /// <summary>
    /// Downloads and extracts the SD.cpp binary to the specified directory.
    /// </summary>
    /// <param name="downloadInfo">Download information</param>
    /// <param name="targetDir">Directory to extract files to</param>
    /// <returns>Path to the extracted executable</returns>
    private static async Task<string> DownloadAndExtract(DownloadInfo downloadInfo, string targetDir)
    {
        try
        {
            // Use temp directory for download
            string tempDir = Path.Combine(Path.GetTempPath(), "SwarmUI-SDcpp");
            Directory.CreateDirectory(tempDir);

            string zipPath = Path.Combine(tempDir, downloadInfo.FileName);
            string tempExtractDir = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(downloadInfo.FileName));

            // Download with progress logging
            double sizeMB = downloadInfo.Size / 1024.0 / 1024.0;
            Logs.Info($"[SDcpp] Downloading {downloadInfo.FileName} ({sizeMB:F1} MB)...");
            Logs.Info($"[SDcpp] URL: {downloadInfo.DownloadUrl}");

            using (HttpResponseMessage response = await HttpClient.GetAsync(downloadInfo.DownloadUrl))
            {
                response.EnsureSuccessStatusCode();
                await using FileStream fileStream = File.Create(zipPath);
                await response.Content.CopyToAsync(fileStream);
            }

            Logs.Info("[SDcpp] Download completed, extracting...");

            // Clean up any previous extraction
            if (Directory.Exists(tempExtractDir))
            {
                Directory.Delete(tempExtractDir, true);
            }

            // Extract to temp directory first
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

            // Find the executable in extracted files (asset layouts/names vary by release)
            string foundExecutable = FindBestExecutableInDirectory(tempExtractDir);

            if (string.IsNullOrEmpty(foundExecutable))
            {
                Logs.Error("[SDcpp] Could not find SD.cpp executable in extracted files");
                return null;
            }

            // Copy the executable and all files from its directory to target
            string sourceDir = Path.GetDirectoryName(foundExecutable);
            CopyDirectoryContents(sourceDir, targetDir);

            string finalExecutable = Path.Combine(targetDir, Path.GetFileName(foundExecutable));

            // Set executable permissions on Unix
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var process = System.Diagnostics.Process.Start("chmod", $"+x \"{finalExecutable}\"");
                    process?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[SDcpp] Could not set executable permissions: {ex.Message}");
                }
            }

            // Clean up temp files
            try
            {
                File.Delete(zipPath);
                Directory.Delete(tempExtractDir, true);
            }
            catch
            {
                // Ignore cleanup errors
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

    private static string FindBestExecutableInDirectory(string directory)
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

                // Fallback: if upstream changed naming, grab the first .exe that isn't a helper/uninstaller.
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

            // Unix-like: should be sd
            return FindExecutableInDirectory(directory, "sd");
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
    private static void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Copy all files
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        // Copy subdirectories recursively
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryContents(subDir, destSubDir);
        }
    }

    /// <summary>
    /// Recursively searches for the executable file in the extracted directory
    /// </summary>
    private static string FindExecutableInDirectory(string directory, string executableName)
    {
        try
        {
            // Check current directory
            string directPath = Path.Combine(directory, executableName);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // Search subdirectories
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

    /// <summary>
    /// Checks if the installed binary should be updated by comparing with the latest GitHub release.
    /// Implements smart update checking with rate limiting (checks at most once per day).
    /// </summary>
    /// <param name="versionInfoPath">Path to version info file</param>
    /// <param name="deviceType">Device type for logging</param>
    /// <returns>True if update is available and should be downloaded</returns>
    private static async Task<bool> ShouldUpdateBinary(string versionInfoPath, string deviceType)
    {
        try
        {
            // Load existing version info
            VersionInfo currentVersion = LoadVersionInfo(versionInfoPath);
            if (currentVersion == null)
            {
                Logs.Info("[SDcpp] No version info found, will check for updates");
                // No version file = old installation, check for update
                currentVersion = new VersionInfo { TagName = "unknown", InstalledDate = DateTime.MinValue };
            }

            // Rate limiting: only check for updates once per day
            TimeSpan timeSinceLastCheck = DateTime.UtcNow - currentVersion.LastUpdateCheck;
            if (timeSinceLastCheck.TotalHours < 24)
            {
                Logs.Debug($"[SDcpp] Last update check was {timeSinceLastCheck.TotalHours:F1} hours ago, skipping check");
                return false;
            }

            Logs.Info($"[SDcpp] Checking for updates (current version: {currentVersion.TagName})...");

            // Get latest release info from GitHub
            DownloadInfo latestRelease = await GetDownloadInfo(deviceType);
            if (latestRelease == null || string.IsNullOrEmpty(latestRelease.TagName))
            {
                Logs.Warning("[SDcpp] Could not determine latest version from GitHub");
                // Update last check time even on failure to avoid spamming GitHub API
                currentVersion.LastUpdateCheck = DateTime.UtcNow;
                SaveVersionInfo(versionInfoPath, currentVersion);
                return false;
            }

            // Update last check time
            currentVersion.LastUpdateCheck = DateTime.UtcNow;
            SaveVersionInfo(versionInfoPath, currentVersion);

            // Compare versions
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

    /// <summary>
    /// Downloads and installs the latest version of SD.cpp binary.
    /// </summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan)</param>
    /// <param name="targetDir">Directory to install to</param>
    /// <param name="versionInfoPath">Path to save version info</param>
    /// <returns>Path to installed executable</returns>
    private static async Task<string> DownloadLatestVersion(string deviceType, string targetDir, string versionInfoPath)
    {
        try
        {
            // Get download information from GitHub
            DownloadInfo downloadInfo = await GetDownloadInfo(deviceType);
            if (downloadInfo == null)
            {
                Logs.Error("[SDcpp] Could not determine download URL for SD.cpp binary.");
                return null;
            }

            Logs.Info($"[SDcpp] Downloading version {downloadInfo.TagName}...");

            // Download and extract
            string extractedExecutable = await DownloadAndExtract(downloadInfo, targetDir);
            if (string.IsNullOrEmpty(extractedExecutable) || !File.Exists(extractedExecutable))
            {
                Logs.Error("[SDcpp] Failed to download and extract SD.cpp binary.");
                return null;
            }

            // Save version information
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

    /// <summary>
    /// Compares two version tags to determine if the second is newer.
    /// Handles SD.cpp's tag format like "master-abc1234" or semantic versions.
    /// </summary>
    private static bool IsNewerVersion(string currentTag, string newTag)
    {
        if (string.IsNullOrEmpty(currentTag) || currentTag == "unknown")
        {
            return true; // Any version is newer than unknown
        }

        if (currentTag == newTag)
        {
            return false;
        }

        // Extract commit hashes if format is "master-hash" or "v1.2.3-hash"
        string currentHash = ExtractCommitHash(currentTag);
        string newHash = ExtractCommitHash(newTag);

        // If both have hashes, they're different builds (we can't determine order, so assume newer)
        if (!string.IsNullOrEmpty(currentHash) && !string.IsNullOrEmpty(newHash))
        {
            return currentHash != newHash;
        }

        // Try semantic version comparison
        if (Version.TryParse(CleanVersionString(currentTag), out Version currentVer) &&
            Version.TryParse(CleanVersionString(newTag), out Version newVer))
        {
            return newVer > currentVer;
        }

        // Fallback: different tags = assume newer
        return true;
    }

    /// <summary>
    /// Extracts commit hash from tags like "master-abc1234" or "v1.0-abc1234"
    /// </summary>
    private static string ExtractCommitHash(string tag)
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

    /// <summary>
    /// Cleans version string for parsing (removes 'v' prefix, extracts numeric parts)
    /// </summary>
    private static string CleanVersionString(string version)
    {
        if (string.IsNullOrEmpty(version))
            return "0.0.0";

        // Remove 'v' prefix if present
        version = version.TrimStart('v', 'V');

        // Extract version before any dash or other separator
        int dashIndex = version.IndexOf('-');
        if (dashIndex > 0)
        {
            version = version.Substring(0, dashIndex);
        }

        return version;
    }

    /// <summary>
    /// Loads version information from JSON file
    /// </summary>
    private static VersionInfo LoadVersionInfo(string path)
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

    /// <summary>
    /// Saves version information to JSON file
    /// </summary>
    private static void SaveVersionInfo(string path, VersionInfo versionInfo)
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

    /// <summary>
    /// Version tracking information for installed SD.cpp binary
    /// </summary>
    private class VersionInfo
    {
        public string TagName { get; set; }
        public DateTime InstalledDate { get; set; }
        public DateTime LastUpdateCheck { get; set; }
        public string DeviceType { get; set; }
        public string ExecutablePath { get; set; }
    }

    /// <summary>
    /// Information about a downloadable SD.cpp release asset
    /// </summary>
    private class DownloadInfo
    {
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }
        public string TagName { get; set; }
    }
}
