using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ShortcutWheel.Services;

public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> IconCache = new();

    public static BitmapSource? ExtractIcon(string filePath, int? iconIndex = null, int size = 32)
    {
        string cacheKey = $"{filePath}:{iconIndex ?? -1}:{size}";

        return IconCache.GetOrAdd(cacheKey, _ => TryGetIcon(filePath, size) ?? GetDefaultIcon());
    }

    private static BitmapSource? TryGetIcon(string filePath, int size)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            var shfi = new NativeMethods.SHFILEINFO();
            uint flags = NativeMethods.SHGFI_ICON |
                         (size > 32 ? NativeMethods.SHGFI_LARGEICON : NativeMethods.SHGFI_SMALLICON) |
                         NativeMethods.SHGFI_USEFILEATTRIBUTES;

            IntPtr result = NativeMethods.SHGetFileInfo(
                filePath,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref shfi,
                (uint)Marshal.SizeOf(shfi),
                flags);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var icon = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                return icon;
            }
            finally
            {
                NativeMethods.DestroyIcon(shfi.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? GetDefaultIcon()
    {
        // Fall back to a generic file icon. We pretend a non-existing path
        // is a normal file (USEFILEATTRIBUTES) so the shell never touches disk.
        try
        {
            var shfi = new NativeMethods.SHFILEINFO();
            uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON |
                         NativeMethods.SHGFI_USEFILEATTRIBUTES;

            IntPtr result = NativeMethods.SHGetFileInfo(
                "file.txt",
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref shfi,
                (uint)Marshal.SizeOf(shfi),
                flags);

            if (result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var icon = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    icon.Freeze();
                    return icon;
                }
                finally
                {
                    NativeMethods.DestroyIcon(shfi.hIcon);
                }
            }
        }
        catch
        {
            // ignore
        }

        // Absolute last resort: a transparent placeholder.
        var blank = BitmapSource.Create(
            32, 32, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32,
            null, new byte[32 * 32 * 4], 32 * 4);
        blank.Freeze();
        return blank;
    }
}
