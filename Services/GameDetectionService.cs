using System.Diagnostics;
using System.Text;

namespace ShortcutWheel.Services;

/// <summary>
/// Best-effort foreground-game detection used only to suppress accidental
/// wheel activation. It deliberately does not inspect or alter game state.
///
/// There is no universal Windows API flag that says "this is a game", so the
/// detector combines several low-risk signals:
///   - common game engine window classes;
///   - known game process names and game-install locations;
///   - a foreground window covering the whole monitor (exclusive or borderless
///     fullscreen), excluding common desktop/media applications.
/// </summary>
public static class GameDetectionService
{
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;

    private static readonly HashSet<string> KnownGameProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "csgo", "dota2", "valorant", "valorant-win64-shipping",
        "leagueoflegends", "leagueclientux", "r5apex", "apex_legends",
        "overwatch", "overwatchlauncher", "fortniteclient-win64-shipping",
        "rocketleague", "eldenring", "sekiro", "palworld-win64-shipping",
        "monsterhunterworld", "mhws", "terraria", "tmodloader",
        "factorio", "stardewvalley", "deadbydaylight", "thefinals",
        "baldursgate3", "gta5"
    };

    private static readonly string[] GameEngineWindowClasses =
    {
        "UnityWndClass",
        "UnrealWindow",
        "SDL_app",
        "GLFW30",
        "CryENGINE",
        "Godot"
    };

    private static readonly string[] GamePathMarkers =
    {
        @"\steamapps\common\",
        @"\epic games\",
        @"\riot games\",
        @"\battle.net\",
        @"\ubisoft game launcher\games\",
        @"\gog galaxy\games\",
        @"\xboxgames\",
        @"\games\",
        @"\游戏\"
    };

    private static readonly HashSet<string> SafeFullscreenProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore",
        "vlc", "potplayer", "mpc-hc", "mpc-hc64", "wmplayer",
        "powerpnt", "winword", "excel", "acrord32", "applicationframehost",
        "explorer", "dwm", "searchhost", "shellexperiencehost", "gamebar",
        "obs64", "obs32", "devenv", "code"
    };

    // The foreground process/window rarely changes within a few hundred
    // milliseconds, and detection (Process.Path, window inspection) is not
    // free. Cache the last result for a short window so repeated ShowWheel
    // calls don't re-run the expensive checks on the UI thread.
    private static readonly object _cacheLock = new();
    private static long _lastCheckTicks;
    private static bool _lastResult;
    private const long CacheTtlMs = 200;

    public static bool IsGameForeground()
    {
        long now = Environment.TickCount64;
        lock (_cacheLock)
        {
            if (now - _lastCheckTicks < CacheTtlMs)
                return _lastResult;
        }

        bool result = ComputeIsGameForeground();

        lock (_cacheLock)
        {
            _lastCheckTicks = now;
            _lastResult = result;
        }
        return result;
    }

    private static bool ComputeIsGameForeground()
    {
        try
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window))
                return false;

            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0 || processId == Environment.ProcessId)
                return false;

            using var process = Process.GetProcessById((int)processId);
            string processName = process.ProcessName;
            string processPath = TryGetProcessPath(process);
            string windowClass = ReadWindowClass(window);

            if (KnownGameProcessNames.Contains(processName) ||
                IsGameLikeProcessName(processName) ||
                IsGameEngineWindowClass(windowClass) ||
                IsGameInstallPath(processPath))
            {
                return true;
            }

            if (!SafeFullscreenProcessNames.Contains(processName) && IsFullscreenWindow(window))
                return true;

            // Some games use a large borderless window without covering the
            // final few pixels of the monitor. Require a game-like class or
            // path here so ordinary borderless productivity apps are not
            // suppressed merely because they are large.
            return !SafeFullscreenProcessNames.Contains(processName) &&
                   IsLargeBorderlessWindow(window) &&
                   (IsGameEngineWindowClass(windowClass) || IsGameInstallPath(processPath) ||
                    IsGameLikeProcessName(processName));
        }
        catch
        {
            // Detection must never prevent the application from starting or
            // turn a protected process into a user-visible error.
            return false;
        }
    }

    private static bool IsGameLikeProcessName(string processName)
    {
        return processName.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
               processName.EndsWith("-Win64-Test", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("GameClient", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("game", StringComparison.OrdinalIgnoreCase) ||
               processName.EndsWith("game", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameEngineWindowClass(string windowClass)
    {
        return GameEngineWindowClasses.Any(engineClass =>
            string.Equals(windowClass, engineClass, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGameInstallPath(string processPath)
    {
        return GamePathMarkers.Any(marker =>
            processPath.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string TryGetProcessPath(Process process)
    {
        // QueryFullProcessImageName reads the native process image path
        // directly and is far cheaper / less likely to stall than
        // Process.MainModule, which enumerates loaded modules and can block
        // on protected processes.
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process.Handle, 0, buffer, ref size)
                ? buffer.ToString()
                : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return NativeMethods.GetClassName(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static bool IsFullscreenWindow(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out var windowRect))
            return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        const int tolerance = 2;
        var bounds = monitorInfo.rcMonitor;
        return windowRect.Left <= bounds.Left + tolerance &&
               windowRect.Top <= bounds.Top + tolerance &&
               windowRect.Right >= bounds.Right - tolerance &&
               windowRect.Bottom >= bounds.Bottom - tolerance;
    }

    private static bool IsLargeBorderlessWindow(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out var windowRect))
            return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        long style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
        bool borderless = (style & WsCaption) == 0 && (style & WsThickFrame) == 0;
        if (!borderless)
            return false;

        var bounds = monitorInfo.rcMonitor;
        int windowWidth = Math.Max(0, windowRect.Right - windowRect.Left);
        int windowHeight = Math.Max(0, windowRect.Bottom - windowRect.Top);
        int monitorWidth = Math.Max(1, bounds.Right - bounds.Left);
        int monitorHeight = Math.Max(1, bounds.Bottom - bounds.Top);

        return windowWidth >= monitorWidth * 0.75 && windowHeight >= monitorHeight * 0.65;
    }
}
