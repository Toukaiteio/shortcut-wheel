using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace ShortcutWheel.Services;

public class UpdateInfo
{
    public string Version { get; init; } = "";
    public string TagName { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long AssetSize { get; init; }
}

/// <summary>
/// Auto-update via GitHub Releases. Reads the latest release of the
/// configured repo, compares it to the running assembly's version, and
/// can download + replace the exe via a small batch helper.
/// </summary>
public class UpdateService
{
    private const string Owner = "Toukaiteio";
    private const string Repo = "shortcut-wheel";
    private const string AssetName = "ShortcutWheel.exe";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ShortcutWheel-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Returns information about a newer release if one exists, otherwise null.
    /// Network or parse errors are swallowed and logged.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var release = await Http.GetFromJsonAsync<GitHubRelease>(url, ct);
            if (release == null) return null;

            string tag = release.TagName ?? "";
            string version = tag.TrimStart('v', 'V');
            if (string.IsNullOrEmpty(version)) return null;

            if (!IsNewer(version, GetCurrentVersion())) return null;

            var asset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            return new UpdateInfo
            {
                Version = version,
                TagName = tag,
                ReleaseNotes = release.Body ?? "",
                ReleaseUrl = release.HtmlUrl ?? "",
                DownloadUrl = asset.BrowserDownloadUrl,
                AssetSize = asset.Size
            };
        }
        catch (Exception ex)
        {
            ShortcutWheel.App.LogError($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Streams the new exe to a temp file, reporting download progress in [0,1].
    /// Returns the temp file path on success.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo info,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "ShortcutWheel.update.exe");

        using var resp = await Http.GetAsync(info.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? info.AssetSize;
        long read = 0;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(tempPath);

        var buffer = new byte[81920];
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }

        return tempPath;
    }

    /// <summary>
    /// Spawns a detached batch script that waits for the current exe to be
    /// released, swaps it with the freshly-downloaded one, and relaunches.
    /// Then shuts the current process down.
    /// </summary>
    public void ApplyUpdateAndRestart(string tempExePath)
    {
        string targetExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName!;

        string scriptPath = Path.Combine(Path.GetTempPath(), "ShortcutWheel.update.bat");

        // Try to delete the running exe in a loop (it stays locked until the
        // current process exits). Once delete succeeds, move the new one in
        // place and relaunch it.
        string script =
            "@echo off\r\n" +
            "rem ShortcutWheel auto-update helper\r\n" +
            "timeout /t 2 /nobreak >NUL\r\n" +
            ":wait\r\n" +
            $"del \"{targetExe}\" >NUL 2>&1\r\n" +
            $"if exist \"{targetExe}\" (\r\n" +
            "  timeout /t 1 /nobreak >NUL\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"move /Y \"{tempExePath}\" \"{targetExe}\" >NUL\r\n" +
            $"start \"\" \"{targetExe}\"\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C \"\"{scriptPath}\"\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi);

        // Exit so the script can replace the exe.
        System.Windows.Application.Current.Shutdown();
    }

    public static string GetCurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly;
        var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion))
        {
            // Strip any "+commit" suffix.
            int plus = attr.InformationalVersion.IndexOf('+');
            return plus >= 0 ? attr.InformationalVersion[..plus] : attr.InformationalVersion;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(remote, out var rv) && Version.TryParse(local, out var lv))
            return rv > lv;
        // Fallback: lexicographic compare.
        return string.CompareOrdinal(remote, local) > 0;
    }

    // ── GitHub API DTOs ────────────────────────────────────────────────────

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}
