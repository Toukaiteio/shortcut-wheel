using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShortcutWheel.Controls;
using ShortcutWheel.Models;
using ShortcutWheel.Services;

namespace ShortcutWheel.Views;

public partial class OverlayWindow : Window
{
    private readonly HotkeyService _hotkeyService;
    private readonly ConfigService _configService;
    private readonly LaunchService _launchService;
    private readonly ScreenService _screenService;
    private string? _cachedBackgroundImagePath;
    private DateTime _cachedBackgroundImageWriteTimeUtc;
    private System.Windows.Media.Imaging.BitmapImage? _cachedBackgroundImage;

    private readonly Stack<List<ShortcutItem>> _navigationStack = new();
    private List<ShortcutItem> _currentItems = new();
    private bool _wheelActive;

    // CSGO-style pagination: at most 6 wedges per page. When _currentItems
    // overflows, the last wedge becomes a "next page" placeholder.
    private const int PageSize = 6;
    private int _pageIndex;
    private static readonly ShortcutItem NextPageMarker = new ShortcutItem
    {
        Id = "__next_page__",
        Label = "下一页 ›"
    };

    private const int WHEEL_PADDING = 60;

    public OverlayWindow(HotkeyService hotkeyService, ConfigService configService,
        LaunchService launchService, ScreenService screenService)
    {
        InitializeComponent();

        _hotkeyService = hotkeyService;
        _configService = configService;
        _launchService = launchService;
        _screenService = screenService;

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.WheelDismissed += OnWheelDismissed;
        _hotkeyService.WheelVisibilityChanged += OnWheelVisibilityChanged;

        RadialMenuControl.WedgeClicked += OnWedgeClicked;
        RadialMenuControl.CenterClicked += OnCenterClicked;
    }

    /// <summary>
    /// Forces the WPF visual tree to complete its first layout pass so that
    /// elements like the background image render correctly the very first
    /// time ShowWheel is called. Without this, Visibility=Collapsed elements
    /// (like BgImage) only get measured/arranged after the window is shown,
    /// which means the first ShowWheel sees a blank background.
    /// </summary>
    public void PreWarm()
    {
        // Position off-screen and fully transparent so the user can't see it.
        var origLeft = Left;
        var origTop = Top;
        var origOpacity = Opacity;
        Left = -10000;
        Top = -10000;
        Opacity = 0;
        Show();
        UpdateLayout();
        Hide();
        Left = origLeft;
        Top = origTop;
        Opacity = origOpacity;
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        ShowWheel();
    }

