using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace ShortcutWheel.Models;

public class ShortcutItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string? TargetPath { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? IconPath { get; set; }
    public int? IconIndex { get; set; }
    public bool RunAsAdmin { get; set; } = false;

    [JsonConverter(typeof(ObservableCollectionConverter<ShortcutItem>))]
    public ObservableCollection<ShortcutItem> Children { get; set; } = new();

    /// <summary>
    /// An item is a folder when it has no launch target. Folders may be
    /// empty (the user just created them) or contain children. Items with
    /// a TargetPath are launchable shortcuts.
    /// </summary>
    [JsonIgnore]
    public bool IsFolder => string.IsNullOrEmpty(TargetPath);

    public string? ColorOverride { get; set; }
}
