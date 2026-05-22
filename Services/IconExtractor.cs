using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ShortcutWheel.Services;

/// <summary>
/// Thread-safe icon cache. All SHGetFileInfo / HICON work runs on a
/// dedicated STA thread so it never blocks the WPF render thread and
/// never dead-locks via Dispatcher.Invoke.
/// </summary>
public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> _cache = new();
    private static readonly ConcurrentDictionary<string, bool> _pending = new();

    // Single long-lived STA worker thread for all shell icon work.
    private static readonly BlockingCollection<Action> _queue = new();

    static IconExtractor()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch { /* never crash the worker */ }
            }
        })
        {
            IsBackground = true,
            Name = "IconExtractor-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Returns a cached icon immediately (null if not yet loaded).
    /// Schedules a background load on first call; calls <paramref name="onLoaded"/>
    /// on the UI thread when the icon is ready. Safe to call from OnRender.
    /// </summary>
    public static BitmapSource? GetCached(string filePath, Action? onLoaded = null)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        if (_cache.TryGetValue(filePath, out var cached))
            return cached;

        if (_pending.TryAdd(filePath, true))
        {
            _queue.Add(() =>
            {
                var icon = LoadOnStaThread(filePath);
                _cache[filePath] = icon;
                _pending.TryRemove(filePath, out _);
                if (onLoaded != null)
                    Application.Current?.Dispatcher.BeginInvoke(onLoaded);
            });
        }

        return null;
    }

    /// <summary>Pre-warms the cache for a list of paths (fire-and-forget).</summary>
    public static void Prefetch(IEnumerable<string?> paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrEmpty(p))
                GetCached(p);
        }
    }

    // ── STA worker ──────────────────────────────────────────────────────────

    private static BitmapSource? LoadOnStaThread(string filePath)
    {
        var bmp = TryGetIcon(filePath);
        return bmp ?? GetDefaultIcon();
    }

    private static BitmapSource? TryGetIcon(string filePath)
    {
        try
        {
            // Step 1: get the system icon index for this file via SHGetFileInfo.
            // SHGFI_SYSICONINDEX returns the index into the system image list
            // without extracting an HICON, so it works for any path including
            // ones that don't exist on disk (USEFILEATTRIBUTES).
            var shfi = new NativeMethods.SHFILEINFO();
            uint flags = NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_USEFILEATTRIBUTES;

            IntPtr result = NativeMethods.SHGetFileInfo(
                filePath,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref shfi,
                (uint)Marshal.SizeOf(shfi),
                flags);

            if (result == IntPtr.Zero) return null;
            int iconIndex = shfi.iIcon;

            // Step 2: retrieve the JUMBO (256×256) image list and extract the icon.
            var iid = NativeMethods.IID_IImageList;
            int hr = NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref iid, out var imageList);
            if (hr != 0 || imageList == null) return null;

            hr = imageList.GetIcon(iconIndex, 0x00000001 /* ILD_TRANSPARENT */, out IntPtr hIcon);
            if (hr != 0 || hIcon == IntPtr.Zero) return null;

            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? GetDefaultIcon()
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFO();
            uint flags = NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_USEFILEATTRIBUTES;

            IntPtr result = NativeMethods.SHGetFileInfo(
                "file.exe",
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref shfi,
                (uint)Marshal.SizeOf(shfi),
                flags);

            if (result != IntPtr.Zero)
            {
                var iid = NativeMethods.IID_IImageList;
                int hr = NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref iid, out var imageList);
                if (hr == 0 && imageList != null)
                {
                    hr = imageList.GetIcon(shfi.iIcon, 0x00000001, out IntPtr hIcon);
                    if (hr == 0 && hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bmp.Freeze();
                            return bmp;
                        }
                        finally { NativeMethods.DestroyIcon(hIcon); }
                    }
                }
            }
        }
        catch { }

        var blank = BitmapSource.Create(32, 32, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32,
            null, new byte[32 * 32 * 4], 32 * 4);
        blank.Freeze();
        return blank;
    }
}
