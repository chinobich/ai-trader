using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FolderJump
{
    /// <summary>FolderJump.ini (メモ帳で編集できる簡単な設定ファイル)。</summary>
    internal sealed class Settings
    {
        public string Hotkey = "Ctrl+Alt+Space";
        public bool IncludeFiles = true;
        public bool SkipHidden = true;
        public bool FollowLinks = false;
        public double RescanHours = 12;
        public int ScanThreads = 8;
        public int MaxEntries = 3000000;
        public bool ReuseExplorer = true;
        public bool LearnFromExplorer = true;
        public List<string> Roots = new List<string>();
        public List<string> Exclude = new List<string>();

        public static readonly string[] DefaultExclude =
        {
            "$RECYCLE.BIN", "System Volume Information", "~snapshot", ".snapshot", "#recycle", "@Recycle", "@eaDir",
            "Thumbs.db", "desktop.ini", ".DS_Store", "~$*", "*.tmp"
        };

        /// <summary>スキャン結果に影響する設定の要約。変わったら再スキャンが必要。</summary>
        public string ScanSignature
        {
            get
            {
                var sb = new StringBuilder();
                foreach (string r in Roots) sb.Append(r.ToLowerInvariant()).Append('|');
                sb.Append(IncludeFiles).Append(SkipHidden).Append(FollowLinks).Append(MaxEntries).Append('|');
                foreach (string e in Exclude) sb.Append(e.ToLowerInvariant()).Append('|');
                return sb.ToString();
            }
        }

        public Settings Clone()
        {
            var s = (Settings)MemberwiseClone();
            s.Roots = new List<string>(Roots);
            s.Exclude = new List<string>(Exclude);
            return s;
        }

        public bool HasRoot(string path)
        {
            string p = PathUtil.Trim(path);
            foreach (string r in Roots)
                if (string.Equals(r, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>登録済みのどれかの検索対象の中にあるか。</summary>
        public bool IsCovered(string path)
        {
            foreach (string r in Roots)
                if (PathUtil.IsUnder(path, r)) return true;
            return false;
        }

        public static string NormalizeRoot(string raw)
        {
            string s = (raw ?? "").Trim().Trim('"', '\'').Trim();
            if (s.Length == 0) return "";
            if (Native.IsWindows) s = s.Replace('/', '\\');
            return PathUtil.Trim(s);
        }

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!System.IO.File.Exists(path))
            {
                s.Exclude.AddRange(DefaultExclude);
                return s;
            }
            string section = "";
            bool sawExclude = false;
            foreach (string raw in TextFile.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    if (section == "exclude") sawExclude = true;
                    continue;
                }
                if (section == "roots")
                {
                    string r = NormalizeRoot(line);
                    if (r.Length > 0 && !s.HasRoot(r)) s.Roots.Add(r);
                }
                else if (section == "exclude")
                {
                    s.Exclude.Add(line);
                }
                else
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    s.Set(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            if (!sawExclude) s.Exclude.AddRange(DefaultExclude);
            return s;
        }

        void Set(string key, string value)
        {
            switch (key)
            {
                case "hotkey": if (value.Length > 0) Hotkey = value; break;
                case "includefiles": IncludeFiles = Bool(value, IncludeFiles); break;
                case "skiphidden": SkipHidden = Bool(value, SkipHidden); break;
                case "followlinks": FollowLinks = Bool(value, FollowLinks); break;
                case "reuseexplorer": ReuseExplorer = Bool(value, ReuseExplorer); break;
                case "learnfromexplorer": LearnFromExplorer = Bool(value, LearnFromExplorer); break;
                case "rescanhours":
                    double h;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out h) && h >= 0) RescanHours = h;
                    break;
                case "scanthreads":
                    int t;
                    if (int.TryParse(value, out t) && t > 0) ScanThreads = Math.Min(32, t);
                    break;
                case "maxentries":
                    int m;
                    if (int.TryParse(value.Replace(",", "").Replace("_", ""), out m) && m > 0) MaxEntries = m;
                    break;
            }
        }

        static bool Bool(string v, bool fallback)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": case "はい": return true;
                case "0": case "false": case "no": case "off": case "いいえ": return false;
                default: return fallback;
            }
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; ============================================================");
            sb.AppendLine(";  FolderJump 設定ファイル");
            sb.AppendLine(";  保存すると自動で反映されます。; で始まる行はコメントです。");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();
            sb.AppendLine("[General]");
            sb.AppendLine("; 呼び出しキー (例: Ctrl+Alt+Space / Ctrl+Shift+J / Alt+Q / Win+J)");
            sb.AppendLine("Hotkey = " + Hotkey);
            sb.AppendLine();
            sb.AppendLine("; ファイルも検索対象にする (false にするとフォルダだけ。軽くなります)");
            sb.AppendLine("IncludeFiles = " + B(IncludeFiles));
            sb.AppendLine();
            sb.AppendLine("; 隠しファイル/隠しフォルダを無視する");
            sb.AppendLine("SkipHidden = " + B(SkipHidden));
            sb.AppendLine();
            sb.AppendLine("; ジャンクションやシンボリックリンクの中までたどる (ループに注意)");
            sb.AppendLine("FollowLinks = " + B(FollowLinks));
            sb.AppendLine();
            sb.AppendLine("; インデックスを自動で作り直す間隔 (時間)。0 で自動更新しない");
            sb.AppendLine("RescanHours = " + RescanHours.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("; スキャンの同時実行数。サーバーに遠慮したいときは小さく (1〜32)");
            sb.AppendLine("ScanThreads = " + ScanThreads);
            sb.AppendLine();
            sb.AppendLine("; インデックスに入れる最大件数 (メモリ使用量の上限の目安: 100万件で約150MB)");
            sb.AppendLine("MaxEntries = " + MaxEntries);
            sb.AppendLine();
            sb.AppendLine("; 直前に使っていたエクスプローラーのウィンドウ(タブ)をそのまま移動させる");
            sb.AppendLine("ReuseExplorer = " + B(ReuseExplorer));
            sb.AppendLine();
            sb.AppendLine("; エクスプローラーで普通に開いたフォルダも「よく使う」として学習する");
            sb.AppendLine("LearnFromExplorer = " + B(LearnFromExplorer));
            sb.AppendLine();
            sb.AppendLine("[Roots]");
            sb.AppendLine("; 検索対象のフォルダ。1 行に 1 つ。例:");
            sb.AppendLine(";   \\\\fileserver\\share");
            sb.AppendLine(";   Z:\\");
            foreach (string r in Roots) sb.AppendLine(r);
            sb.AppendLine();
            sb.AppendLine("[Exclude]");
            sb.AppendLine("; スキャンしない名前。* と ? が使えます");
            foreach (string e in Exclude) sb.AppendLine(e);
            AppPaths.Ensure();
            TextFile.WriteAtomic(path, sb.ToString());
        }

        static string B(bool b) { return b ? "true" : "false"; }
    }
}
