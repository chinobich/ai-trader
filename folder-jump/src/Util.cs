using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FolderJump
{
    /// <summary>設定・履歴・インデックスの保存先。</summary>
    internal static class AppPaths
    {
        static string dataDir;

        public static string DataDir
        {
            get
            {
                if (dataDir == null)
                {
                    string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
                    dataDir = Path.Combine(baseDir, "FolderJump");
                }
                return dataDir;
            }
            set { dataDir = value; }
        }

        public static string SettingsFile { get { return Path.Combine(DataDir, "FolderJump.ini"); } }
        public static string IndexFile { get { return Path.Combine(DataDir, "index.bin"); } }
        public static string HistoryFile { get { return Path.Combine(DataDir, "history.tsv"); } }
        public static string PinsFile { get { return Path.Combine(DataDir, "pins.tsv"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "log.txt"); } }

        public static void Ensure()
        {
            Directory.CreateDirectory(DataDir);
        }
    }

    internal static class Log
    {
        static readonly object gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    AppPaths.Ensure();
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(AppPaths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // ログが書けなくても本体の動作は止めない
            }
        }

        public static void Error(string where, Exception ex)
        {
            Write("[" + where + "] " + ex);
        }
    }

    internal static class PathUtil
    {
        public static readonly char Sep = Path.DirectorySeparatorChar;

        /// <summary>末尾の区切り文字を取り除く (ドライブ直下 "Z:\" はそのまま)。</summary>
        public static string Trim(string p)
        {
            if (string.IsNullOrEmpty(p)) return p ?? "";
            int end = p.Length;
            while (end > 1 && (p[end - 1] == '\\' || p[end - 1] == '/'))
            {
                if (end == 3 && p[1] == ':') break;
                end--;
            }
            string r = p.Substring(0, end);
            if (r.Length == 2 && r[1] == ':') r += "\\";
            return r;
        }

        public static string Join(string dir, string name)
        {
            if (dir.Length > 0 && (dir[dir.Length - 1] == '\\' || dir[dir.Length - 1] == '/')) return dir + name;
            return dir + Sep + name;
        }

        public static string LastSegment(string p)
        {
            p = Trim(p);
            int i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            if (i < 0 || i == p.Length - 1) return p;
            return p.Substring(i + 1);
        }

        public static string Parent(string p)
        {
            p = Trim(p);
            int i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            if (i <= 0 || i == p.Length - 1) return null;
            if (p.StartsWith(@"\\") && p.IndexOf('\\', 2) == i) return null; // \\server だけは親にしない
            return Trim(p.Substring(0, i + 1));
        }

        /// <summary>path が root の中 (または root そのもの) か。</summary>
        public static bool IsUnder(string path, string root)
        {
            path = Trim(path);
            root = Trim(root);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (path.Length == root.Length) return true;
            char last = root[root.Length - 1];
            if (last == '\\' || last == '/') return true;
            char c = path[root.Length];
            return c == '\\' || c == '/';
        }

        /// <summary>260 文字を超えるパスでも扱えるように \\?\ を付ける。</summary>
        public static string Extended(string p)
        {
            if (!Native.IsWindows) return p;
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        /// <summary>見た目用: \\server\share\営業 → server › share › 営業</summary>
        public static string Pretty(string p, int maxChars)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.TrimStart('\\');
            string[] parts = s.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return p;
            var sb = new StringBuilder();
            int start = 0;
            int total = 0;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                total += parts[i].Length + 3;
                if (total > maxChars && i < parts.Length - 1) { start = i + 1; break; }
            }
            if (start > 0) sb.Append("…  ›  ");
            for (int i = start; i < parts.Length; i++)
            {
                if (i > start) sb.Append("  ›  ");
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }
    }

    internal static class NameOrder
    {
        static bool logicalAvailable = Native.IsWindows;

        /// <summary>エクスプローラーと同じ並び (フォルダ2 → フォルダ10 の順)。</summary>
        public static int Compare(string a, string b)
        {
            if (logicalAvailable)
            {
                try { return Native.StrCmpLogicalW(a, b); }
                catch (Exception) { logicalAvailable = false; }
            }
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    internal static class TextFile
    {
        /// <summary>UTF-8 で読み、壊れていたらシステムの既定 (Shift_JIS 等) で読み直す。</summary>
        public static string[] ReadLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            try
            {
                int offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Default.GetString(bytes);
            }
            return text.Replace("\r\n", "\n").Split('\n');
        }

        public static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
