namespace ShortcutWheel.Models;

public class HotkeyConfig
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Alt | HotkeyModifiers.Control;
    public VirtualKey Key { get; set; } = VirtualKey.Space;
    public bool MouseHotkeyEnabled { get; set; } = true;
    public MouseButton MouseButton { get; set; } = MouseButton.XButton1;
    public int HoldDelayMs { get; set; } = 200;
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public enum VirtualKey
{
    Space = 0x20,
    Tab = 0x09,
    Enter = 0x0D,
    Escape = 0x1B,
    Back = 0x08,
    Delete = 0x2E,
    Home = 0x24,
    End = 0x23,
    Prior = 0x21,
    Next = 0x22,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47,
    H = 0x48, I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E,
    O = 0x4F, P = 0x50, Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55,
    V = 0x56, W = 0x57, X = 0x58, Y = 0x59, Z = 0x5A,
    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
    F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
    Oem3 = 0xC0, // ~ key
    OemComma = 0xBC,
    OemPeriod = 0xBE,
    OemMinus = 0xBD,
    OemPlus = 0xBB,
}

public enum MouseButton
{
    Left,
    Middle,
    XButton1,
    XButton2,
}
