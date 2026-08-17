using System.Diagnostics;
using System.IO;
using ShortcutWheel.Models;

namespace ShortcutWheel.Services;

public class LaunchService
{
    /// <summary>
    /// Launches the shortcut on a background thread. Use this from UI event
    /// handlers so the wheel closes instantly and the target process startup
    /// never freezes the render thread.
    /// </summary>
    public async Task<bool> LaunchAsync(ShortcutItem item)
    {
        string? target = item.TargetPath;
        if (string.IsNullOrEmpty(target))
            return false;

        try
        {
            // Offload Process.Start to the thread pool. Even with
            // UseShellExecute=true, ShellExecuteEx can stall for hundreds of
            // milliseconds while resolving PATH entries or UAC elevation.
            return await Task.Run(() =>
            {
                using var process = CreateProcess(target, item.Arguments, item.WorkingDirectory, item.RunAsAdmin);
                return process.Start();
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch {item.TargetPath}: {ex.Message}");
            return false;
        }
    }

    private static Process CreateProcess(string target, string? args, string? workDir, bool runAsAdmin = false)
    {
        // .lnk files: let the shell handle them directly via UseShellExecute.
        // Calling ShortcutResolver.Resolve here would block the calling thread
        // on slow/network targets and cause the app to freeze.
        // Non-.lnk paths: set WorkingDirectory for file-system targets.
        if (!ShortcutResolver.IsShortcut(target) && workDir == null && IsFilePath(target))
            workDir = Path.GetDirectoryName(target);

        var psi = new ProcessStartInfo
        {
            FileName = target,
            Arguments = args ?? "",
            WorkingDirectory = workDir ?? "",
            UseShellExecute = true,
            Verb = runAsAdmin ? "runas" : "open"
        };

        return new Process { StartInfo = psi, EnableRaisingEvents = false };
    }

    /// <summary>
    /// Returns true for plain file-system paths (absolute or relative exe
    /// names like "calc.exe"). Returns false for anything that looks like a
    /// URI scheme (contains "://" or ends with ":").
    /// </summary>
    private static bool IsFilePath(string target)
    {
        if (string.IsNullOrEmpty(target)) return false;
        // URI schemes: "https://", "steam://", "ms-settings:", "mailto:", …
        if (target.Contains("://")) return false;
        if (target.EndsWith(':')) return false;
        return true;
    }
}
