using SwarmUI.Utils;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Collections.Generic;
using SwarmUI.Backends;
using System.Diagnostics;

namespace Hartsy.Extensions.SDcppExtension.Utils;

/// <summary>
/// Manages downloading and installing stable-diffusion.cpp binaries from GitHub releases.
/// Handles platform detection, user prompts, and automatic extraction.
/// </summary>
public static class SDcppDownloadManager
{
    private static readonly HttpClient HttpClient = new();
    
    /// <summary>
    /// GitHub API endpoint for latest SD.cpp releases
    /// </summary>
    private const string GITHUB_API_URL = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";
    
    /// <summary>
    /// Ensures SD.cpp is available for the current platform by building from source into the dlbackend/sdcpp folder.
    /// If a custom executable path is already configured and exists, that is used directly.
    /// </summary>
    /// <param name="executablePath">Current configured executable path</param>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which build configuration to use</param>
    /// <returns>Updated executable path after build, or original if build fails</returns>
    public static async Task<string> EnsureSDcppAvailable(string executablePath, string deviceType = "cpu")
    {
        try
        {
            string backendRoot = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, "dlbackend/sdcpp");

            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                string fullExec = Path.GetFullPath(executablePath);
                string fullBackendRoot = Path.GetFullPath(backendRoot) + Path.DirectorySeparatorChar;
                if (fullExec.StartsWith(fullBackendRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Info($"[SDcpp] Found existing built executable at: {fullExec}");
                    return fullExec;
                }
                Logs.Info($"[SDcpp] Existing executable path '{fullExec}' is outside dlbackend/sdcpp, will prefer building from source.");
            }

            Directory.CreateDirectory(backendRoot);
            Logs.Info($"[SDcpp] Using SD.cpp backend folder: {backendRoot}");

            if (!CheckBuildPrerequisites(out string missingSummary))
            {
                Logs.Warning($"[SDcpp] Build prerequisites not satisfied ({missingSummary}). Falling back to precompiled SD.cpp binary download.");
                LogBuildPrerequisiteHelp(missingSummary);
                string precompiledExec = await EnsurePrecompiledBinaryAvailable(backendRoot, deviceType);
                if (!string.IsNullOrEmpty(precompiledExec) && File.Exists(precompiledExec))
                {
                    Logs.Info($"[SDcpp] Using precompiled SD.cpp executable at: {precompiledExec}");
                    return precompiledExec;
                }

                Logs.Error("[SDcpp] Failed to obtain precompiled SD.cpp binary. Falling back to existing executable path if any.");
                return executablePath;
            }

            await EnsureRepositoryPresent(backendRoot);

            string builtExecutable = await BuildFromSource(backendRoot, deviceType);
            if (!string.IsNullOrEmpty(builtExecutable) && File.Exists(builtExecutable))
            {
                Logs.Info($"[SDcpp] Using built SD.cpp executable at: {builtExecutable}");
                return builtExecutable;
            }

            Logs.Error("[SDcpp] Failed to build SD.cpp from source. Attempting fallback to precompiled SD.cpp binary...");
            string fallbackPrecompiledExec = await EnsurePrecompiledBinaryAvailable(backendRoot, deviceType);
            if (!string.IsNullOrEmpty(fallbackPrecompiledExec) && File.Exists(fallbackPrecompiledExec))
            {
                Logs.Info($"[SDcpp] Using precompiled SD.cpp executable at: {fallbackPrecompiledExec}");
                return fallbackPrecompiledExec;
            }

            Logs.Error("[SDcpp] Failed to build SD.cpp and failed to obtain precompiled binary. Falling back to existing executable path if any.");
            return executablePath;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error during SD.cpp build process: {ex.Message}");
            return executablePath;
        }
    }

    /// <summary>
    /// Prompts the user via console to confirm SD.cpp download
    /// </summary>
    private static async Task<bool> PromptUserForDownload()
    {
        try
        {
            Logs.Info("[SDcpp] ========================================");
            Logs.Info("[SDcpp] SD.cpp Backend Setup Required");
            Logs.Info("[SDcpp] ========================================");
            Logs.Info("[SDcpp] The SD.cpp executable was not found.");
            Logs.Info("[SDcpp] Would you like to download it automatically?");
            Logs.Info("[SDcpp] This will download the appropriate binary for your platform.");
            Logs.Info("[SDcpp] ");
            Logs.Info("[SDcpp] Download SD.cpp? (y/n): ");

            // For now, we'll auto-approve in development mode
            // In a real deployment, this would need proper user interaction
            // TODO: Implement proper user prompt mechanism via SwarmUI's UI
            
            // Check if running in development/interactive mode
            if (Environment.UserInteractive)
            {
                // Auto-approve for now - in future versions this could be a UI dialog
                Logs.Info("[SDcpp] Auto-approving download in development mode...");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error prompting user: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets download information for the current platform from GitHub releases
    /// </summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which binary to download</param>
    private static async Task<DownloadInfo> GetDownloadInfo(string deviceType = "cpu")
    {
        try
        {
            Logs.Info("[SDcpp] Fetching latest release information...");
            
            string response;
            try
            {
                response = await HttpClient.GetStringAsync(GITHUB_API_URL);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("403") || ex.Message.Contains("Forbidden"))
            {
                // Fallback to direct release page scraping if API is rate limited
                Logs.Warning("[SDcpp] GitHub API rate limited, using fallback method...");
                return await GetFallbackDownloadInfo(deviceType);
            }
            
            var releaseData = JObject.Parse(response);
            
            var assets = releaseData["assets"] as JArray;
            if (assets == null || assets.Count == 0)
            {
                Logs.Error("[SDcpp] No assets found in latest release");
                return null;
            }

            // Detect platform and select appropriate asset
            string assetName = GetPlatformAssetName();
            if (string.IsNullOrEmpty(assetName))
            {
                Logs.Error("[SDcpp] Unsupported platform for automatic download");
                return null;
            }

            // Find matching asset
            foreach (var asset in assets)
            {
                string name = asset["name"]?.ToString();
                if (name != null && name.Contains(assetName))
                {
                    return new DownloadInfo
                    {
                        FileName = name,
                        DownloadUrl = asset["browser_download_url"]?.ToString(),
                        Size = asset["size"]?.ToObject<long>() ?? 0,
                        TagName = releaseData["tag_name"]?.ToString()
                    };
                }
            }

            Logs.Error($"[SDcpp] No suitable asset found for platform: {assetName}");
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error fetching download info: {ex.Message}");
            // Try fallback method on any error
            Logs.Info("[SDcpp] Attempting fallback download method...");
            return await GetFallbackDownloadInfo(deviceType);
        }
    }

    /// <summary>
    /// Fallback method when GitHub API is rate limited - uses hardcoded latest known release
    /// </summary>
    /// <param name="deviceType">Device type to determine which binary to download</param>
    private static Task<DownloadInfo> GetFallbackDownloadInfo(string deviceType)
    {
        try
        {
            Logs.Info("[SDcpp] Using fallback download URLs...");
            
            string assetName = GetPlatformAssetName(deviceType);
            if (string.IsNullOrEmpty(assetName))
            {
                return null;
            }

            // Use the latest known release from our memory
            string baseUrl = "https://github.com/leejet/stable-diffusion.cpp/releases/download/master-1896b28/";
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Task.FromResult(deviceType.ToLowerInvariant() switch
                {
                    "cuda" => new DownloadInfo
                    {
                        FileName = "sd-master-1896b28-bin-win-cuda12-x64.zip",
                        DownloadUrl = baseUrl + "sd-master-1896b28-bin-win-cuda12-x64.zip",
                        Size = 48234496, // ~46MB
                        TagName = "master-1896b28"
                    },
                    "vulkan" => new DownloadInfo
                    {
                        FileName = "sd-master-1896b28-bin-win-vulkan-x64.zip",
                        DownloadUrl = baseUrl + "sd-master-1896b28-bin-win-vulkan-x64.zip",
                        Size = 7343827, // ~7MB
                        TagName = "master-1896b28"
                    },
                    "cpu" or _ => new DownloadInfo
                    {
                        FileName = "sd-master-1896b28-bin-win-avx2-x64.zip",
                        DownloadUrl = baseUrl + "sd-master-1896b28-bin-win-avx2-x64.zip",
                        Size = 1782579, // ~1.7MB
                        TagName = "master-1896b28"
                    }
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return Task.FromResult(new DownloadInfo
                {
                    FileName = "sd-master--bin-Linux-Ubuntu-24.04-x86_64.zip",
                    DownloadUrl = baseUrl + "sd-master--bin-Linux-Ubuntu-24.04-x86_64.zip",
                    Size = 2374961,
                    TagName = "master-1896b28"
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Task.FromResult(new DownloadInfo
                {
                    FileName = "sd-master--bin-Darwin-macOS-14.7.6-arm64.zip",
                    DownloadUrl = baseUrl + "sd-master--bin-Darwin-macOS-14.7.6-arm64.zip",
                    Size = 4328149,
                    TagName = "master-1896b28"
                });
            }

            return Task.FromResult<DownloadInfo>(null);
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error in fallback download info: {ex.Message}");
            return Task.FromResult<DownloadInfo>(null);
        }
    }

    /// <summary>
    /// Determines the appropriate asset name based on current platform and device type
    /// </summary>
    /// <param name="deviceType">Device type (cpu, cuda, vulkan) to determine which binary to download</param>
    private static string GetPlatformAssetName(string deviceType = "cpu")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return deviceType.ToLowerInvariant() switch
            {
                "cuda" => "win-cuda12-x64",
                "vulkan" => "win-vulkan-x64", 
                "cpu" or _ => "win-avx2-x64" // Default to AVX2 CPU build for best performance
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux-Ubuntu"; // Linux builds are typically CPU-only
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "Darwin-macOS"; // macOS builds are typically CPU-only
        }

        return null;
    }

    private static bool CheckBuildPrerequisites(out string missingSummary)
    {
        List<string> missing = new();

        if (!CheckCommandExists("git", "--version"))
        {
            missing.Add("git");
        }

        string cmakeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmake.exe" : "cmake";
        if (!CheckCommandExists(cmakeName, "--version"))
        {
            missing.Add("cmake");
        }

        bool hasCompiler;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            hasCompiler = CheckCommandExists("cl", "/?")
                || CheckCommandExists("clang", "--version")
                || CheckCommandExists("gcc", "--version");
        }
        else
        {
            hasCompiler = CheckCommandExists("clang", "--version")
                || CheckCommandExists("gcc", "--version");
        }

        if (!hasCompiler)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                missing.Add("C++ build tools (MSVC, clang, or gcc)");
            }
            else
            {
                missing.Add("C++ build tools (gcc or clang)");
            }
        }

        if (missing.Count > 0)
        {
            missingSummary = string.Join(", ", missing);
            return false;
        }

        missingSummary = null;
        return true;
    }

    private static void LogBuildPrerequisiteHelp(string missingSummary)
    {
        Logs.Info("[SDcpp] SD.cpp build prerequisites help:");

        if (missingSummary?.IndexOf("git", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Logs.Info("[SDcpp] - git is required to clone and update the stable-diffusion.cpp repository. Download git from https://git-scm.com/downloads");
        }
        if (missingSummary?.IndexOf("cmake", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Logs.Info("[SDcpp] - CMake is required to configure the SD.cpp build. Download CMake from https://cmake.org/download/");
        }
        if (missingSummary?.IndexOf("C++ build tools", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Logs.Info("[SDcpp] - C++ build tools (MSVC, clang, or gcc) are required on Windows. Install the 'Desktop development with C++' workload or Build Tools from https://visualstudio.microsoft.com/downloads/");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Logs.Info("[SDcpp] - C++ build tools (gcc or clang) are required on Linux. Install them via your distribution's package manager (for example: sudo apt install build-essential).");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Logs.Info("[SDcpp] - C++ build tools (clang) are required on macOS. Install Xcode Command Line Tools (for example: run xcode-select --install).");
            }
        }
    }

    private static bool CheckCommandExists(string fileName, string arguments)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnsureRepositoryPresent(string backendRoot)
    {
        try
        {
            string gitDir = Path.Combine(backendRoot, ".git");
            string cmakeFile = Path.Combine(backendRoot, "CMakeLists.txt");

            // Check if we have a valid git repository with the required files
            bool hasValidRepo = Directory.Exists(gitDir) && File.Exists(cmakeFile);

            if (!hasValidRepo)
            {
                // If directory exists but is incomplete/corrupt, clean it first
                if (Directory.Exists(backendRoot))
                {
                    string[] existingFiles = Directory.GetFiles(backendRoot);
                    string[] existingDirs = Directory.GetDirectories(backendRoot);

                    if (existingFiles.Length > 0 || existingDirs.Length > 0)
                    {
                        Logs.Info("[SDcpp] Cleaning incomplete/corrupt repository in dlbackend/sdcpp...");
                        try
                        {
                            // Delete all contents but keep the directory
                            foreach (string file in existingFiles)
                            {
                                File.Delete(file);
                            }
                            foreach (string dir in existingDirs)
                            {
                                Directory.Delete(dir, true);
                            }
                        }
                        catch (Exception cleanEx)
                        {
                            Logs.Warning($"[SDcpp] Error cleaning directory: {cleanEx.Message}. Will attempt clone anyway.");
                        }
                    }
                }

                Logs.Info("[SDcpp] Cloning stable-diffusion.cpp repository into dlbackend/sdcpp...");
                string cloneResp = await Utilities.RunGitProcess("clone --recursive https://github.com/leejet/stable-diffusion.cpp .", backendRoot);
                Logs.Debug($"[SDcpp] Git clone response: {cloneResp.Trim()}");

                // Verify clone succeeded
                if (!File.Exists(cmakeFile))
                {
                    throw new Exception("Git clone completed but CMakeLists.txt not found. Repository may be incomplete.");
                }
            }
            else
            {
                Logs.Info("[SDcpp] Updating existing stable-diffusion.cpp repository in dlbackend/sdcpp...");
                string fetchResp = await Utilities.RunGitProcess("fetch --all", backendRoot);
                Logs.Debug($"[SDcpp] Git fetch response: {fetchResp.Trim()}");
                string pullResp = await Utilities.RunGitProcess("pull", backendRoot);
                Logs.Debug($"[SDcpp] Git pull response: {pullResp.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error while preparing SD.cpp git repository: {ex.Message}");
            throw;
        }
    }

    private static async Task<string> EnsurePrecompiledBinaryAvailable(string backendRoot, string deviceType)
    {
        try
        {
            DownloadInfo downloadInfo = await GetDownloadInfo(deviceType);
            if (downloadInfo == null)
            {
                Logs.Error("[SDcpp] Could not get download information for precompiled SD.cpp binary.");
                return null;
            }

            string extractedExecutable = await DownloadAndExtract(downloadInfo);
            if (string.IsNullOrEmpty(extractedExecutable) || !File.Exists(extractedExecutable))
            {
                Logs.Error("[SDcpp] Failed to download and extract precompiled SD.cpp binary.");
                return null;
            }

            string prebuiltDir = Path.Combine(backendRoot, "prebuilt");
            Directory.CreateDirectory(prebuiltDir);
            string targetExecutable = Path.Combine(prebuiltDir, Path.GetFileName(extractedExecutable));
            File.Copy(extractedExecutable, targetExecutable, true);
            Logs.Info($"[SDcpp] Copied precompiled SD.cpp binary into backend folder: {targetExecutable}");
            return targetExecutable;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error while obtaining precompiled SD.cpp binary: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> BuildFromSource(string backendRoot, string deviceType)
    {
        try
        {
            string buildDir = Path.Combine(backendRoot, "build");
            Directory.CreateDirectory(buildDir);

            string dev = (deviceType ?? "cpu").ToLowerInvariant();
            string buildFlags = dev switch
            {
                "cuda" => "-DSD_CUDA=ON",
                "vulkan" => "-DSD_VULKAN=ON",
                _ => string.Empty
            };

            Logs.Info($"[SDcpp] Configuring SD.cpp CMake build for device '{dev}'...");
            ProcessStartInfo cmakeConfigure = new()
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmake.exe" : "cmake",
                Arguments = $".. {buildFlags}".Trim(),
                WorkingDirectory = buildDir
            };
            await NetworkBackendUtils.RunProcessWithMonitoring(cmakeConfigure, "SD.cpp CMake Configure", "sdcpp_build");

            Logs.Info("[SDcpp] Building SD.cpp from source (Release config)...");
            ProcessStartInfo cmakeBuild = new()
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmake.exe" : "cmake",
                Arguments = "--build . --config Release",
                WorkingDirectory = buildDir
            };
            await NetworkBackendUtils.RunProcessWithMonitoring(cmakeBuild, "SD.cpp CMake Build", "sdcpp_build");

            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sd.exe" : "sd";
            string executablePath = FindExecutableInDirectory(buildDir, executableName);
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                Logs.Error($"[SDcpp] Could not find built {executableName} in CMake build output under '{buildDir}'.");
                return null;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    Process chmodProc = Process.Start("chmod", $"+x \"{executablePath}\"");
                    chmodProc?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[SDcpp] Could not set executable permission on built SD.cpp binary: {ex.Message}");
                }
            }

            return executablePath;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error while building SD.cpp from source: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads and extracts the SD.cpp binary
    /// </summary>
    private static async Task<string> DownloadAndExtract(DownloadInfo downloadInfo)
    {
        try
        {
            // Create download directory
            string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmUI", "SDcpp");
            Directory.CreateDirectory(downloadDir);

            string zipPath = Path.Combine(downloadDir, downloadInfo.FileName);
            string extractDir = Path.Combine(downloadDir, Path.GetFileNameWithoutExtension(downloadInfo.FileName));

            Logs.Info($"[SDcpp] Downloading {downloadInfo.FileName} ({downloadInfo.Size / 1024 / 1024:F1} MB)...");
            
            // Download file
            using (var response = await HttpClient.GetAsync(downloadInfo.DownloadUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(zipPath);
                await response.Content.CopyToAsync(fileStream);
            }

            Logs.Info("[SDcpp] Download completed, extracting...");

            // Extract ZIP
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // Find the executable
            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sd.exe" : "sd";
            string executablePath = FindExecutableInDirectory(extractDir, executableName);

            if (string.IsNullOrEmpty(executablePath))
            {
                Logs.Error($"[SDcpp] Could not find {executableName} in extracted files");
                return null;
            }

            // Make executable on Unix systems
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var process = System.Diagnostics.Process.Start("chmod", $"+x \"{executablePath}\"");
                    process?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[SDcpp] Could not set executable permissions: {ex.Message}");
                }
            }

            // Clean up ZIP file
            try
            {
                File.Delete(zipPath);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[SDcpp] Could not delete temporary ZIP file: {ex.Message}");
            }

            return executablePath;
        }
        catch (Exception ex)
        {
            Logs.Error($"[SDcpp] Error downloading and extracting: {ex.Message}");
            return null;
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
