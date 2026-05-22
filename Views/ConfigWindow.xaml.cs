using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ShortcutWheel.Models;
using ShortcutWheel.Services;
using ShortcutWheel.ViewModels;

namespace ShortcutWheel.Views;

public partial class ConfigWindow : Window
{
    private readonly ConfigService _configService;
    private readonly List<ShortcutItemViewModel> _viewModels = new();
    private ShortcutItemViewModel? _selectedItem;

    // Slider/CheckBox/TextBox events fire during InitializeComponent() and
    // again while LoadSettings() restores values. Both happen before the user
    // has touched anything, so we suppress writes until OnLoaded has finished.
    private bool _isLoaded;

    // Drag-and-drop state.
    private const string ItemDataFormat = "ShortcutWheel.ShortcutItemViewModel";
    private const double DragStartThreshold = 6.0;
    private Point _dragStartPoint;
    private ShortcutItemViewModel? _pendingDragItem;
    private DropIndicatorAdorner? _currentIndicator;
    private TreeViewItem? _currentIndicatorTarget;

    // Guards PropChanged while ShortcutTree_SelectedItemChanged is in the
    // middle of repopulating the textboxes. Without this, the very first
    // Prop*.Text assignment fires TextChanged → PropChanged, which reads
    // the *other* textboxes (still holding the previous selection's values)
    // and stamps them onto the newly-selected item — clobbering its
    // TargetPath/Arguments/WorkingDirectory.
    private bool _isPopulatingProps;

    private static readonly ShortcutWheel.Models.MouseButton[] MouseButtonValues =
        { Models.MouseButton.XButton1, Models.MouseButton.XButton2, Models.MouseButton.Middle,
          Models.MouseButton.Left };

    private static readonly string[] MouseButtonNames = { "XButton1 (Back)", "XButton2 (Forward)", "Middle", "Left" };

    public ConfigWindow(ConfigService configService)
    {
        // CRITICAL: assign BEFORE InitializeComponent. Slider's Minimum/Value
        // properties can clamp during XAML parsing and raise ValueChanged,
        // which would otherwise hit a null _configService and crash.
        _configService = configService;
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadShortcuts();
        LoadSettings();
        _isLoaded = true;
    }

    private void LoadShortcuts()
    {
        _viewModels.Clear();
        ShortcutTree.Items.Clear();

        foreach (var item in _configService.Config.RootItems)
        {
            var vm = new ShortcutItemViewModel(item);
            _viewModels.Add(vm);
            ShortcutTree.Items.Add(vm);
        }
    }

