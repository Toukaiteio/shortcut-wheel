using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ShortcutWheel.Models;

namespace ShortcutWheel.Services;

public class HotkeyService : IDisposable
{
    private readonly int _hotkeyId = 9001;
    private volatile IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private CancellationTokenSource? _holdCts;
    private readonly object _holdLock = new();
    private volatile HotkeyConfig? _config;

    // The low-level mouse hook is hosted on a dedicated STA thread with its
    // own message pump. WH_MOUSE_LL delivers every system mouse event to the
    // installing thread synchronously, so keeping it on the UI thread meant a
    // busy UI (wheel animation, rendering) stalled mouse input system-wide.
    private volatile Dispatcher? _mouseHookDispatcher;
    private volatile bool _hookStopRequested;

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

        NativeMethods.LowLevelMouseProc mouseProc = MouseHookCallback;
        _mouseProc = mouseProc; // keep the delegate rooted for the native callback
        _hookStopRequested = false;

        var thread = new Thread(() => MouseHookThreadMain(mouseProc))
        {
            IsBackground = true,
            Name = "ShortcutWheel-MouseHook"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void UnregisterMouseHotkey()
    {
        CancelPendingHold();
        _hookStopRequested = true;

        // Ask the hook thread to unhook itself, then tear down its pump. The
        // unhook runs ON the hook thread so there is never a window in which
        // the pump has stopped but the hook is still installed — that would
        // leave hook messages undeliverable and freeze system mouse input.
        // This never blocks the UI thread; the hook thread is a background
        // thread, so if the app is shutting down the process exits regardless
        // of whether the teardown finished.
        var dispatcher = _mouseHookDispatcher;
        _mouseHookDispatcher = null;
        if (dispatcher != null)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_hookId != IntPtr.Zero)
                    {
                        NativeMethods.UnhookWindowsHookEx(_hookId);
                        _hookId = IntPtr.Zero;
                    }
                }
                catch { }
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }));
        }
    }

    /// <summary>
    /// Entry point for the dedicated mouse-hook thread. Installs the
    /// WH_MOUSE_LL hook and runs a message pump. The system invokes the hook
    /// proc by posting a message to the installing thread, so this loop must
    /// stay alive for the hook to keep working.
    /// </summary>
    private void MouseHookThreadMain(NativeMethods.LowLevelMouseProc mouseProc)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        _mouseHookDispatcher = dispatcher;

        try
        {
            IntPtr moduleHandle = NativeMethods.GetModuleHandle(null!);
            _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, mouseProc, moduleHandle, 0);
            if (_hookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"SetWindowsHookEx failed. Error: {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetWindowsHookEx threw: {ex.Message}");
            _hookId = IntPtr.Zero;
        }

        if (_hookStopRequested)
        {
            // Unregistered before the pump ever ran — remove the hook and exit.
            try
            {
                if (_hookId != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }
            }
            catch { }
            return;
        }

        Dispatcher.Run();

        // Pump exited (shutdown requested) — clean up the hook from this thread.
        try
        {
            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
        catch { }
    }

    private void CancelPendingHold()
    {
        lock (_holdLock)
        {
            _holdCts?.Cancel();
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _config != null)
            {
                int msg = wParam.ToInt32();

                if (IsConfiguredMouseButtonEvent(_config.MouseButton, msg, lParam, isDown: true))
                {
                    CancellationTokenSource cts;
                    lock (_holdLock)
                    {
                        _holdCts?.Cancel();
                        cts = new CancellationTokenSource();
                        _holdCts = cts;
                    }

                    _ = NotifyAfterHoldAsync(_config.HoldDelayMs, cts);
                }
                else if (IsConfiguredMouseButtonEvent(_config.MouseButton, msg, lParam, isDown: false))
                {
                    lock (_holdLock)
                    {
                        _holdCts?.Cancel();
                    }

                    // The callback runs on the dedicated hook thread, so any UI
                    // marshal must tolerate the app shutting down (Application
                    // may already be null / its dispatcher ceasing to accept work).
                    if (Application.Current != null)
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            WheelDismissed?.Invoke(this, EventArgs.Empty)));
                }
            }
        }
        catch
        {
            // Never let a hook callback exception propagate into the native
            // hook invocation and kill the dedicated hook thread.
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private async Task NotifyAfterHoldAsync(int delayMs, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delayMs, cts.Token);
            if (!cts.Token.IsCancellationRequested)
            {
                // Tolerate the app shutting down (Application may already be
                // null / its dispatcher ceasing to accept work).
                if (Application.Current?.Dispatcher is { } dispatcher)
                    _ = dispatcher.BeginInvoke(new Action(() =>
                        WheelVisibilityChanged?.Invoke(true)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_holdLock)
            {
                if (ReferenceEquals(_holdCts, cts))
                    _holdCts = null;
            }
            cts.Dispose();
        }
    }

    private static bool IsConfiguredMouseButtonEvent(MouseButton button, int msg, IntPtr lParam, bool isDown)
    {
        int expectedMsg = isDown ? GetMouseButtonDownMsg(button) : GetMouseButtonUpMsg(button);
        if (msg != expectedMsg) return false;

        if (button is not (MouseButton.XButton1 or MouseButton.XButton2))
            return true;

        var hook = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        int xButton = (int)((hook.mouseData >> 16) & 0xffff);
        return button == MouseButton.XButton1
            ? xButton == NativeMethods.XBUTTON1
            : xButton == NativeMethods.XBUTTON2;
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
        lock (_holdLock)
        {
            _holdCts?.Cancel();
        }
    }
}
