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

    /// <summary>GitHub API endpoint for latest SD.cpp releases</summary>
    private const string GITHUB_API_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";

    /// <summary>Known working release tag for fallback when API is unavailable</summary>
    private const string FALLBACK_RELEASE_TAG = "master-1896b28";

    /// <summary>
    /// Ensures SD.cpp is available for the current platform by downloading the prebuilt binary.
    /// If a valid executable already exists, it is reused.
    /// </summary>
    /// <param name="executablePath">Current configured executable path (if any)</param>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which binary to download</param>
    /// <returns>Path to the SD.cpp executable</returns>
    public static async Task<string> EnsureSDcppAvailable(string executablePath, string deviceType = "cpu")
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

            // Check if we already have a valid executable for this device type
            if (File.Exists(expectedExecutable))
            {
                Logs.Info($"[SDcpp] Found existing SD.cpp executable: {expectedExecutable}");
                return expectedExecutable;
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

            // Get download information from GitHub
            DownloadInfo downloadInfo = await GetDownloadInfo(deviceType);
            if (downloadInfo == null)
            {
                Logs.Error("[SDcpp] Could not determine download URL for SD.cpp binary.");
                return null;
            }

            // Download and extract
            string extractedExecutable = await DownloadAndExtract(downloadInfo, deviceDir);
            if (string.IsNullOrEmpty(extractedExecutable) || !File.Exists(extractedExecutable))
            {
                Logs.Error("[SDcpp] Failed to download and extract SD.cpp binary.");
                return null;
            }

            Logs.Info($"[SDcpp] SD.cpp installed successfully: {extractedExecutable}");
            return extractedExecutable;
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

            // Find the executable in extracted files
            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sd.exe" : "sd";
            string foundExecutable = FindExecutableInDirectory(tempExtractDir, executableName);

            if (string.IsNullOrEmpty(foundExecutable))
            {
                Logs.Error($"[SDcpp] Could not find {executableName} in extracted files");
                return null;
            }

            // Copy the executable and all files from its directory to target
            string sourceDir = Path.GetDirectoryName(foundExecutable);
            CopyDirectoryContents(sourceDir, targetDir);

            string finalExecutable = Path.Combine(targetDir, executableName);

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
