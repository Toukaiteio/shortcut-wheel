namespace ShortcutWheel.Models;

public class AppSettings
{
    public HotkeyConfig Hotkey { get; set; } = new();

    // CSGO-style: a noticeably bigger wheel so labels/icons are clearly readable.
    public int WheelRadius { get; set; } = 280;
    public int CenterCircleRadius { get; set; } = 70;

    public double Opacity { get; set; } = 0.95;

    // Dark, near-black wedge fill with a faint warm tint (CSGO look).
    public string AccentColor { get; set; } = "#1F1B17";
    // Hover: warm, slightly desaturated gold tone over the dark wedge.
    public string HoverColor { get; set; } = "#7A6A48";
    // Outer wheel/ring background.
    public string BackgroundColor { get; set; } = "#E0141312";
    // Gold trim used for borders, numerals, and the centre disc.
    public string TrimColor { get; set; } = "#C9A24E";

    public bool ShowTooltips { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool RunMinimized { get; set; } = true;
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool SilentUpdate { get; set; } = false;

    public string? BackgroundImagePath { get; set; }
    public double BackgroundImageOpacity { get; set; } = 0.4;
}
