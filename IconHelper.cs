using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Loadout;

/// <summary>Pulls the real Windows icon out of an exe so the card shows its actual artwork.</summary>
public static class IconHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    // Cache extracted icons by path so we don't re-hit the shell on every popup refresh.
    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (_cache.TryGetValue(exePath, out var cached)) return cached;

        var icon = Extract(exePath);
        _cache[exePath] = icon;   // cache misses too (null) so we don't retry a bad path every refresh
        return icon;
    }

    private static ImageSource? Extract(string exePath)
    {
        try
        {
            if (!File.Exists(exePath))
                return null;

            var info = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(exePath, 0, ref info, (uint)Marshal.SizeOf(info),
                SHGFI_ICON | SHGFI_LARGEICON);
            if (info.hIcon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
