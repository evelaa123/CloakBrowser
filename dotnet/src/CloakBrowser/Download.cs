using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CloakBrowser;

/// <summary>Info about the current binary installation (returned by <see cref="Download.BinaryInfo"/>).</summary>
public sealed record CloakBinaryInfo(
    string Version,
    string BundledVersion,
    string Platform,
    string BinaryPath,
    bool Installed,
    string CacheDir,
    string DownloadUrl);

/// <summary>
/// Binary download and cache management for CloakBrowser.
/// Downloads the patched Chromium binary on first use, caches it locally.
/// Direct port of Python <c>cloakbrowser/download.py</c>.
/// </summary>
public static class Download
{
    // Auto-update check interval (1 hour).
    private const int UpdateCheckInterval = 3600;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"cloakbrowser-dotnet/{CloakVersion.Version}");
        return client;
    }

    private static bool _wrapperUpdateChecked;

    // -----------------------------------------------------------------------

    private static void ShowWelcome()
    {
        var marker = Path.Combine(Config.GetCacheDir(), ".welcome_shown");
        if (File.Exists(marker)) return;
        Console.Error.Write(
            "\n" +
            "  CloakBrowser — stealth Chromium for automation\n" +
            "  https://github.com/CloakHQ/CloakBrowser\n" +
            "\n" +
            "  Donate?  https://ko-fi.com/cloakhq\n" +
            "  Star us if CloakBrowser helps your project!\n" +
            "\n");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Ensure the stealth Chromium binary is available. Download if needed.
    /// Returns the path to the chrome executable. Set <c>CLOAKBROWSER_BINARY_PATH</c>
    /// to skip download and use a local build.
    /// </summary>
    public static async Task<string> EnsureBinaryAsync(CancellationToken ct = default)
    {
        // Check for local override first.
        var localOverride = Config.GetLocalBinaryOverride();
        if (!string.IsNullOrEmpty(localOverride))
        {
            if (!File.Exists(localOverride))
                throw new FileNotFoundException(
                    $"CLOAKBROWSER_BINARY_PATH set to '{localOverride}' but file does not exist");
            CloakLog.Info("Using local binary override: {0}", localOverride);
            return localOverride;
        }

        // Fail fast if no binary available for this platform.
        Config.CheckPlatformAvailable();

        // Check for auto-updated version first, then fall back to hardcoded.
        var effective = Config.GetEffectiveVersion();
        var binaryPath = Config.GetBinaryPath(effective);

        if (File.Exists(binaryPath) && IsExecutable(binaryPath))
        {
            CloakLog.Debug("Binary found in cache: {0} (version {1})", binaryPath, effective);
            ShowWelcome();
            MaybeTriggerUpdateCheck();
            return binaryPath;
        }

        // Fall back to platform's hardcoded version if effective version binary doesn't exist.
        var platformVersion = Config.GetChromiumVersion();
        if (effective != platformVersion)
        {
            var fallbackPath = Config.GetBinaryPath();
            if (File.Exists(fallbackPath) && IsExecutable(fallbackPath))
            {
                CloakLog.Debug("Binary found in cache: {0}", fallbackPath);
                MaybeTriggerUpdateCheck();
                return fallbackPath;
            }
        }

        // Download platform's hardcoded version.
        CloakLog.Info("Stealth Chromium {0} not found. Downloading for {1}...",
            platformVersion, Config.GetPlatformTag());
        await DownloadAndExtractAsync(null, ct).ConfigureAwait(false);

        binaryPath = Config.GetBinaryPath();
        if (!File.Exists(binaryPath))
            throw new InvalidOperationException(
                $"Download completed but binary not found at expected path: {binaryPath}. " +
                "This may indicate a packaging issue. Please report at " +
                "https://github.com/CloakHQ/cloakbrowser/issues");

        MaybeTriggerUpdateCheck();
        return binaryPath;
    }

    /// <summary>Synchronous convenience wrapper around <see cref="EnsureBinaryAsync"/>.</summary>
    public static string EnsureBinary() =>
        EnsureBinaryAsync().GetAwaiter().GetResult();

    private static async Task DownloadAndExtractAsync(string? version, CancellationToken ct)
    {
        var primaryUrl = Config.GetDownloadUrl(version);
        var fallbackUrl = Config.GetFallbackDownloadUrl(version);
        var binaryDir = Config.GetBinaryDir(version);
        var binaryPath = Config.GetBinaryPath(version);

        Directory.CreateDirectory(Path.GetDirectoryName(binaryDir)!);

        var tmpPath = Path.Combine(Path.GetTempPath(),
            $"cloakbrowser-{Guid.NewGuid():N}{Config.GetArchiveExt()}");

        try
        {
            // Try primary, fall back to GitHub Releases (skip fallback if custom URL).
            try
            {
                await DownloadFileAsync(primaryUrl, tmpPath, ct).ConfigureAwait(false);
            }
            catch (Exception primaryErr)
            {
                if (Environment.GetEnvironmentVariable("CLOAKBROWSER_DOWNLOAD_URL") != null)
                    throw;
                CloakLog.Warning("Primary download failed ({0}), trying GitHub Releases...", primaryErr.Message);
                await DownloadFileAsync(fallbackUrl, tmpPath, ct).ConfigureAwait(false);
            }

            // Verify checksum before extraction.
            var skipChecksum = (Environment.GetEnvironmentVariable("CLOAKBROWSER_SKIP_CHECKSUM") ?? "")
                .ToLowerInvariant() == "true";
            if (!skipChecksum)
                await VerifyDownloadChecksumAsync(tmpPath, version, ct).ConfigureAwait(false);

            ExtractArchive(tmpPath, binaryDir, binaryPath);
            ShowWelcome();
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch (IOException) { }
        }
    }

    private static async Task VerifyDownloadChecksumAsync(string filePath, string? version, CancellationToken ct)
    {
        var checksums = await FetchChecksumsAsync(version, ct).ConfigureAwait(false);
        var tarballName = Config.GetArchiveName();

        if (checksums == null)
        {
            CloakLog.Warning("SHA256SUMS not available for this release — skipping checksum verification");
            return;
        }

        if (!checksums.TryGetValue(tarballName, out var expected))
        {
            CloakLog.Warning("SHA256SUMS found but no entry for {0} — skipping verification", tarballName);
            return;
        }

        VerifyChecksum(filePath, expected);
    }

    private static async Task<Dictionary<string, string>?> FetchChecksumsAsync(string? version, CancellationToken ct)
    {
        var v = version ?? Config.GetChromiumVersion();
        var hasCustomUrl = Environment.GetEnvironmentVariable("CLOAKBROWSER_DOWNLOAD_URL") != null;

        var urls = new List<string> { $"{Config.DownloadBaseUrl}/chromium-v{v}/SHA256SUMS" };
        if (!hasCustomUrl)
            urls.Add($"{Config.GitHubDownloadBaseUrl}/chromium-v{v}/SHA256SUMS");

        foreach (var url in urls)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseChecksums(text);
            }
            catch (Exception) { /* try next */ }
        }
        return null;
    }

    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var rawLine in text.Trim().Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var hashVal = parts[0];
                var filename = parts[1].TrimStart('*');
                result[filename] = hashVal.ToLowerInvariant();
            }
        }
        return result;
    }

    private static void VerifyChecksum(string filePath, string expectedHash)
    {
        using var sha256 = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(fs);
        var actual = Convert.ToHexString(hashBytes).ToLowerInvariant();
        if (actual != expectedHash.ToLowerInvariant())
            throw new InvalidOperationException(
                "Checksum verification failed!\n" +
                $"  Expected: {expectedHash}\n" +
                $"  Got:      {actual}\n" +
                "  File may be corrupted or tampered with. " +
                "Please retry or report at https://github.com/CloakHQ/cloakbrowser/issues");
        CloakLog.Info("Checksum verified: SHA-256 OK");
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken ct)
    {
        CloakLog.Info("Downloading from {0}", url);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? 0;
        long downloaded = 0;
        int lastLoggedPct = -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[8192];
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (total > 0)
            {
                int pct = (int)(downloaded / (double)total * 100);
                if (pct >= lastLoggedPct + 10)
                {
                    lastLoggedPct = pct;
                    CloakLog.Info("Download progress: {0}% ({1}/{2} MB)",
                        pct, downloaded / (1024 * 1024), total / (1024 * 1024));
                }
            }
        }

        CloakLog.Info("Download complete: {0} MB", new FileInfo(dest).Length / (1024 * 1024));
    }

    private static void ExtractArchive(string archivePath, string destDir, string? binaryPath)
    {
        CloakLog.Info("Extracting to {0}", destDir);

        // Clean existing dir if partial download existed.
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);

        Directory.CreateDirectory(destDir);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ExtractZip(archivePath, destDir);
        else
            ExtractTar(archivePath, destDir);

        // If extracted into a single subdirectory, flatten it (but never .app bundles).
        FlattenSingleSubdir(destDir);

        var bp = binaryPath ?? Config.GetBinaryPath();
        if (File.Exists(bp))
            MakeExecutable(bp);

        // macOS: remove quarantine/provenance xattrs to prevent Gatekeeper prompts.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            RemoveQuarantine(destDir);

        if (File.Exists(bp))
            CloakLog.Info("Binary ready: {0}", bp);
    }

    private static void ExtractTar(string archivePath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        using var fileStream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            // Allow symlinks — macOS .app bundles require them (Framework layout).
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                var linkTarget = entry.LinkName;
                if (Path.IsPathRooted(linkTarget) || linkTarget.Split('/').Contains(".."))
                {
                    CloakLog.Warning("Skipping suspicious symlink: {0} -> {1}", entry.Name, linkTarget);
                    continue;
                }
                var linkPath = Path.Combine(destDir, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                entry.ExtractToFile(linkPath, overwrite: true);
                continue;
            }

            var memberPath = Path.GetFullPath(Path.Combine(destDir, entry.Name));
            if (!memberPath.StartsWith(destFull, StringComparison.Ordinal))
                throw new InvalidOperationException($"Archive contains path traversal: {entry.Name}");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(memberPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(memberPath)!);
            entry.ExtractToFile(memberPath, overwrite: true);
        }
    }

    private static void ExtractZip(string archivePath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        using var zf = ZipFile.OpenRead(archivePath);
        foreach (var info in zf.Entries)
        {
            var memberPath = Path.GetFullPath(Path.Combine(destDir, info.FullName));
            if (!memberPath.StartsWith(destFull, StringComparison.Ordinal))
                throw new InvalidOperationException($"Archive contains path traversal: {info.FullName}");
        }
        ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
    }

    private static void FlattenSingleSubdir(string destDir)
    {
        var entries = Directory.GetFileSystemEntries(destDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            var subdir = entries[0];
            var name = Path.GetFileName(subdir);
            // Never flatten .app bundles — macOS needs the bundle structure.
            if (name.EndsWith(".app", StringComparison.Ordinal))
            {
                CloakLog.Debug("Keeping .app bundle intact: {0}", name);
                return;
            }
            CloakLog.Debug("Flattening single subdirectory: {0}", name);
            foreach (var item in Directory.GetFileSystemEntries(subdir))
            {
                var target = Path.Combine(destDir, Path.GetFileName(item));
                if (Directory.Exists(item))
                    Directory.Move(item, target);
                else
                    File.Move(item, target);
            }
            Directory.Delete(subdir, recursive: true);
        }
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static void MakeExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static void RemoveQuarantine(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("xattr")
            {
                ArgumentList = { "-cr", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            CloakLog.Debug("Removed quarantine attributes from {0}", path);
        }
        catch (Exception)
        {
            CloakLog.Debug("Failed to remove quarantine attributes");
        }
    }

    /// <summary>Remove all cached binaries. Forces re-download on next launch.</summary>
    public static void ClearCache()
    {
        var cacheDir = Config.GetCacheDir();
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true);
            CloakLog.Info("Cache cleared: {0}", cacheDir);
        }
    }

    /// <summary>Return info about the current binary installation.</summary>
    public static CloakBinaryInfo BinaryInfo()
    {
        var effective = Config.GetEffectiveVersion();
        var binaryPath = Config.GetBinaryPath(effective);
        return new CloakBinaryInfo(
            Version: effective,
            BundledVersion: Config.ChromiumVersion,
            Platform: Config.GetPlatformTag(),
            BinaryPath: binaryPath,
            Installed: File.Exists(binaryPath),
            CacheDir: Config.GetBinaryDir(effective),
            DownloadUrl: Config.GetDownloadUrl(effective));
    }

    // -----------------------------------------------------------------------
    // Auto-update
    // -----------------------------------------------------------------------

    /// <summary>
    /// Manually check for a newer Chromium version. Returns new version or null.
    /// Unlike the background check in EnsureBinary, this blocks until complete.
    /// </summary>
    public static async Task<string?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var latest = await GetLatestChromiumVersionAsync(ct).ConfigureAwait(false);
        if (latest == null) return null;
        if (!Config.VersionNewer(latest, Config.GetChromiumVersion())) return null;

        var binaryDir = Config.GetBinaryDir(latest);
        if (Directory.Exists(binaryDir))
        {
            WriteVersionMarker(latest);
            return latest;
        }

        CloakLog.Info("Downloading Chromium {0}...", latest);
        await DownloadAndExtractAsync(latest, ct).ConfigureAwait(false);
        WriteVersionMarker(latest);
        return latest;
    }

    /// <summary>Synchronous convenience wrapper around <see cref="CheckForUpdateAsync"/>.</summary>
    public static string? CheckForUpdate() => CheckForUpdateAsync().GetAwaiter().GetResult();

    private static bool ShouldCheckForUpdate()
    {
        if ((Environment.GetEnvironmentVariable("CLOAKBROWSER_AUTO_UPDATE") ?? "").ToLowerInvariant() == "false")
            return false;
        if (Config.GetLocalBinaryOverride() != null) return false;
        if (Environment.GetEnvironmentVariable("CLOAKBROWSER_DOWNLOAD_URL") != null) return false;

        var checkFile = Path.Combine(Config.GetCacheDir(), ".last_update_check");
        if (File.Exists(checkFile))
        {
            try
            {
                var lastCheck = double.Parse(File.ReadAllText(checkFile).Trim());
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                if (now - lastCheck < UpdateCheckInterval)
                    return false;
            }
            catch (Exception ex) when (ex is FormatException or IOException) { }
        }
        return true;
    }

    private static async Task<string?> GetLatestChromiumVersionAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{Config.GitHubApiUrl}?per_page=10";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var platformTarball = Config.GetArchiveName();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var draft = release.TryGetProperty("draft", out var d) && d.GetBoolean();
                if (tag.StartsWith("chromium-v", StringComparison.Ordinal) && !draft)
                {
                    if (release.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("name", out var n) && n.GetString() == platformTarball)
                                return tag["chromium-v".Length..];
                        }
                    }
                }
            }
            return null;
        }
        catch (Exception)
        {
            CloakLog.Debug("Auto-update check failed");
            return null;
        }
    }

    private static void WriteVersionMarker(string version)
    {
        var cacheDir = Config.GetCacheDir();
        Directory.CreateDirectory(cacheDir);
        var marker = Path.Combine(cacheDir, $"latest_version_{Config.GetPlatformTag()}");
        var tmp = marker + ".tmp";
        File.WriteAllText(tmp, version);
        if (File.Exists(marker)) File.Delete(marker);
        File.Move(tmp, marker);
    }

    private static async Task CheckWrapperUpdateAsync()
    {
        if ((Environment.GetEnvironmentVariable("CLOAKBROWSER_AUTO_UPDATE") ?? "").ToLowerInvariant() == "false")
            return;
        if (Environment.GetEnvironmentVariable("CLOAKBROWSER_DOWNLOAD_URL") != null)
            return;
        try
        {
            using var resp = await Http.GetAsync("https://www.nuget.org/packages/CloakBrowser")
                .ConfigureAwait(false);
            // NuGet has no simple version JSON identical to PyPI; best-effort no-op log.
            CloakLog.Debug("Wrapper update check completed");
        }
        catch (Exception)
        {
            CloakLog.Debug("Wrapper update check failed");
        }
    }

    private static async Task CheckAndDownloadUpdateAsync()
    {
        try
        {
            var checkFile = Path.Combine(Config.GetCacheDir(), ".last_update_check");
            Directory.CreateDirectory(Path.GetDirectoryName(checkFile)!);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            File.WriteAllText(checkFile, now.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var platformVersion = Config.GetChromiumVersion();
            var latest = await GetLatestChromiumVersionAsync(CancellationToken.None).ConfigureAwait(false);
            if (latest == null) return;
            if (!Config.VersionNewer(latest, platformVersion)) return;

            if (Directory.Exists(Config.GetBinaryDir(latest)))
            {
                WriteVersionMarker(latest);
                return;
            }

            CloakLog.Info("Newer Chromium available: {0} (current: {1}). Downloading in background...",
                latest, platformVersion);
            await DownloadAndExtractAsync(latest, CancellationToken.None).ConfigureAwait(false);
            WriteVersionMarker(latest);
            CloakLog.Info("Background update complete: Chromium {0} ready. Will use on next launch.", latest);
        }
        catch (Exception)
        {
            CloakLog.Debug("Background update failed");
        }
    }

    private static void MaybeTriggerUpdateCheck()
    {
        // Wrapper update: once per process, not rate-limited.
        if (!_wrapperUpdateChecked)
        {
            _wrapperUpdateChecked = true;
            _ = Task.Run(CheckWrapperUpdateAsync);
        }

        // Binary update: rate-limited to once per hour.
        if (!ShouldCheckForUpdate()) return;
        _ = Task.Run(CheckAndDownloadUpdateAsync);
    }
}
