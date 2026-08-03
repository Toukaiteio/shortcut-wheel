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
    private ConfigWindow? _configWindow;
    private HwndSource? _hotkeyWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _hotkeysEnabled = true;

    public bool HotkeyRegistered { get; private set; }
    public bool HotkeysEnabled => _hotkeysEnabled;

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
        // Apply saved language preference, then push strings into ResourceDictionary.
        Services.LocalizationService.SetLanguage(_configService.Config.Settings.Language);
        // Migrate any .lnk TargetPaths to real exe paths in the background.
        _ = _configService.MigrateLnkPathsAsync();

        _hotkeyService = new HotkeyService();
        _launchService = new LaunchService();
        _screenService = new ScreenService();
        _updateService = new UpdateService();
        _overlayWindow = new OverlayWindow(_hotkeyService, _configService, _launchService, _screenService);
        _overlayWindow.PreWarm();

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
                if (_configService.Config.Settings.AutoUpdateEnabled)
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
        string title = Services.LocalizationService.Get("StartupTitle");
        string body = HotkeyRegistered
            ? Services.LocalizationService.Get("StartupBody")
            : Services.LocalizationService.Get("StartupBodyFail");

        Views.StartupToast.Show(title, body);

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
            Text = Services.LocalizationService.Get("TrayTooltip"),
            Visible = true
        };

        BuildTrayMenu();

        // Rebuild tray menu when language changes.
        Services.LocalizationService.LanguageChanged += (_, _) =>
            Dispatcher.Invoke(BuildTrayMenu);

        _trayIcon.DoubleClick += (_, _) => OpenConfig();
        _trayIcon.MouseClick += (_, ev) =>
        {
            if (ev.Button == System.Windows.Forms.MouseButtons.Left)
                NotifyStartup();
        };
    }

    private void BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var hotkeyToggle = new System.Windows.Forms.ToolStripMenuItem
        {
            Text = Services.LocalizationService.Get("TrayHotkeys"),
            Checked = _hotkeysEnabled
        };
        hotkeyToggle.Click += (_, _) => ToggleHotkeys();
        menu.Items.Add(hotkeyToggle);
        menu.Items.Add("-");
        menu.Items.Add(Services.LocalizationService.Get("TrayHelp"), null, (_, _) => ShowHelp());
        menu.Items.Add(Services.LocalizationService.Get("TrayConfigure"), null, (_, _) => OpenConfig());
        menu.Items.Add(Services.LocalizationService.Get("TrayReload"), null, (_, _) => ReloadConfig());
        menu.Items.Add(Services.LocalizationService.Get("TrayCheckUpdate"), null, async (_, _) => await CheckForUpdatesAsync(showWhenUpToDate: true));
        menu.Items.Add("-");
        menu.Items.Add(Services.LocalizationService.Get("TrayExit"), null, (_, _) => ExitApp());
        if (_trayIcon != null)
        {
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Text = Services.LocalizationService.Get("TrayTooltip");
        }
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            "快捷转盘正在后台运行。\n\n" +
            "• 按 Ctrl+Alt+Space 呼出转盘\n" +
            "• 或按住鼠标侧键 (默认 XButton1) 唤起\n" +
            "• 转盘出现后将快捷方式拖入扇区即可添加\n" +
            "• ESC 或点击空白区域关闭\n" +
            "• 托盘菜单可快速禁用 / 启用快捷键\n" +
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
            if (_configWindow == null)
            {
                _configWindow = new ConfigWindow(_configService);
                _configWindow.Closed += (_, _) => _configWindow = null;
            }

            if (!_configWindow.IsVisible)
                _configWindow.Show();

            if (_configWindow.WindowState == WindowState.Minimized)
                _configWindow.WindowState = WindowState.Normal;

            _configWindow.Activate();
        });
    }

    private void ReloadConfig()
    {
        _configService.Load();
        RefreshHotkeyRegistration();
    }

    private void ToggleHotkeys()
    {
        _hotkeysEnabled = !_hotkeysEnabled;
        RefreshHotkeyRegistration();
        BuildTrayMenu();
        App.LogInfo(_hotkeysEnabled ? "Global hotkeys enabled from tray." : "Global hotkeys disabled from tray.");
    }

    /// <summary>
    /// Re-registers both the keyboard hotkey and the mouse-side-button hook
    /// from the latest config. Safe to call repeatedly — RegisterKeyboardHotkey
    /// and RegisterMouseHotkey both unregister any prior hook first.
    /// </summary>
    private void RefreshHotkeyRegistration()
    {
        if (!_hotkeysEnabled)
        {
            _hotkeyService.UnregisterKeyboardHotkey();
            _hotkeyService.UnregisterMouseHotkey();
            HotkeyRegistered = false;
            return;
        }

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
    /// Checks GitHub Releases for a newer version.
    /// - If <paramref name="showWhenUpToDate"/> is true (manual trigger), always shows result.
    /// - If silent update is enabled and this is an auto-check, downloads and
    ///   installs without showing a window.
    /// - If a fullscreen app is running during auto-check, defers the popup
    ///   (retries every 5 minutes) until the screen is no longer occupied.
    /// </summary>
    public async System.Threading.Tasks.Task CheckForUpdatesAsync(bool showWhenUpToDate = false)
    {
        var info = await _updateService.CheckForUpdatesAsync();
        bool silent = _configService.Config.Settings.SilentUpdate;

        Dispatcher.Invoke(() =>
        {
            if (info == null)
            {
                if (showWhenUpToDate)
                    MessageBox.Show(
                        $"已是最新版本 (v{Services.UpdateService.GetCurrentVersion()})\nYou are on the latest version.",
                        "ShortcutWheel", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Silent update: download and apply without any UI.
            if (silent && !showWhenUpToDate)
            {
                _ = SilentInstallAsync(info);
                return;
            }

            if (!showWhenUpToDate)
            {
                Services.UpdateService.SetPendingUpdate(info);
                App.LogInfo($"Update available: {info.TagName}. Notification is shown in the config window.");
                return;
            }

            ShowUpdateWindow(info);
        });
    }

    private void ShowUpdateWindow(Services.UpdateInfo info)
    {
        var dlg = new Views.UpdateWindow(_updateService, info) { Owner = null };
        dlg.Show();
        dlg.Activate();
    }

    private async System.Threading.Tasks.Task SilentInstallAsync(Services.UpdateInfo info)
    {
        try
        {
            App.LogInfo($"Silent update: downloading {info.TagName}...");
            string temp = await _updateService.DownloadUpdateAsync(info);
            App.LogInfo("Silent update: applying...");
            _updateService.ApplyUpdateAndRestart(temp);
        }
        catch (Exception ex)
        {
            App.LogError($"Silent update failed: {ex.Message}");
        }
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
