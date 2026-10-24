using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderJump
{
    /// <summary>
    /// エクスプローラーと同じアイコンを拡張子ごとに取得してキャッシュする。
    /// (USEFILEATTRIBUTES なのでネットワークにはアクセスしない)
    /// </summary>
    internal static class ShellIcons
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Get(string name, bool isDir)
        {
            string ext = isDir ? "<dir>" : SafeExtension(name);
            ImageSource img;
            if (cache.TryGetValue(ext, out img)) return img;
            img = Load(isDir ? "folder" : "file" + ext, isDir);
            cache[ext] = img;
            return img;
        }

        static string SafeExtension(string name)
        {
            try { return Path.GetExtension(name) ?? ""; }
            catch (ArgumentException) { return ""; }
        }

        static ImageSource Load(string fakeName, bool isDir)
        {
            var info = new Native.SHFILEINFO();
            try
            {
                uint attr = isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
                IntPtr r = Native.SHGetFileInfo(fakeName, attr, ref info, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)),
                    Native.SHGFI_ICON | Native.SHGFI_LARGEICON | Native.SHGFI_USEFILEATTRIBUTES);
                if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (info.hIcon != IntPtr.Zero) Native.DestroyIcon(info.hIcon);
            }
        }
    }
}
