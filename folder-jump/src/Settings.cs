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
        public bool SaveDialogJump = true;
        public bool JapaneseEra = true;
        public string Theme = "dark";
        public string Accent = "cyan";
        public int Zoom = 100;
        public int Width = 780;
        public int BackgroundOpacity = 96;
        public bool Compact = false;
        public bool Animations = true;
        public List<string> Roots = new List<string>();
        public List<string> Exclude = new List<string>();
        public List<string> SynonymLines = new List<string>();
        Synonyms synonyms;

        public static readonly string[] DefaultExclude =
        {
            "$RECYCLE.BIN", "System Volume Information", "~snapshot", ".snapshot", "#recycle", "@Recycle", "@eaDir",
            "Thumbs.db", "desktop.ini", ".DS_Store", "~$*", "*.tmp"
        };

        /// <summary>アクセント色の名前と 2 色 (グラデーションの両端)。</summary>
        public static readonly string[][] AccentPresets =
        {
            new[] { "cyan", "シアン", "#38BDF8", "#A78BFA" },
            new[] { "blue", "ブルー", "#60A5FA", "#818CF8" },
            new[] { "violet", "バイオレット", "#A78BFA", "#F472B6" },
            new[] { "green", "グリーン", "#34D399", "#22D3EE" },
            new[] { "orange", "オレンジ", "#FB923C", "#F472B6" },
            new[] { "pink", "ピンク", "#F472B6", "#A78BFA" }
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

        /// <summary>見た目に関わる設定の要約。変わったら画面を作り直す。</summary>
        public string AppearanceSignature
        {
            get
            {
                return string.Join("|", Theme.ToLowerInvariant(), Accent.ToLowerInvariant(), Zoom, Width, BackgroundOpacity, Compact, Animations);
            }
        }

        /// <summary>表記ゆれ辞書 (初回に組み立てて使い回す)。</summary>
        public Synonyms Synonyms
        {
            get
            {
                Synonyms d = synonyms;
                if (d == null)
                {
                    d = new Synonyms(SynonymLines, JapaneseEra);
                    synonyms = d;
                }
                return d;
            }
        }

        public Settings Clone()
        {
            var s = (Settings)MemberwiseClone();
            s.Roots = new List<string>(Roots);
            s.Exclude = new List<string>(Exclude);
            s.SynonymLines = new List<string>(SynonymLines);
            s.synonyms = null;
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
                s.SynonymLines.AddRange(Synonyms.Defaults);
                return s;
            }
            string section = "";
            bool sawExclude = false, sawSynonyms = false;
            foreach (string raw in TextFile.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    if (section == "exclude") sawExclude = true;
                    if (section == "synonyms") sawSynonyms = true;
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
                else if (section == "synonyms")
                {
                    s.SynonymLines.Add(line);
                }
                else
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    s.Set(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            if (!sawExclude) s.Exclude.AddRange(DefaultExclude);
            if (!sawSynonyms) s.SynonymLines.AddRange(Synonyms.Defaults); // 以前の設定ファイルには辞書がないので既定を使う
            return s;
        }

        /// <summary>1 項目を変更する (キーは小文字)。知らないキーなら false。</summary>
        public bool Set(string key, string value)
        {
            synonyms = null;
            switch (key)
            {
                case "hotkey": if (value.Length > 0) Hotkey = value; break;
                case "includefiles": IncludeFiles = Bool(value, IncludeFiles); break;
                case "skiphidden": SkipHidden = Bool(value, SkipHidden); break;
                case "followlinks": FollowLinks = Bool(value, FollowLinks); break;
                case "reuseexplorer": ReuseExplorer = Bool(value, ReuseExplorer); break;
                case "learnfromexplorer": LearnFromExplorer = Bool(value, LearnFromExplorer); break;
                case "savedialogjump": SaveDialogJump = Bool(value, SaveDialogJump); break;
                case "japaneseera": JapaneseEra = Bool(value, JapaneseEra); break;
                case "compact": Compact = Bool(value, Compact); break;
                case "animations": Animations = Bool(value, Animations); break;
                case "theme":
                    string th = value.Trim().ToLowerInvariant();
                    if (th == "dark" || th == "light" || th == "system") Theme = th;
                    break;
                case "accent": if (value.Trim().Length > 0) Accent = value.Trim(); break;
                case "zoom": Zoom = Clamp(value, Zoom, 70, 160); break;
                case "width": Width = Clamp(value, Width, 560, 1400); break;
                case "backgroundopacity": BackgroundOpacity = Clamp(value, BackgroundOpacity, 60, 100); break;
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
                default:
                    return false;
            }
            return true;
        }

        static int Clamp(string value, int fallback, int min, int max)
        {
            int v;
            if (!int.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
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
            sb.AppendLine("; 「名前を付けて保存」などの画面で呼び出すと、その画面を選んだフォルダへ移動させる");
            sb.AppendLine("SaveDialogJump = " + B(SaveDialogJump));
            sb.AppendLine();
            sb.AppendLine("; 和暦と西暦を同じものとして探す (2024 で「令和6」「R6」も見つかる)");
            sb.AppendLine("JapaneseEra = " + B(JapaneseEra));
            sb.AppendLine();
            sb.AppendLine("[Appearance]");
            sb.AppendLine("; テーマ: dark / light / system (Windows の設定に合わせる)");
            sb.AppendLine("Theme = " + Theme);
            sb.AppendLine();
            sb.AppendLine("; アクセント色: cyan / blue / violet / green / orange / pink / windows (Windows のアクセント色) / #RRGGBB");
            sb.AppendLine("Accent = " + Accent);
            sb.AppendLine();
            sb.AppendLine("; 表示の大きさ (%)。文字もアイコンも一緒に拡大されます (70〜160)");
            sb.AppendLine("Zoom = " + Zoom);
            sb.AppendLine();
            sb.AppendLine("; ランチャーの幅 (100% 表示のときのピクセル数、560〜1400)");
            sb.AppendLine("Width = " + Width);
            sb.AppendLine();
            sb.AppendLine("; 背景の不透明度 (%)。小さくすると後ろが透けて見えます (60〜100)");
            sb.AppendLine("BackgroundOpacity = " + BackgroundOpacity);
            sb.AppendLine();
            sb.AppendLine("; 1 行表示にして、一度にたくさん並べる (パスはマウスを乗せると表示)");
            sb.AppendLine("Compact = " + B(Compact));
            sb.AppendLine();
            sb.AppendLine("; 表示時やスキャン中のアニメーション");
            sb.AppendLine("Animations = " + B(Animations));
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
            sb.AppendLine();
            sb.AppendLine("[Synonyms]");
            sb.AppendLine("; 表記ゆれ辞書。1 行に「同じものとして探したい言葉」をカンマ区切りで並べます");
            sb.AppendLine("; 例: 見積, 見積り, 見積もり  → どれで探しても全部見つかります");
            foreach (string line in SynonymLines) sb.AppendLine(line);
            AppPaths.Ensure();
            TextFile.WriteAtomic(path, sb.ToString());
        }

        static string B(bool b) { return b ? "true" : "false"; }
    }
}