    private void OnWheelDismissed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_wheelActive) return;

            // CSGO-style press-and-release select: if the cursor is over a
            // wedge when the mouse side button is released, treat it as a
            // click on that wedge. If the cursor is on the centre disc,
            // navigate back. If the cursor is outside the wheel, keep the
            // wheel OPEN so the user can continue interacting with the mouse
            // — releasing the side button no longer dismisses the wheel.
            var pos = Mouse.GetPosition(RadialMenuControl);
            int idx = RadialMenuControl.HitTestWedge(pos);
            if (idx >= 0)
            {
                OnWedgeClicked(this, idx);
            }
            else if (idx == -2)
            {
                NavigateBack();
            }
            // idx == -1: leave the wheel visible.
        });
    }

    private void OnWheelVisibilityChanged(bool visible)
    {
        Dispatcher.Invoke(() =>
        {
            if (visible) ShowWheel();
            else HideWheel();
        });
    }

    public void ShowWheel()
    {
        if (_wheelActive)
        {
            HideWheel();
            return;
        }

        var settings = _configService.Config.Settings;
        int wheelRadius = settings.WheelRadius;
        int overlaySize = wheelRadius * 2 + WHEEL_PADDING;

        // Size the window to fit the wheel
        Width = overlaySize;
        Height = overlaySize;
        RadialMenuControl.WheelRadius = wheelRadius;
        RadialMenuControl.CenterRadius = settings.CenterCircleRadius;

        ApplyPalette(settings);
        ApplyBackgroundImage(settings, wheelRadius);
        Opacity = Math.Clamp(settings.Opacity, 0.1, 1.0);

        // Get cursor position using Win32
        NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPos);
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        var desiredPos = new System.Windows.Point(
            cursorPos.x / dpiScale - overlaySize / 2,
            cursorPos.y / dpiScale - overlaySize / 2);

        var clamped = _screenService.ClampToScreenBounds(desiredPos,
            new System.Windows.Size(overlaySize, overlaySize), dpiScale);

        Left = clamped.X;
        Top = clamped.Y;

        _navigationStack.Clear();
        _currentItems = _configService.Config.RootItems;
        _pageIndex = 0;
        RadialMenuControl.SetItems(GetVisibleItems());
        RadialMenuControl.IsSubMenu = false;
        RadialMenuControl.AnimateIn();

        Show();
        Activate();
        Focus();
        _wheelActive = true;
    }

    public void HideWheel()
    {
        if (!_wheelActive) return;
        _wheelActive = false;
        RadialMenuControl.AnimateOut();

        // Belt-and-suspenders. Hide() alone occasionally appears to "stick"
        // when the launched process steals focus mid-call, so we also drop
        // Topmost and force Visibility=Hidden synchronously.
        Topmost = false;
        Visibility = Visibility.Hidden;
        Hide();
        Topmost = true; // restore for the next show
    }

    private void OnWedgeClicked(object? sender, int index)
    {
        var visible = GetVisibleItems();
        if (index < 0 || index >= visible.Count) return;

        var item = visible[index];

        // Special "next page" wedge.
        if (ReferenceEquals(item, NextPageMarker))
        {
            ChangePage(+1);
            return;
        }

        if (item.IsFolder)
        {
            // Folders always navigate — even when empty — so the user can
            // see the wheel update. Empty folders render the placeholder
            // disc so the user knows the folder exists but has no content yet.
            _navigationStack.Push(_currentItems);
            _currentItems = item.Children.ToList();
            _pageIndex = 0;
            RadialMenuControl.SetItems(GetVisibleItems());
            RadialMenuControl.IsSubMenu = true;
            RadialMenuControl.AnimateIn();
        }
        else if (!string.IsNullOrEmpty(item.TargetPath))
        {
            // Hide FIRST so the wheel never visually lingers, then launch
            // asynchronously on a background thread. UseShellExecute=true
            // can block for a noticeable moment while resolving PATH entries
            // or delegating to the shell, so we keep it off the UI thread.
            var captured = item;
            var closeAfterLaunch = _configService.Config.Settings.CloseWheelAfterLaunch;

            // Hide wheel first (default behavior) or keep it open based on setting
            if (closeAfterLaunch)
            {
                HideWheel();
            }

            _ = _launchService.LaunchAsync(captured)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        App.LogError($"Launch failed: {t.Exception?.GetBaseException()}");
                }, System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Returns the wedges that should be drawn for the current page.
    /// At most <see cref="PageSize"/> entries; if the underlying list
    /// overflows, the last wedge is replaced with the "next page" marker.
    /// </summary>
    private List<ShortcutItem> GetVisibleItems()
    {
        UpdatePageInfo();

        if (_currentItems.Count <= PageSize)
            return _currentItems;

        int totalPages = (int)Math.Ceiling(_currentItems.Count / (double)(PageSize - 1));
        if (_pageIndex < 0) _pageIndex = 0;
        if (_pageIndex >= totalPages) _pageIndex = totalPages - 1;

        int start = _pageIndex * (PageSize - 1);
        int remaining = _currentItems.Count - start;
        bool isLastPage = remaining <= PageSize;

        if (isLastPage)
        {
            return _currentItems.GetRange(start, remaining);
        }

        // Take 5 items + a "next page" wedge as the 6th slot.
        var page = _currentItems.GetRange(start, PageSize - 1);
        page.Add(NextPageMarker);
        return page;
    }

    private void UpdatePageInfo()
    {
        if (_currentItems.Count <= PageSize)
        {
            RadialMenuControl.PageInfo = null;
        }
        else
        {
            int totalPages = (int)Math.Ceiling(_currentItems.Count / (double)(PageSize - 1));
            int safeIndex = Math.Clamp(_pageIndex, 0, totalPages - 1);
            RadialMenuControl.PageInfo = $"{safeIndex + 1} / {totalPages}";
        }
    }

    private void ChangePage(int delta)
    {
        if (_currentItems.Count <= PageSize) return;

        int totalPages = (int)Math.Ceiling(_currentItems.Count / (double)(PageSize - 1));
        int next = (_pageIndex + delta + totalPages) % totalPages;
        if (next == _pageIndex) return;

        _pageIndex = next;
        RadialMenuControl.SetItems(GetVisibleItems());
        RadialMenuControl.AnimateIn();
    }

    private void OnCenterClicked(object? sender, EventArgs e)
    {
        NavigateBack();
    }

    private void NavigateBack()
    {
        if (_navigationStack.Count > 0)
        {
            _currentItems = _navigationStack.Pop();
            _pageIndex = 0;
            RadialMenuControl.SetItems(GetVisibleItems());
            RadialMenuControl.IsSubMenu = _navigationStack.Count > 0;
            RadialMenuControl.AnimateIn();
        }
        else
        {
            HideWheel();
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Right click anywhere navigates back one level (or closes the
        // wheel when already at the root). This matches the behaviour
        // users expect from CSGO-style radial menus.
        if (e.ChangedButton == System.Windows.Input.MouseButton.Right)
        {
            NavigateBack();
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(RadialMenuControl);
        int hit = RadialMenuControl.HitTestWedge(pos);
        if (hit == -1)
        {
            HideWheel();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // ESC always closes the wheel completely.
            HideWheel();
            e.Handled = true;
        }
        else if (e.Key == Key.Back ||
                 e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            // Backspace / Shift navigate back one menu level.
            NavigateBack();
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown || e.Key == Key.Right || e.Key == Key.Down)
        {
            ChangePage(+1);
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp || e.Key == Key.Left || e.Key == Key.Up)
        {
            ChangePage(-1);
            e.Handled = true;
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangePage(e.Delta > 0 ? -1 : +1);
        e.Handled = true;
    }

    private bool _externalDragActive;

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Don't close while the user is dragging a file onto the wheel.
        if (!_externalDragActive)
            HideWheel();
    }

    private void ApplyBackgroundImage(Models.AppSettings settings, int wheelRadius)
    {
        var path = settings.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            BgEllipse.Visibility = Visibility.Collapsed;
            RadialMenuControl.HasBackgroundImage = false;
            CompositionTarget.Rendering -= SyncBgImageClip;
            return;
        }

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(path);
            if (_cachedBackgroundImage == null ||
                !string.Equals(_cachedBackgroundImagePath, path, StringComparison.OrdinalIgnoreCase) ||
                _cachedBackgroundImageWriteTimeUtc != writeTimeUtc)
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                _cachedBackgroundImage = bmp;
                _cachedBackgroundImagePath = path;
                _cachedBackgroundImageWriteTimeUtc = writeTimeUtc;
            }

            BgImageBrush.ImageSource = _cachedBackgroundImage;
            BgEllipse.Width = wheelRadius * 2;
            BgEllipse.Height = wheelRadius * 2;
            BgEllipse.Opacity = Math.Clamp(settings.BackgroundImageOpacity, 0, 1);

            // Start collapsed (scale 0); animation grows to 1.
            BgEllipseScale.ScaleX = 0;
            BgEllipseScale.ScaleY = 0;

            BgEllipse.Visibility = Visibility.Visible;
            RadialMenuControl.HasBackgroundImage = true;

            CompositionTarget.Rendering -= SyncBgImageClip;
            CompositionTarget.Rendering += SyncBgImageClip;
        }
        catch
        {
            BgEllipse.Visibility = Visibility.Collapsed;
            RadialMenuControl.HasBackgroundImage = false;
        }
    }

    private void SyncBgImageClip(object? sender, EventArgs e)
    {
        if (!_wheelActive)
        {
            CompositionTarget.Rendering -= SyncBgImageClip;
            return;
        }
        double s = EaseOutCubic(RadialMenuControl.AnimationProgress);
        BgEllipseScale.ScaleX = s;
        BgEllipseScale.ScaleY = s;
    }

    private static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3);

    private void ApplyPalette(Models.AppSettings settings)
    {
        if (TryParseColor(settings.AccentColor, out var accent))
            RadialMenuControl.AccentColor = accent;
        if (TryParseColor(settings.HoverColor, out var hover))
            RadialMenuControl.HoverColor = hover;
        if (TryParseColor(settings.BackgroundColor, out var bg))
            RadialMenuControl.BackgroundColor = bg;
        if (!string.IsNullOrWhiteSpace(settings.TrimColor) &&
            TryParseColor(settings.TrimColor, out var trim))
            RadialMenuControl.TrimColor = trim;
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            if (obj is Color c)
            {
                color = c;
                return true;
            }
        }
        catch { }
        return false;
    }

    #region Drag & Drop

    private int _dragHoverIndex = -1;

    private void LayoutRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(DataFormats.Text))
        {
            _externalDragActive = true;
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void LayoutRoot_DragOver(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(RadialMenuControl);
        int idx = RadialMenuControl.HitTestWedge(pos);

        if (idx != _dragHoverIndex)
        {
            _dragHoverIndex = idx;
            RadialMenuControl.InvalidateVisual();
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void LayoutRoot_DragLeave(object sender, DragEventArgs e)
    {
        _externalDragActive = false;
        _dragHoverIndex = -1;
        RadialMenuControl.InvalidateVisual();
    }

    private void LayoutRoot_Drop(object sender, DragEventArgs e)
    {
        _externalDragActive = false;
        _dragHoverIndex = -1;

        string[]? files = null;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            files = e.Data.GetData(DataFormats.FileDrop) as string[];
        }
        else if (e.Data.GetDataPresent(DataFormats.Text))
        {
            string? text = e.Data.GetData(DataFormats.Text) as string;
            if (!string.IsNullOrEmpty(text))
                files = [text];
        }

        if (files == null || files.Length == 0)
        {
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(RadialMenuControl);
        int wedgeIndex = RadialMenuControl.HitTestWedge(pos);
        var visible = GetVisibleItems();

        // Resolve async to avoid blocking the UI thread on WScript.Shell COM calls.
        _ = ResolveAndAddDroppedFilesAsync(files, wedgeIndex, visible);
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task ResolveAndAddDroppedFilesAsync(
        string[] files, int wedgeIndex, List<ShortcutItem> visible)
    {
        foreach (string file in files)
        {
            ShortcutItem item;
            if (ShortcutResolver.IsShortcut(file))
            {
                var info = await ShortcutResolver.ResolveAsync(file);
                item = info != null
                    ? new ShortcutItem
                    {
                        Label = Path.GetFileNameWithoutExtension(file),
                        TargetPath = info.TargetPath,
                        Arguments = info.Arguments,
                        WorkingDirectory = info.WorkingDirectory
                    }
                    : new ShortcutItem
                    {
                        Label = Path.GetFileNameWithoutExtension(file),
                        TargetPath = file
                    };
            }
            else
            {
                item = new ShortcutItem
                {
                    Label = Path.GetFileNameWithoutExtension(file),
                    TargetPath = file
                };
            }

            if (wedgeIndex >= 0 && wedgeIndex < visible.Count &&
                !ReferenceEquals(visible[wedgeIndex], NextPageMarker) &&
                visible[wedgeIndex].IsFolder)
            {
                visible[wedgeIndex].Children.Add(item);
            }
            else
            {
                _currentItems.Add(item);
                if (_navigationStack.Count == 0)
                    _configService.Config.RootItems.Add(item);
            }
        }

        _configService.Save();
        RadialMenuControl.SetItems(GetVisibleItems());
        RadialMenuControl.InvalidateVisual();
    }

    #endregion
}
