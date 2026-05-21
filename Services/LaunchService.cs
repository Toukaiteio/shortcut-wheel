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

            // Resolve .lnk so we don't shell-execute through an extra layer.
            if (ShortcutResolver.IsShortcut(target))
            {
                var info = ShortcutResolver.Resolve(target);
                if (info != null)
                {
                    target = info.TargetPath;
                    args ??= info.Arguments;
                    workDir ??= info.WorkingDirectory;
                }
            }

            // Only set WorkingDirectory for real file-system paths.
            // URIs (https://, steam://, ms-settings:, etc.) have no
            // meaningful directory and Path.GetDirectoryName throws on them.
            if (workDir == null && IsFilePath(target))
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
