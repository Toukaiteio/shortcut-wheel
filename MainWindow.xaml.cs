using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ShortcutWheel.Services;
using ShortcutWheel.Views;

namespace ShortcutWheel;

/// <summary>
/// MainWindow exists only to host a hidden hotkey HWND and own the system-tray
/// icon. It is never shown to the user. Its HWND is created via
/// WindowInteropHelper.EnsureHandle() in App.OnStartup so no title bar ever
/// flashes on the desktop.
/// </summary>
public partial class MainWindow : Window
{
    private HotkeyService _hotkeyService = null!;
    private ConfigService _configService = null!;
    private LaunchService _launchService = null!;
    private ScreenService _screenService = null!;
    private UpdateService _updateService = null!;
    private OverlayWindow _overlayWindow = null!;
    private HwndSource? _hotkeyWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public bool HotkeyRegistered { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        // Belt-and-suspenders: even though XAML already hides this window,
        // re-assert these in code so future XAML edits cannot make it visible.
        ShowInTaskbar = false;
        Visibility = Visibility.Hidden;
        ShowActivated = false;
    }

    /// <summary>
    /// Wires up services, the hidden hotkey window, and the tray icon.
    /// Call this AFTER the HWND has been created (via EnsureHandle) so that
    /// SourceInitialized has run.
    /// </summary>
    public void InitializeServices()
    {
        _configService = new ConfigService();
        _configService.Load();

        _hotkeyService = new HotkeyService();
        _launchService = new LaunchService();
        _screenService = new ScreenService();
        _updateService = new UpdateService();
        _overlayWindow = new OverlayWindow(_hotkeyService, _configService, _launchService, _screenService);

        // Message-only HWND for hotkey reception. Lives independently of any
        // visible window, so closing the overlay never tears it down.
        var hwndParams = new HwndSourceParameters("ShortcutWheel_Hotkey")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _hotkeyWindow = new HwndSource(hwndParams);
        _hotkeyWindow.AddHook(WndProc);

        _hotkeyService.SetWindowHandle(_hotkeyWindow.Handle);
        RefreshHotkeyRegistration();

        // Auto-reapply hotkeys whenever the config is saved (e.g. user changes
        // the modifiers, key, or mouse-button in ConfigWindow).
        _configService.ConfigChanged += (_, _) =>
            Dispatcher.Invoke(RefreshHotkeyRegistration);

        SetupSystemTray();

        string status = HotkeyRegistered
            ? "Running — press Ctrl+Alt+Space to open the wheel"
            : "WARNING: Hotkey registration failed (key may be in use)";
        App.LogInfo(status);

        // Background update check: wait until the app is fully idle, then
        // wait an extra 8 s before hitting GitHub. Failures are silent.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(8000);
                await CheckForUpdatesAsync(showWhenUpToDate: false);
            }
            catch (Exception ex) { App.LogError($"Auto update check failed: {ex.Message}"); }
        }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Shows a clearly-visible startup notification:
    ///   1. A custom CSGO-styled toast in the lower-right (always visible,
    ///      bypasses Windows 10/11 Focus Assist suppression).
    ///   2. A best-effort tray balloon (works if the user allows balloons).
    /// </summary>
    public void NotifyStartup()
    {
        string title = "快捷转盘 已启动";
        string body = HotkeyRegistered
            ? "按 Ctrl+Alt+Space 呼出转盘\n或按住鼠标侧键 (默认 X1)\n右键托盘图标可设置 / 退出"
            : "热键注册失败，请右键托盘图标 → 配置";

        // 1) Custom floating toast – guaranteed visible.
        Views.StartupToast.Show(title, body);

        // 2) Best-effort balloon (may be silently dropped by Windows).
        if (_trayIcon != null)
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = body;
            _trayIcon.BalloonTipIcon = HotkeyRegistered
                ? System.Windows.Forms.ToolTipIcon.Info
                : System.Windows.Forms.ToolTipIcon.Warning;
            try { _trayIcon.ShowBalloonTip(4000); } catch { }
        }
    }

    private void SetupSystemTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                System.Windows.Forms.Application.ExecutablePath),
            Text = "快捷转盘 — 按 Ctrl+Alt+Space 打开 (右键查看更多)",
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示帮助 / Show Help", null, (_, _) => ShowHelp());
        menu.Items.Add("配置 / Configure", null, (_, _) => OpenConfig());
        menu.Items.Add("重新加载配置 / Reload", null, (_, _) => ReloadConfig());
        menu.Items.Add("检查更新 / Check for updates", null, async (_, _) => await CheckForUpdatesAsync(showWhenUpToDate: true));
        menu.Items.Add("-");
        menu.Items.Add("退出 / Exit", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => OpenConfig();
        _trayIcon.MouseClick += (_, ev) =>
        {
            if (ev.Button == System.Windows.Forms.MouseButtons.Left)
                NotifyStartup();
        };
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            "快捷转盘正在后台运行。\n\n" +
            "• 按 Ctrl+Alt+Space 呼出转盘\n" +
            "• 或按住鼠标侧键 (默认 XButton1) 唤起\n" +
            "• 转盘出现后将快捷方式拖入扇区即可添加\n" +
            "• ESC 或点击空白区域关闭\n" +
            "• 右键托盘图标可进入配置\n",
            "快捷转盘 - 帮助",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            _hotkeyService?.OnHotkeyReceived();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OpenConfig()
    {
        Dispatcher.Invoke(() =>
        {
            var configWindow = new ConfigWindow(_configService);
            configWindow.Show();
            configWindow.Activate();
        });
    }

    private void ReloadConfig()
    {
        _configService.Load();
        RefreshHotkeyRegistration();
    }

    /// <summary>
    /// Re-registers both the keyboard hotkey and the mouse-side-button hook
    /// from the latest config. Safe to call repeatedly — RegisterKeyboardHotkey
    /// and RegisterMouseHotkey both unregister any prior hook first.
    /// </summary>
    private void RefreshHotkeyRegistration()
    {
        var hotkey = _configService.Config.Settings.Hotkey;
        HotkeyRegistered = _hotkeyService.RegisterKeyboardHotkey(hotkey);

        if (hotkey.MouseHotkeyEnabled)
        {
            _hotkeyService.RegisterMouseHotkey(hotkey.MouseButton);
        }
        else
        {
            _hotkeyService.UnregisterMouseHotkey();
        }
    }

    private void ExitApp()
    {
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Checks GitHub Releases for a newer version. When found, opens the
    /// UpdateWindow so the user can review the changelog and choose to
    /// install. When <paramref name="showWhenUpToDate"/> is true (manual
    /// trigger from tray menu), shows a notification on the up-to-date case.
    /// </summary>
    public async System.Threading.Tasks.Task CheckForUpdatesAsync(bool showWhenUpToDate = false)
    {
        var info = await _updateService.CheckForUpdatesAsync();

        Dispatcher.Invoke(() =>
        {
            if (info != null)
            {
                var dlg = new Views.UpdateWindow(_updateService, info)
                {
                    Owner = null
                };
                dlg.Show();
                dlg.Activate();
            }
            else if (showWhenUpToDate)
            {
                MessageBox.Show(
                    $"已是最新版本 (v{Services.UpdateService.GetCurrentVersion()})\nYou are on the latest version.",
                    "ShortcutWheel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        });
    }

    /// <summary>
    /// Cleanly tear down all native resources. Called from App.OnExit.
    /// </summary>
    public void DisposeResources()
    {
        try { _hotkeyService?.Dispose(); } catch { }
        try { _hotkeyWindow?.Dispose(); } catch { }
        try
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
        catch { }
    }
}
