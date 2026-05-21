using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using ShortcutWheel.Models;
using ShortcutWheel.Services;

namespace ShortcutWheel.ViewModels;

public class ShortcutItemViewModel : INotifyPropertyChanged
{
    private readonly ShortcutItem _item;

    public ShortcutItem Item => _item;
    public string Id => _item.Id;
    public string Label => _item.Label;
    public string? TargetPath => _item.TargetPath;
    public string? Arguments => _item.Arguments;

    /// <summary>
    /// True for any item without a launch target. Reflects the underlying
    /// model so a freshly-created empty folder is still a folder.
    /// </summary>
    public bool IsFolder => _item.IsFolder;

    public ObservableCollection<ShortcutItemViewModel> Children { get; }

    public BitmapSource? Icon =>
        !string.IsNullOrEmpty(_item.TargetPath) ? IconExtractor.ExtractIcon(_item.TargetPath) : null;

    public ShortcutItemViewModel(ShortcutItem item)
    {
        _item = item;
        Children = new ObservableCollection<ShortcutItemViewModel>(
            item.Children.Select(c => new ShortcutItemViewModel(c)));
    }

    /// <summary>
    /// Adds a child to BOTH the underlying model and the view-model
    /// collection so the TreeView updates immediately and the change is
    /// persisted on the next save.
    /// </summary>
    public ShortcutItemViewModel AddChild(ShortcutItem child)
    {
        _item.Children.Add(child);
        var vm = new ShortcutItemViewModel(child);
        Children.Add(vm);

        // IsFolder may have flipped (was determined by TargetPath, but the
        // surrounding UI also gates on "has children" in some places).
        OnPropertyChanged(nameof(IsFolder));
        return vm;
    }

    public void RemoveChild(ShortcutItemViewModel childVm)
    {
        _item.Children.Remove(childVm.Item);
        Children.Remove(childVm);
        OnPropertyChanged(nameof(IsFolder));
    }

    public void UpdateLabel(string label)
    {
        _item.Label = label;
        OnPropertyChanged(nameof(Label));
    }

    public void UpdateTargetPath(string? path)
    {
        _item.TargetPath = path;
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(Icon));
        // Adding/removing a TargetPath flips file <-> folder semantics.
        OnPropertyChanged(nameof(IsFolder));
    }

    public void UpdateArguments(string? args)
    {
        _item.Arguments = args;
        OnPropertyChanged(nameof(Arguments));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
