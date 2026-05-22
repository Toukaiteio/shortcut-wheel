using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShortcutWheel.Models;

namespace ShortcutWheel.Services;

public class ConfigService
{
    private readonly string _configDir;
    private readonly string _configFilePath;
    private CancellationTokenSource? _debounceCts;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ShortcutConfig Config { get; private set; } = new();
    public string ConfigFilePath => _configFilePath;

    public event EventHandler? ConfigChanged;

    public ConfigService()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string portableConfig = Path.Combine(exeDir, "Config", "shortcuts.json");

        if (File.Exists(portableConfig))
        {
            _configDir = Path.GetDirectoryName(portableConfig)!;
            _configFilePath = portableConfig;
        }
        else
        {
            _configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShortcutWheel");
            _configFilePath = Path.Combine(_configDir, "shortcuts.json");
        }
    }

    public ShortcutConfig Load()
    {
        if (!File.Exists(_configFilePath))
        {
            Directory.CreateDirectory(_configDir);
            Config = CreateDefaultConfig();
            Save();
            return Config;
        }

        try
        {
            string json = File.ReadAllText(_configFilePath);
            Config = JsonSerializer.Deserialize<ShortcutConfig>(json, JsonOptions) ?? CreateDefaultConfig();
            ValidateConfig();
        }
        catch (JsonException)
        {
            string backupPath = _configFilePath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(_configFilePath, backupPath);
            Config = CreateDefaultConfig();
            Save();
        }

        return Config;
    }

    public void Save()
    {
        string tmpPath = _configFilePath + ".tmp";
        string json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(tmpPath, json);

        if (File.Exists(_configFilePath))
            File.Replace(tmpPath, _configFilePath, null);
        else
            File.Move(tmpPath, _configFilePath);
    }

    public void SaveDebounced()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Delay(500, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
            {
                Save();
                ConfigChanged?.Invoke(this, EventArgs.Empty);
            }
        }, token);
    }

    /// <summary>
    /// Scans all items for .lnk TargetPaths and resolves them to the real
    /// executable path in the background. Saves once if any items changed.
    /// Safe to call fire-and-forget from the UI thread.
    /// </summary>
    public async Task MigrateLnkPathsAsync()
    {
        bool changed = await MigrateItemsAsync(Config.RootItems);
        if (changed) Save();
    }

    private static async Task<bool> MigrateItemsAsync(IList<ShortcutItem> items)
    {
        bool changed = false;
        foreach (var item in items)
        {
            if (ShortcutResolver.IsShortcut(item.TargetPath))
            {
                var info = await ShortcutResolver.ResolveAsync(item.TargetPath!);
                if (info != null && !string.IsNullOrEmpty(info.TargetPath))
                {
                    item.TargetPath = info.TargetPath;
                    if (item.Arguments == null && info.Arguments != null)
                        item.Arguments = info.Arguments;
                    if (item.WorkingDirectory == null && info.WorkingDirectory != null)
                        item.WorkingDirectory = info.WorkingDirectory;
                    changed = true;
                }
            }
            if (item.Children?.Count > 0)
                changed |= await MigrateItemsAsync(item.Children);
        }
        return changed;
    }

    private ShortcutConfig CreateDefaultConfig()
    {
        var config = new ShortcutConfig();

        string defaultJson = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "default-shortcuts.json");
        if (File.Exists(defaultJson))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<ShortcutItem>>(File.ReadAllText(defaultJson), JsonOptions);
                if (items != null)
                    config.RootItems = items;
            }
            catch { }
        }

        return config;
    }

    private void ValidateConfig()
    {
        Config.Settings ??= new AppSettings();
        Config.Settings.Hotkey ??= new HotkeyConfig();
        Config.RootItems ??= new List<ShortcutItem>();
        EnsureIds(Config.RootItems);
        DeduplicateById(Config.RootItems);

        MigrateSchema();
    }

    /// <summary>
    /// Removes duplicate entries (matched by Id) anywhere in the tree,
    /// keeping only the first occurrence. Repairs configs corrupted by
    /// an earlier drag-and-drop bug that left the same item referenced
    /// in two parents.
    /// </summary>
    private static void DeduplicateById(IList<ShortcutItem> roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(roots);

        void Walk(IList<ShortcutItem> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var id = list[i].Id;
                if (string.IsNullOrEmpty(id)) continue;
                if (!seen.Add(id))
                {
                    list.RemoveAt(i);
                }
            }
            // Walk surviving children.
            foreach (var item in list)
            {
                if (item.Children != null)
                    Walk(item.Children);
            }
        }
    }

    /// <summary>
    /// Migrates legacy schemas (v1 = small blue wheel) to the current
    /// CSGO-style defaults (v2). Only overwrites values that were left at
    /// the legacy defaults so user-customised settings are preserved.
    /// </summary>
    private void MigrateSchema()
    {
        if (Config.SchemaVersion < 2)
        {
            var s = Config.Settings;

            // v1 default was 200; saved configs in the wild often have 180.
            // Anything below 240 looked too small per CSGO reference; bump it.
            if (s.WheelRadius < 240) s.WheelRadius = 280;
            if (s.CenterCircleRadius < 60) s.CenterCircleRadius = 70;

            // Replace legacy blue palette with the new CSGO palette.
            if (string.Equals(s.AccentColor, "#1E9CD9", StringComparison.OrdinalIgnoreCase))
                s.AccentColor = "#1F1B17";
            if (string.Equals(s.HoverColor, "#2FB8FF", StringComparison.OrdinalIgnoreCase))
                s.HoverColor = "#7A6A48";
            if (string.Equals(s.BackgroundColor, "#CC1A1A2E", StringComparison.OrdinalIgnoreCase))
                s.BackgroundColor = "#E0141312";

            if (string.IsNullOrWhiteSpace(s.TrimColor))
                s.TrimColor = "#C9A24E";

            Config.SchemaVersion = 2;
        }

        if (Config.SchemaVersion < 3)
        {
            // v2 had a ConfigWindow data-corruption bug that blanked
            // TargetPath/Arguments/WorkingDirectory on items the user
            // selected in the Properties panel. Two repairs:
            //   1. Restore known default-shortcut targets by id.
            //   2. Normalise empty strings to null so IsFolder works
            //      consistently and the JSON stays clean.
            RepairBlankedTargets(Config.RootItems);
            Config.SchemaVersion = 3;
        }

        try { Save(); } catch { /* best-effort */ }
    }

    private static readonly Dictionary<string, string> KnownDefaultTargets = new(StringComparer.Ordinal)
    {
        { "root-calc",     "calc.exe" },
        { "root-notepad",  "notepad.exe" },
        { "root-cmd",      "cmd.exe" },
        { "root-explorer", "explorer.exe" },
        { "dev-code",      "code" },
        { "dev-settings",  "ms-settings:" },
    };

    private static void RepairBlankedTargets(List<ShortcutItem> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.TargetPath))
            {
                if (KnownDefaultTargets.TryGetValue(item.Id, out var restored))
                    item.TargetPath = restored;
                else
                    item.TargetPath = null;
            }

            if (item.Arguments == "") item.Arguments = null;
            if (item.WorkingDirectory == "") item.WorkingDirectory = null;

            if (item.Children != null && item.Children.Count > 0)
            {
                RepairBlankedTargets(item.Children.ToList());
            }
        }
    }

    private void EnsureIds(List<ShortcutItem> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Id))
                item.Id = Guid.NewGuid().ToString("N");
            if (item.Children == null)
                item.Children = new System.Collections.ObjectModel.ObservableCollection<ShortcutItem>();
            EnsureIds(item.Children.ToList());
        }
    }
}
