using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ShortcutWheel.Services;

/// <summary>
/// Resolves Windows .lnk shortcut files into their actual launch parameters
/// (target path, arguments, working directory, icon location). Wraps the
/// IShellLinkW COM interface.
/// </summary>
public static class ShortcutResolver
{
    public class ShortcutInfo
    {
        public string TargetPath { get; init; } = "";
        public string? Arguments { get; init; }
        public string? WorkingDirectory { get; init; }
        public string? IconLocation { get; init; }
        public int IconIndex { get; init; }
    }

    /// <summary>
    /// True if <paramref name="path"/> ends in .lnk (case-insensitive).
    /// </summary>
    public static bool IsShortcut(string? path) =>
        !string.IsNullOrEmpty(path) &&
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the contents of a .lnk file and returns its launch parameters.
    /// Returns null if the file doesn't exist, isn't a shortcut, or cannot
    /// be opened.
    /// </summary>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (!IsShortcut(shortcutPath) || !File.Exists(shortcutPath))
            return null;

        try
        {
            var link = new ShellLink();
            var persistFile = (IPersistFile)link;
            persistFile.Load(shortcutPath, 0);

            var slw = (IShellLinkW)link;

            var sbPath = new StringBuilder(MAX_PATH);
            slw.GetPath(sbPath, sbPath.Capacity, out _, 2 /* SLGP_RAWPATH */);
            string target = sbPath.ToString();
            if (string.IsNullOrEmpty(target))
                return null;

            var sbArgs = new StringBuilder(MAX_PATH);
            slw.GetArguments(sbArgs, sbArgs.Capacity);

            var sbDir = new StringBuilder(MAX_PATH);
            slw.GetWorkingDirectory(sbDir, sbDir.Capacity);

            var sbIcon = new StringBuilder(MAX_PATH);
            slw.GetIconLocation(sbIcon, sbIcon.Capacity, out int iconIndex);

            return new ShortcutInfo
            {
                TargetPath = target,
                Arguments = NullIfEmpty(sbArgs.ToString()),
                WorkingDirectory = NullIfEmpty(sbDir.ToString()),
                IconLocation = NullIfEmpty(sbIcon.ToString()),
                IconIndex = iconIndex
            };
        }
        catch
        {
            return null;
        }
    }

    private const int MAX_PATH = 260;
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    }
}
