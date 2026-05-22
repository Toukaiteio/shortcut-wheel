using System.Diagnostics;
using System.IO;
using ShortcutWheel.Models;

namespace ShortcutWheel.Services;

public class LaunchService
{
    public bool Launch(ShortcutItem item)
    {
        if (string.IsNullOrEmpty(item.TargetPath))
            return false;

        try
        {
            string target = item.TargetPath;
            string? args = item.Arguments;
            string? workDir = item.WorkingDirectory;

            // .lnk files: let the shell handle them directly via UseShellExecute.
            // Calling ShortcutResolver.Resolve here would block the UI thread
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
                Verb = "open"
            };

            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch {item.TargetPath}: {ex.Message}");
            return false;
        }
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
