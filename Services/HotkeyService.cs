using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ShortcutWheel.Models;

namespace ShortcutWheel.Services;

public class HotkeyService : IDisposable
{
    private readonly int _hotkeyId = 9001;
    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private CancellationTokenSource? _holdCts;
    private HotkeyConfig? _config;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? WheelDismissed;
    public event Action<bool>? WheelVisibilityChanged;

    public IntPtr WindowHandle { get; private set; }

    public void SetWindowHandle(IntPtr hWnd)
    {
        WindowHandle = hWnd;
    }

    public bool RegisterKeyboardHotkey(HotkeyConfig config)
    {
        _config = config;
        UnregisterKeyboardHotkey();

        uint vk = (uint)config.Key;
        bool result = NativeMethods.RegisterHotKey(WindowHandle, _hotkeyId, config.Modifiers, vk);

        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"RegisterHotKey failed. Error: {error}");
        }

        return result;
    }

    public void UnregisterKeyboardHotkey()
    {
        if (WindowHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(WindowHandle, _hotkeyId);
        }
    }

    public void RegisterMouseHotkey(MouseButton button)
    {
        UnregisterMouseHotkey();

        _mouseProc = MouseHookCallback;
        IntPtr moduleHandle = NativeMethods.GetModuleHandle(null!);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"SetWindowsHookEx failed. Error: {error}");
        }
    }

    public void UnregisterMouseHotkey()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _config != null)
        {
            int msg = wParam.ToInt32();
            int mouseButtonMsg = GetMouseButtonDownMsg(_config.MouseButton);

            if (msg == mouseButtonMsg)
            {
                _holdCts?.Cancel();
                _holdCts = new CancellationTokenSource();
                var token = _holdCts.Token;

                Task.Delay(_config.HoldDelayMs, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && !token.IsCancellationRequested)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            WheelVisibilityChanged?.Invoke(true);
                        });
                    }
                }, token);
            }
            else if (msg == GetMouseButtonUpMsg(_config.MouseButton))
            {
                _holdCts?.Cancel();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    WheelDismissed?.Invoke(this, EventArgs.Empty);
                });
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static int GetMouseButtonDownMsg(MouseButton button) => button switch
    {
        MouseButton.Left => NativeMethods.WM_LBUTTONDOWN,
        MouseButton.Middle => NativeMethods.WM_MBUTTONDOWN,
        MouseButton.XButton1 or MouseButton.XButton2 => NativeMethods.WM_XBUTTONDOWN,
        _ => NativeMethods.WM_LBUTTONDOWN,
    };

    private static int GetMouseButtonUpMsg(MouseButton button) => button switch
    {
        MouseButton.Left => NativeMethods.WM_LBUTTONUP,
        MouseButton.Middle => NativeMethods.WM_MBUTTONUP,
        MouseButton.XButton1 or MouseButton.XButton2 => NativeMethods.WM_XBUTTONUP,
        _ => NativeMethods.WM_LBUTTONUP,
    };

    public void OnHotkeyReceived()
    {
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        UnregisterKeyboardHotkey();
        UnregisterMouseHotkey();
        _holdCts?.Cancel();
        _holdCts?.Dispose();
    }
}
