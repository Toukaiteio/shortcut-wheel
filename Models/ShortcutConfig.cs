namespace ShortcutWheel.Models;

public class ShortcutConfig
{
    public List<ShortcutItem> RootItems { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public int SchemaVersion { get; set; } = 1;
}
