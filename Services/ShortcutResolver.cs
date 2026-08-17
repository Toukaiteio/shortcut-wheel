using System.Reflection;

namespace ShortcutWheel.Services;

public static class ShortcutResolver
{
    public class ShortcutInfo
    {
        public string TargetPath { get; init; } = "";
        public string? Arguments { get; init; }
        public string? WorkingDirectory { get; init; }
    }

    public static bool IsShortcut(string? path) =>
        !string.IsNullOrEmpty(path) &&
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Synchronous resolve — only call from a background thread.
    /// Uses WScript.Shell via reflection (no direct COM reference needed).
    /// </summary>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (!IsShortcut(shortcutPath) || !System.IO.File.Exists(shortcutPath))
            return null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;

            object shell = Activator.CreateInstance(shellType)!;
            object sc = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;

            var scType = sc.GetType();
            string target = (string)scType.InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, sc, null)!;

            if (string.IsNullOrEmpty(target)) return null;

            string? args = scType.InvokeMember("Arguments",
                BindingFlags.GetProperty, null, sc, null) as string;
            string? workDir = scType.InvokeMember("WorkingDirectory",
                BindingFlags.GetProperty, null, sc, null) as string;

            return new ShortcutInfo
            {
                TargetPath = target,
                Arguments = string.IsNullOrEmpty(args) ? null : args,
                WorkingDirectory = string.IsNullOrEmpty(workDir) ? null : workDir
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Async resolve — safe to call from the UI thread.
    /// Runs WScript.Shell on a background thread with a timeout.
    /// </summary>
    public static async Task<ShortcutInfo?> ResolveAsync(string shortcutPath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            // ConfigureAwait(false): this method's own continuation does not
            // touch UI state, so it need not resume on the UI thread. The
            // caller's continuation is unaffected.
            return await Task.Run(() => Resolve(shortcutPath), cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            App.LogError($"ShortcutResolver timed out for: {shortcutPath}");
            return null;
        }
    }
}