    private void LoadSettings()
    {
        var settings = _configService.Config.Settings;
        if (settings == null) return;

        // Hotkey modifiers
        SetCtrl.IsChecked = settings.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Control);
        SetAlt.IsChecked = settings.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt);
        SetShift.IsChecked = settings.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift);
        SetWin.IsChecked = settings.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows);

        // Key
        SetKey.ItemsSource = Enum.GetValues<System.Windows.Input.Key>();
        SetKey.SelectedItem = (System.Windows.Input.Key)System.Windows.Input.KeyInterop
            .KeyFromVirtualKey((int)settings.Hotkey.Key);

        // Mouse
        SetMouseEnabled.IsChecked = settings.Hotkey.MouseHotkeyEnabled;
        SetMouseButton.ItemsSource = MouseButtonNames;
        SetMouseButton.SelectedIndex = Array.IndexOf(MouseButtonValues, settings.Hotkey.MouseButton);
        SetMouseButton.IsEnabled = settings.Hotkey.MouseHotkeyEnabled;

        // Hold delay
        SetHoldDelay.Value = settings.Hotkey.HoldDelayMs;
        SetHoldDelayLabel.Text = $"{settings.Hotkey.HoldDelayMs}ms";

        // Appearance
        SetWheelRadius.Value = settings.WheelRadius;
        SetWheelRadiusLabel.Text = settings.WheelRadius.ToString();
        SetOpacity.Value = settings.Opacity;
        SetOpacityLabel.Text = settings.Opacity.ToString("F2");
        SetAccentColor.Text = settings.AccentColor;
        SetBgColor.Text = settings.BackgroundColor;

        // General
        SetStartWithWindows.IsChecked = settings.StartWithWindows;
        SetRunMinimized.IsChecked = settings.RunMinimized;
    }

    private void ShortcutTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedItem = e.NewValue as ShortcutItemViewModel;
        _isPopulatingProps = true;
        try
        {
            if (_selectedItem != null)
            {
                PropLabel.Text = _selectedItem.Label;
                PropTargetPath.Text = _selectedItem.TargetPath ?? "";
                PropArguments.Text = _selectedItem.Arguments ?? "";
                PropWorkDir.Text = _selectedItem.Item.WorkingDirectory ?? "";
                PropIsFolder.IsChecked = _selectedItem.IsFolder;
                PropHasChildren.IsChecked = _selectedItem.Children.Count > 0;
                PropBrowse.IsEnabled = true;
            }
            else
            {
                PropLabel.Text = "";
                PropTargetPath.Text = "";
                PropArguments.Text = "";
                PropWorkDir.Text = "";
                PropIsFolder.IsChecked = false;
                PropHasChildren.IsChecked = false;
                PropBrowse.IsEnabled = false;
            }
        }
        finally
        {
            _isPopulatingProps = false;
        }
    }

    private void BtnAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择应用程序或快捷方式 / Select an application or shortcut",
            Filter = "可执行文件与快捷方式 / Executables and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            var item = CreateItemFromPath(dialog.FileName);
            AddItemUnderSelection(item);
            _configService.Save();
            UpdateStatus($"已添加 / Added: {item.Label}");
        }
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = new ShortcutItem
        {
            Label = "New Folder",
            TargetPath = null  // null target path is what makes this a folder
        };

        AddItemUnderSelection(item);
        _configService.Save();
        UpdateStatus("Added new folder");
    }

    /// <summary>
    /// Adds <paramref name="item"/> as a child of the currently-selected
    /// folder, or at the root if the selection is empty / not a folder.
    /// Expands the parent and selects the new node so the user can see
    /// where it landed.
    /// </summary>
    private void AddItemUnderSelection(ShortcutItem item)
    {
        ShortcutItemViewModel newVm;
        ShortcutItemViewModel? parentVm = null;

        if (_selectedItem != null && _selectedItem.IsFolder)
        {
            parentVm = _selectedItem;
            newVm = parentVm.AddChild(item);
        }
        else
        {
            _configService.Config.RootItems.Add(item);
            newVm = new ShortcutItemViewModel(item);
            _viewModels.Add(newVm);
            ShortcutTree.Items.Add(newVm);
        }

        // Defer to the next layout pass so the TreeView has had a chance to
        // realise containers for new nodes. We expand the parent first, then
        // wait one more dispatcher cycle for child containers to be
        // generated, then select the new node.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (parentVm != null)
            {
                var parentContainer = FindTreeViewItem(ShortcutTree, parentVm);
                if (parentContainer != null)
                {
                    parentContainer.IsExpanded = true;
                    parentContainer.UpdateLayout();
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var container = FindTreeViewItem(ShortcutTree, newVm);
                if (container != null)
                {
                    container.IsSelected = true;
                    container.BringIntoView();
                }
                else
                {
                    // Couldn't realise the container — fall back to refreshing
                    // _selectedItem manually so the property panel updates.
                    _selectedItem = newVm;
                    PropLabel.Text = newVm.Label;
                    PropTargetPath.Text = newVm.TargetPath ?? "";
                    PropArguments.Text = newVm.Arguments ?? "";
                    PropWorkDir.Text = newVm.Item.WorkingDirectory ?? "";
                    PropIsFolder.IsChecked = newVm.IsFolder;
                    PropHasChildren.IsChecked = newVm.Children.Count > 0;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static System.Windows.Controls.TreeViewItem? FindTreeViewItem(
        System.Windows.Controls.ItemsControl parent, object item)
    {
        if (parent == null) return null;
        var direct = parent.ItemContainerGenerator.ContainerFromItem(item)
                     as System.Windows.Controls.TreeViewItem;
        if (direct != null) return direct;

        foreach (var child in parent.Items)
        {
            var childContainer = parent.ItemContainerGenerator.ContainerFromItem(child)
                                 as System.Windows.Controls.TreeViewItem;
            if (childContainer == null) continue;
            var found = FindTreeViewItem(childContainer, item);
            if (found != null) return found;
        }
        return null;
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;

        var result = MessageBox.Show($"Delete '{_selectedItem.Label}'?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        RemoveItem(_selectedItem.Item);
        LoadShortcuts();
        _configService.Save();
        UpdateStatus($"Deleted: {_selectedItem.Label}");
        _selectedItem = null;

        PropLabel.Text = "";
        PropTargetPath.Text = "";
        PropArguments.Text = "";
        PropWorkDir.Text = "";
    }

    private void RemoveItem(ShortcutItem item)
    {
        _configService.Config.RootItems.Remove(item);

        foreach (var root in _configService.Config.RootItems)
        {
            if (RemoveFromChildren(root, item))
                break;
        }
    }

    private static bool RemoveFromChildren(ShortcutItem parent, ShortcutItem target)
    {
        if (parent.Children.Remove(target)) return true;
        foreach (var child in parent.Children)
        {
            if (RemoveFromChildren(child, target))
                return true;
        }
        return false;
    }

    private void PropBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择目标 / Select target",
            Filter = "可执行文件与快捷方式 / Executables and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true && _selectedItem != null)
        {
            // Auto-resolve .lnk so the user gets the real exe + arguments
            // without having to chase the shortcut manually.
            var resolved = ShortcutResolver.Resolve(dialog.FileName);

            _isPopulatingProps = true;
            try
            {
                if (resolved != null)
                {
                    PropTargetPath.Text = resolved.TargetPath;
                    PropArguments.Text = resolved.Arguments ?? "";
                    PropWorkDir.Text = resolved.WorkingDirectory ?? "";

                    _selectedItem.UpdateTargetPath(resolved.TargetPath);
                    _selectedItem.UpdateArguments(resolved.Arguments);
                    _selectedItem.Item.WorkingDirectory = resolved.WorkingDirectory;

                    UpdateStatus($"快捷方式已解析 / Shortcut resolved → {resolved.TargetPath}");
                }
                else
                {
                    PropTargetPath.Text = dialog.FileName;
                    _selectedItem.UpdateTargetPath(dialog.FileName);
                }
            }
            finally
            {
                _isPopulatingProps = false;
            }

            _configService.SaveDebounced();
        }
    }

    /// <summary>
    /// Builds a ShortcutItem for a file the user picked. If the file is a
    /// .lnk shortcut, it is automatically resolved into the real target +
    /// arguments + working directory (a much friendlier default than
    /// storing the .lnk path itself).
    /// </summary>
    private static ShortcutItem CreateItemFromPath(string path)
    {
        string label = System.IO.Path.GetFileNameWithoutExtension(path);

        if (ShortcutResolver.IsShortcut(path))
        {
            var info = ShortcutResolver.Resolve(path);
            if (info != null)
            {
                return new ShortcutItem
                {
                    Label = label,
                    TargetPath = info.TargetPath,
                    Arguments = info.Arguments,
                    WorkingDirectory = info.WorkingDirectory
                };
            }
        }

        return new ShortcutItem
        {
            Label = label,
            TargetPath = path
        };
    }

    private void PropChanged(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingProps) return;
        if (_selectedItem == null) return;

        // Update only the field whose textbox actually changed. This prevents
        // an edit to one field from re-stamping the other three (which can
        // momentarily lag behind their backing model state).
        if (sender == PropLabel)
            _selectedItem.UpdateLabel(PropLabel.Text);
        else if (sender == PropTargetPath)
            _selectedItem.UpdateTargetPath(string.IsNullOrEmpty(PropTargetPath.Text) ? null : PropTargetPath.Text);
        else if (sender == PropArguments)
            _selectedItem.UpdateArguments(string.IsNullOrEmpty(PropArguments.Text) ? null : PropArguments.Text);
        else if (sender == PropWorkDir)
            _selectedItem.Item.WorkingDirectory = string.IsNullOrEmpty(PropWorkDir.Text) ? null : PropWorkDir.Text;

        _configService.SaveDebounced();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _configService.Save();
        UpdateStatus("Config saved");
    }

    #region Settings Handlers

    private void SetKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (SetKey.SelectedItem is not System.Windows.Input.Key key) return;
        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        _configService.Config.Settings.Hotkey.Key = (VirtualKey)vk;
        _configService.SaveDebounced();
    }

    private void SetModifier_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        HotkeyModifiers mods = HotkeyModifiers.None;
        if (SetCtrl.IsChecked == true) mods |= HotkeyModifiers.Control;
        if (SetAlt.IsChecked == true) mods |= HotkeyModifiers.Alt;
        if (SetShift.IsChecked == true) mods |= HotkeyModifiers.Shift;
        if (SetWin.IsChecked == true) mods |= HotkeyModifiers.Windows;
        _configService.Config.Settings.Hotkey.Modifiers = mods;
        _configService.SaveDebounced();
    }

    private void SetMouseButton_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        int idx = SetMouseButton.SelectedIndex;
        if (idx < 0 || idx >= MouseButtonValues.Length) return;
        _configService.Config.Settings.Hotkey.MouseButton = MouseButtonValues[idx];
        _configService.SaveDebounced();
    }

    private void SetMouseEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        SetMouseButton.IsEnabled = SetMouseEnabled.IsChecked == true;
        _configService.Config.Settings.Hotkey.MouseHotkeyEnabled = SetMouseEnabled.IsChecked == true;
        _configService.SaveDebounced();
    }

    private void SetHoldDelay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded)
        {
            // Still safe to update the label even before load completes,
            // but bail before touching the config object.
            if (SetHoldDelayLabel != null)
                SetHoldDelayLabel.Text = $"{(int)e.NewValue}ms";
            return;
        }
        int ms = (int)e.NewValue;
        SetHoldDelayLabel.Text = $"{ms}ms";
        _configService.Config.Settings.Hotkey.HoldDelayMs = ms;
        _configService.SaveDebounced();
    }

    private void SetWheelRadius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded)
        {
            if (SetWheelRadiusLabel != null)
                SetWheelRadiusLabel.Text = ((int)e.NewValue).ToString();
            return;
        }
        int val = (int)e.NewValue;
        SetWheelRadiusLabel.Text = val.ToString();
        _configService.Config.Settings.WheelRadius = val;
        _configService.SaveDebounced();
    }

    private void SetOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded)
        {
            if (SetOpacityLabel != null)
                SetOpacityLabel.Text = e.NewValue.ToString("F2");
            return;
        }
        SetOpacityLabel.Text = e.NewValue.ToString("F2");
        _configService.Config.Settings.Opacity = e.NewValue;
        _configService.SaveDebounced();
    }

    private void SetAccentColor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        _configService.Config.Settings.AccentColor = SetAccentColor.Text;
        _configService.SaveDebounced();
    }

    private void SetBgColor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        _configService.Config.Settings.BackgroundColor = SetBgColor.Text;
        _configService.SaveDebounced();
    }

    private void SetStartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        _configService.Config.Settings.StartWithWindows = SetStartWithWindows.IsChecked == true;
        SetStartupWithWindows(SetStartWithWindows.IsChecked == true);
        _configService.SaveDebounced();
    }

    private static void SetStartupWithWindows(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;

        if (enable)
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            key.SetValue("ShortcutWheel", $"\"{exePath}\" --minimized");
        }
        else
        {
            key.DeleteValue("ShortcutWheel", false);
        }
    }

    #endregion

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    #region Drag-and-Drop reordering

    private void ShortcutTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _pendingDragItem = GetItemFromMouse(e.OriginalSource);
    }

    private void ShortcutTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pendingDragItem == null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < DragStartThreshold &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < DragStartThreshold)
            return;

        var dragged = _pendingDragItem;
        _pendingDragItem = null;

        try
        {
            var data = new DataObject(ItemDataFormat, dragged);
            DragDrop.DoDragDrop(ShortcutTree, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
        }
    }

    private void ShortcutTree_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ItemDataFormat))
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
            e.Handled = true;
            return;
        }

        var dragged = e.Data.GetData(ItemDataFormat) as ShortcutItemViewModel;
        if (dragged == null)
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
            e.Handled = true;
            return;
        }

        var (targetItem, targetVm, position) = ComputeDropTarget(e);

        // Disallow dropping a folder onto itself or any of its descendants.
        if (targetVm == dragged ||
            (targetVm != null && IsDescendant(targetVm, dragged)))
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
            e.Handled = true;
            return;
        }

        if (targetItem != null)
        {
            ShowDropIndicator(targetItem, position);
        }
        else
        {
            ClearDropIndicator();
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ShortcutTree_DragLeave(object sender, DragEventArgs e)
    {
        // Only clear if we left the tree entirely (DragLeave fires on every
        // child boundary too).
        var pos = e.GetPosition(ShortcutTree);
        if (pos.X < 0 || pos.Y < 0 ||
            pos.X > ShortcutTree.ActualWidth ||
            pos.Y > ShortcutTree.ActualHeight)
        {
            ClearDropIndicator();
        }
    }

    private void ShortcutTree_Drop(object sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (!e.Data.GetDataPresent(ItemDataFormat))
        {
            e.Handled = true;
            return;
        }

        var dragged = e.Data.GetData(ItemDataFormat) as ShortcutItemViewModel;
        if (dragged == null) { e.Handled = true; return; }

        var (_, targetVm, position) = ComputeDropTarget(e);
        if (targetVm == dragged ||
            (targetVm != null && IsDescendant(targetVm, dragged)))
        {
            e.Handled = true;
            return;
        }

        try
        {
            PerformMove(dragged, targetVm, position);
            _configService.Save();
            UpdateStatus($"已移动 / Moved: {dragged.Label}");
        }
        catch (Exception ex)
        {
            App.LogError($"Drop failed: {ex}");
            UpdateStatus("移动失败 / Move failed (see error.log)");
        }

        e.Handled = true;
    }

    /// <summary>
    /// Resolves the TreeViewItem under the cursor and computes whether the
    /// drop should be Before/After/Into based on the cursor's vertical
    /// position relative to the item.
    /// </summary>
    private (TreeViewItem? container, ShortcutItemViewModel? vm, DropIndicatorAdorner.DropPosition position)
        ComputeDropTarget(DragEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        var tvi = FindAncestor<TreeViewItem>(element);
        if (tvi == null || tvi.DataContext is not ShortcutItemViewModel vm)
        {
            // Empty space → append to root via "After" on last root item.
            return (null, null, DropIndicatorAdorner.DropPosition.After);
        }

        var headerHost = (FrameworkElement?)tvi.Template?.FindName("PART_Header", tvi) ?? tvi;
        var pos = e.GetPosition(headerHost);
        double h = headerHost.ActualHeight;
        if (h <= 0) h = tvi.ActualHeight;

        DropIndicatorAdorner.DropPosition position;
        if (vm.IsFolder)
        {
            // Folders: top 25% = before, bottom 25% = after, middle = into
            if (pos.Y < h * 0.25) position = DropIndicatorAdorner.DropPosition.Before;
            else if (pos.Y > h * 0.75) position = DropIndicatorAdorner.DropPosition.After;
            else position = DropIndicatorAdorner.DropPosition.Into;
        }
        else
        {
            // Files: only Before/After, split at midpoint
            position = pos.Y < h * 0.5
                ? DropIndicatorAdorner.DropPosition.Before
                : DropIndicatorAdorner.DropPosition.After;
        }

        return (tvi, vm, position);
    }

    private void PerformMove(ShortcutItemViewModel dragged,
                             ShortcutItemViewModel? targetVm,
                             DropIndicatorAdorner.DropPosition position)
    {
        // Drop in empty space → append to root.
        if (targetVm == null)
        {
            DetachFromCurrentParent(dragged);
            _viewModels.Add(dragged);
            ShortcutTree.Items.Add(dragged);
            _configService.Config.RootItems.Add(dragged.Item);
            return;
        }

        if (position == DropIndicatorAdorner.DropPosition.Into)
        {
            // Move INTO the target folder.
            DetachFromCurrentParent(dragged);
            targetVm.AddChild(dragged.Item);
            // AddChild created a fresh VM around the same model. Replace it
            // so we keep the original VM identity (selection / state).
            // Cheaper alternative: remove the AddChild-spawned VM and add
            // the existing one directly.
            var spawned = targetVm.Children[^1];
            if (!ReferenceEquals(spawned, dragged))
            {
                targetVm.Children.RemoveAt(targetVm.Children.Count - 1);
                targetVm.Children.Add(dragged);
            }
            return;
        }

        // Before / After at the target's siblings level.
        var (targetParentVm, targetSiblingsVms, targetSiblingsItems) = GetSiblings(targetVm);
        int targetIdx = targetSiblingsVms.IndexOf(targetVm);
        if (targetIdx < 0) return;

        int insertIdx = position == DropIndicatorAdorner.DropPosition.After
            ? targetIdx + 1
            : targetIdx;

        var (sourceParentVm, sourceSiblingsVms, sourceSiblingsItems) = GetSiblings(dragged);
        int sourceIdx = sourceSiblingsVms.IndexOf(dragged);

        bool sameLevel = ReferenceEquals(sourceSiblingsVms, targetSiblingsVms);

        // Detach from source FIRST so index math for same-parent moves works.
        if (sourceIdx >= 0)
        {
            sourceSiblingsVms.RemoveAt(sourceIdx);
            sourceSiblingsItems.RemoveAt(sourceIdx);

            if (sameLevel && sourceIdx < insertIdx)
                insertIdx--;
        }

        targetSiblingsVms.Insert(insertIdx, dragged);
        targetSiblingsItems.Insert(insertIdx, dragged.Item);
    }

    private (ShortcutItemViewModel? parent,
             IList<ShortcutItemViewModel> siblingVms,
             IList<ShortcutItem> siblingItems)
        GetSiblings(ShortcutItemViewModel item)
    {
        // Root level?
        if (_viewModels.Contains(item))
        {
            return (null, _viewModels, _configService.Config.RootItems);
        }

        // Walk the tree.
        foreach (var root in _viewModels)
        {
            var found = FindParentVm(root, item);
            if (found != null)
            {
                return (found, found.Children, found.Item.Children);
            }
        }

        // Shouldn't happen – return root as a safe default.
        return (null, _viewModels, _configService.Config.RootItems);
    }

    private static ShortcutItemViewModel? FindParentVm(ShortcutItemViewModel current, ShortcutItemViewModel target)
    {
        if (current.Children.Contains(target)) return current;
        foreach (var child in current.Children)
        {
            var found = FindParentVm(child, target);
            if (found != null) return found;
        }
        return null;
    }

    private void DetachFromCurrentParent(ShortcutItemViewModel item)
    {
        var (parent, siblingVms, siblingItems) = GetSiblings(item);

        int idx = siblingVms.IndexOf(item);
        if (idx >= 0)
        {
            siblingVms.RemoveAt(idx);
            siblingItems.RemoveAt(idx);
        }

        // _viewModels and ShortcutTree.Items are kept in lock-step at root.
        if (parent == null)
        {
            ShortcutTree.Items.Remove(item);
        }
    }

    private static bool IsDescendant(ShortcutItemViewModel maybeDescendant, ShortcutItemViewModel ancestor)
    {
        // Returns true if maybeDescendant is anywhere in ancestor's subtree.
        if (ReferenceEquals(maybeDescendant, ancestor)) return true;
        foreach (var child in ancestor.Children)
        {
            if (IsDescendant(maybeDescendant, child)) return true;
        }
        return false;
    }

    private ShortcutItemViewModel? GetItemFromMouse(object originalSource)
    {
        var dep = originalSource as DependencyObject;
        var tvi = FindAncestor<TreeViewItem>(dep);
        return tvi?.DataContext as ShortcutItemViewModel;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void ShowDropIndicator(TreeViewItem target, DropIndicatorAdorner.DropPosition position)
    {
        if (ReferenceEquals(_currentIndicatorTarget, target) &&
            _currentIndicator != null &&
            _currentIndicator.IsLoaded)
        {
            // Same target — but the position might have changed. Cheap path:
            // remove + re-add only if the stored position differs.
            if (_currentIndicator is DropIndicatorAdorner existing &&
                existing.GetType() == typeof(DropIndicatorAdorner))
            {
                // We don't expose Position; just always replace to be safe.
            }
        }

        ClearDropIndicator();

        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer == null) return;

        _currentIndicator = new DropIndicatorAdorner(target, position);
        _currentIndicatorTarget = target;
        layer.Add(_currentIndicator);
    }

    private void ClearDropIndicator()
    {
        if (_currentIndicator != null && _currentIndicatorTarget != null)
        {
            var layer = AdornerLayer.GetAdornerLayer(_currentIndicatorTarget);
            layer?.Remove(_currentIndicator);
        }
        _currentIndicator = null;
        _currentIndicatorTarget = null;
    }

    #endregion
}
