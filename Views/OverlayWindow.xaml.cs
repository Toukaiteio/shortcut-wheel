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
            // Hide FIRST so the wheel never visually lingers while the
            // shell launches the process (UseShellExecute=true can stall
            // on PATH-only targets like "code"). The actual Launch is
            // dispatched at Background priority so the hide is painted
            // immediately.
            HideWheel();
            var captured = item;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _launchService.Launch(captured); }
                catch (Exception ex) { App.LogError($"Launch failed: {ex}"); }
            }), System.Windows.Threading.DispatcherPriority.Background);
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

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        HideWheel();
    }

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
        _dragHoverIndex = -1;
        RadialMenuControl.InvalidateVisual();
    }

    private void LayoutRoot_Drop(object sender, DragEventArgs e)
    {
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

        foreach (string file in files)
        {
            // Defer .lnk resolution to LaunchService so dropping doesn't
            // block the UI thread on slow / hung COM calls.
            ShortcutItem item = new ShortcutItem
            {
                Label = Path.GetFileNameWithoutExtension(file),
                TargetPath = file
            };

            // If the user dropped onto an existing folder wedge, add the
            // shortcut as a child of that folder. Otherwise add it to the
            // current level.
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
        e.Handled = true;
    }

    #endregion
}
