using System;
using System.Collections.Generic;
using System.Threading;

namespace FolderJump
{
    internal enum ItemKind { Header, Entry, Command }

    /// <summary>結果リストの 1 行。</summary>
    internal sealed class ResultItem
    {
        public ItemKind Kind;
        public string Title;
        public string Subtitle;
        public string Path;
        public bool IsDir;
        public int Node = -1;
        public long Mtime;
        public bool Pinned;
        public string Badge;
        public string Glyph;          // コマンド等のアイコン (Segoe Fluent Icons)
        public int[] Highlights;      // [開始, 長さ, 開始, 長さ, ...]
        public Action Command;
        public bool KeepOpen;         // 実行後もランチャーを閉じない

        public bool Selectable { get { return Kind != ItemKind.Header; } }
    }

    /// <summary>UI から呼ばれる操作 (コマンドパレット用)。</summary>
    internal interface ICommandHost
    {
        Settings CurrentSettings { get; }
        bool IsStartupRegistered { get; }
        void Rescan();
        void PickRootFolder();
        void AddRoot(string path);
        void RemoveRoot(string path);
        void OpenSettingsFile();
        void OpenDataFolder();
        void ToggleStartup();
        void ClearHistory();
        void Quit();
        void SetQueryText(string text);
        void ChangeSetting(string key, string value);
    }

    /// <summary>入力文字列から結果リストを組み立てる (バックグラウンドスレッドで実行)。</summary>
    internal sealed class QueryEngine
    {
        public const int MaxResults = 80;
        public const int MaxChildren = 400;

        readonly ICommandHost host;
        readonly UsageStore usage;
        readonly object boostLock = new object();
        FileIndex boostIndex;
        int boostVersion = -1;
        Dictionary<int, double> boost = new Dictionary<int, double>();
        Dictionary<string, int> pathToNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public QueryEngine(ICommandHost host, UsageStore usage)
        {
            this.host = host;
            this.usage = usage;
        }

        /// <param name="preferFolders">保存ダイアログから呼ばれたとき true (フォルダを優先)</param>
        public List<ResultItem> Compute(string text, int scope, FileIndex index, CancellationToken ct, bool preferFolders = false)
        {
            string t = (text ?? "").Trim();
            if (t.StartsWith(">") || t.StartsWith("＞")) return Commands(t.Substring(1));
            if (t == "?" || t == "？") return Help();

            string asPath = PathInput.TryParse(t);
            if (asPath != null) return PathItems(asPath, index, ct);

            Query q = Query.Parse(t, host.CurrentSettings.Synonyms, DateTime.Now);
            q.PreferFolders = preferFolders;
            if (q.IsEmpty) return scope >= 0 ? Children(index, scope) : Home(index, ct, preferFolders);

            EnsureBoost(index, ct);
            Dictionary<int, double> b;
            lock (boostLock) b = boost;
            List<SearchHit> hits = index.Search(q, scope, b, MaxResults, ct);
            var items = new List<ResultItem>(hits.Count);
            foreach (SearchHit h in hits)
            {
                ResultItem item = FromNode(index, h.Node, true);
                item.Highlights = Highlight(index.Name[h.Node], index.Key[h.Node], q.Alts);
                items.Add(item);
            }
            return items;
        }

        /// <summary>パス → ノード番号 (ピン/履歴/貼り付けパスを Tab で開くとき用)。</summary>
        public int NodeOf(FileIndex index, string path, CancellationToken ct)
        {
            EnsureBoost(index, ct);
            int node;
            lock (boostLock)
                if (boostIndex == index && pathToNode.TryGetValue(PathUtil.Trim(path), out node)) return node;
            return index.Find(path, ct);
        }

        void EnsureBoost(FileIndex index, CancellationToken ct)
        {
            int v = usage.Version;
            lock (boostLock)
                if (boostIndex == index && boostVersion == v) return;

            Dictionary<string, double> weights = usage.BoostByPath();
            Dictionary<string, int> resolved = index.Resolve(weights.Keys, ct);
            var map = new Dictionary<int, double>();
            foreach (KeyValuePair<string, int> kv in resolved)
            {
                double w;
                if (weights.TryGetValue(kv.Key, out w)) map[kv.Value] = w;
            }
            lock (boostLock)
            {
                boost = map;
                pathToNode = resolved;
                boostIndex = index;
                boostVersion = v;
            }
        }

        ResultItem FromNode(FileIndex index, int node, bool withParent)
        {
            string path = index.FullPath(node);
            var item = new ResultItem
            {
                Kind = ItemKind.Entry,
                Title = index.Name[node],
                Path = path,
                IsDir = index.IsDir(node),
                Node = node,
                Mtime = index.Mtime[node],
                Pinned = usage.IsPinned(path)
            };
            if (index.IsRoot(node)) item.Subtitle = "検索対象";
            else if (withParent) item.Subtitle = PathUtil.Pretty(index.FullPath(index.Parent[node]), 90);
            item.Badge = FormatDate(item.Mtime);
            return item;
        }

        List<ResultItem> Home(FileIndex index, CancellationToken ct, bool foldersOnly)
        {
            var items = new List<ResultItem>();
            Settings s = host.CurrentSettings;
            if (s.Roots.Count == 0)
            {
                items.Add(Cmd("", "フォルダを選んで検索対象に追加…", "ファイルサーバーの共有フォルダなどを選んでください", host.PickRootFolder, true));
                return items;
            }
            if (index.Count > 0) EnsureBoost(index, ct);
            Dictionary<string, int> map;
            lock (boostLock) map = boostIndex == index ? pathToNode : new Dictionary<string, int>();

            List<UsageEntry> pins = usage.Pins();
            if (pins.Count > 0)
            {
                items.Add(Header("ピン留め"));
                foreach (UsageEntry p in pins) items.Add(FromUsage(p, map, true));
            }
            var recent = new List<ResultItem>();
            foreach (UsageEntry h in usage.TopHistory(40))
            {
                if (usage.IsPinned(h.Path) || (foldersOnly && !h.IsDir)) continue;
                recent.Add(FromUsage(h, map, false));
                if (recent.Count >= 10) break;
            }
            if (recent.Count > 0)
            {
                items.Add(Header("よく使う"));
                items.AddRange(recent);
            }
            items.Add(Header("検索対象"));
            foreach (string r in s.Roots)
            {
                int node;
                var item = new ResultItem
                {
                    Kind = ItemKind.Entry, Title = r, Path = r, IsDir = true, Subtitle = "Tab で中を表示",
                    Pinned = usage.IsPinned(r)
                };
                if (map.TryGetValue(r, out node)) item.Node = node;
                else
                {
                    for (int i = 0; i < index.Count && i < 64; i++)
                        if (index.Parent[i] < 0 && string.Equals(index.Name[i], r, StringComparison.OrdinalIgnoreCase)) { item.Node = i; break; }
                }
                items.Add(item);
            }
            return items;
        }

        ResultItem FromUsage(UsageEntry e, Dictionary<string, int> map, bool pinned)
        {
            int node;
            var item = new ResultItem
            {
                Kind = ItemKind.Entry,
                Title = PathUtil.LastSegment(e.Path),
                Subtitle = PathUtil.Pretty(PathUtil.Parent(e.Path) ?? "", 90),
                Path = e.Path,
                IsDir = e.IsDir,
                Pinned = pinned || usage.IsPinned(e.Path)
            };
            if (map.TryGetValue(e.Path, out node)) item.Node = node;
            return item;
        }

        List<ResultItem> Children(FileIndex index, int scope)
        {
            var items = new List<ResultItem>();
            foreach (int c in index.Children(scope, MaxChildren)) items.Add(FromNode(index, c, false));
            return items;
        }

        List<ResultItem> PathItems(string path, FileIndex index, CancellationToken ct)
        {
            var items = new List<ResultItem>();
            var open = new ResultItem
            {
                Kind = ItemKind.Entry,
                Title = PathUtil.LastSegment(path),
                Subtitle = path,
                Path = path,
                IsDir = PathUtil.LastSegment(path).LastIndexOf('.') <= 0,
                Badge = "貼り付けたパス",
                Pinned = usage.IsPinned(path)
            };
            if (index.Count > 0)
            {
                int node = NodeOf(index, path, ct);
                if (node >= 0)
                {
                    open.Node = node;
                    open.IsDir = index.IsDir(node);
                    open.Badge = "Tab で中を表示";
                }
            }
            items.Add(open);
            Settings s = host.CurrentSettings;
            if (!s.IsCovered(path))
            {
                string p = path;
                items.Add(Cmd("", "このフォルダを検索対象に追加", path, delegate { host.AddRoot(p); }, true));
            }
            return items;
        }

        List<ResultItem> Commands(string filter)
        {
            var main = new List<ResultItem>();
            main.Add(Cmd("\uE72C", "今すぐ再スキャン", "インデックスを最新の状態に作り直します", host.Rescan, true));
            main.Add(Cmd("\uE710", "フォルダを選んで検索対象に追加…", null, host.PickRootFolder, true));
            foreach (string r in host.CurrentSettings.Roots)
            {
                string root = r;
                main.Add(Cmd("\uE74D", "検索対象から外す: " + root, null, delegate { host.RemoveRoot(root); }, true));
            }
            main.Add(Cmd("\uE790", "見た目の設定…", "テーマ・アクセント色・大きさ・透明度などを変えます", delegate { host.SetQueryText(">見た目 "); }, true));
            main.Add(Cmd("\uE897", "検索の書き方 (ヘルプ)", "拡張子・更新日での絞り込みなど (? と入力しても出ます)", delegate { host.SetQueryText("?"); }, true));
            main.Add(Cmd("\uE713", "設定ファイルを開く", "FolderJump.ini をメモ帳で開きます (表記ゆれ辞書もここで編集)", host.OpenSettingsFile, false));
            main.Add(Cmd("\uE8B7", "データフォルダを開く", AppPaths.DataDir, host.OpenDataFolder, false));
            if (host.IsStartupRegistered)
                main.Add(Cmd("\uE7E8", "Windows 起動時の自動起動をやめる", null, host.ToggleStartup, true));
            else
                main.Add(Cmd("\uE7E8", "Windows 起動時に自動で起動する", "スタートアップにショートカットを作ります", host.ToggleStartup, true));
            main.Add(Cmd("\uE81C", "履歴をクリア", "「よく使う」の記録を消します (ピン留めは残ります)", host.ClearHistory, true));
            main.Add(Cmd("\uE711", "FolderJump を終了", null, host.Quit, false));

            Query q = Query.Parse(filter);
            if (q.Terms.Length == 0) return main;
            var all = new List<ResultItem>(main);
            all.AddRange(AppearanceCommands());
            var first = new List<ResultItem>();
            var rest = new List<ResultItem>();
            foreach (ResultItem item in all)
            {
                string key = TextNorm.Key(item.Title + " " + (item.Subtitle ?? ""));
                bool ok = true;
                foreach (string term in q.Terms)
                    if (key.IndexOf(term, StringComparison.Ordinal) < 0) { ok = false; break; }
                if (!ok) continue;
                // 名前が入力した語で始まるコマンドを先に (「>設定」なら「設定ファイルを開く」が一番上)
                if (TextNorm.Key(item.Title).StartsWith(q.Terms[0], StringComparison.Ordinal)) first.Add(item);
                else rest.Add(item);
            }
            first.AddRange(rest);
            return first;
        }

        /// <summary>「>見た目」で出る設定の切り替え。選ぶとすぐ反映されて設定ファイルにも保存される。</summary>
        List<ResultItem> AppearanceCommands()
        {
            Settings s = host.CurrentSettings;
            var list = new List<ResultItem>();
            string theme = s.Theme.ToLowerInvariant();
            list.Add(Setting("\uE708", "テーマ: ダーク", "theme", "dark", theme == "dark"));
            list.Add(Setting("\uE706", "テーマ: ライト", "theme", "light", theme == "light"));
            list.Add(Setting("\uE7F4", "テーマ: Windows の設定に合わせる", "theme", "system", theme == "system"));
            string accent = s.Accent.ToLowerInvariant();
            foreach (string[] p in Settings.AccentPresets)
                list.Add(Setting("\uE790", "アクセント色: " + p[1], "accent", p[0], accent == p[0] || accent == p[1].ToLowerInvariant()));
            list.Add(Setting("\uE790", "アクセント色: Windows のアクセント色", "accent", "windows", accent == "windows"));
            list.Add(Setting("\uE8A3", "表示を大きくする (" + Math.Min(160, s.Zoom + 10) + "%)", "zoom", (s.Zoom + 10).ToString(), false));
            list.Add(Setting("\uE71F", "表示を小さくする (" + Math.Max(70, s.Zoom - 10) + "%)", "zoom", (s.Zoom - 10).ToString(), false));
            list.Add(Setting("\uE72C", "表示の大きさを 100% に戻す", "zoom", "100", s.Zoom == 100));
            list.Add(Setting("\uE740", "幅を広くする (" + Math.Min(1400, s.Width + 80) + ")", "width", (s.Width + 80).ToString(), false));
            list.Add(Setting("\uE73F", "幅を狭くする (" + Math.Max(560, s.Width - 80) + ")", "width", (s.Width - 80).ToString(), false));
            list.Add(Setting("\uE72C", "幅を標準 (780) に戻す", "width", "780", s.Width == 780));
            list.Add(Setting("\uE790", "背景: 不透明", "backgroundopacity", "100", s.BackgroundOpacity == 100));
            list.Add(Setting("\uE790", "背景: 少し透ける", "backgroundopacity", "90", s.BackgroundOpacity == 90));
            list.Add(Setting("\uE790", "背景: よく透ける", "backgroundopacity", "78", s.BackgroundOpacity == 78));
            list.Add(Setting("\uE8FD", s.Compact ? "コンパクト表示をやめる (2 行表示)" : "コンパクト表示にする (1 行表示)", "compact", s.Compact ? "false" : "true", false));
            list.Add(Setting("\uE768", s.Animations ? "アニメーションを切る" : "アニメーションを付ける", "animations", s.Animations ? "false" : "true", false));
            return list;
        }

        ResultItem Setting(string glyph, string title, string key, string value, bool current)
        {
            ResultItem item = Cmd(glyph, title, "見た目 (選ぶとすぐ反映)", delegate { host.ChangeSetting(key, value); }, true);
            if (current) item.Badge = "使用中";
            return item;
        }

        /// <summary>「?」で出る検索の書き方の一覧。選ぶと例が入力される。</summary>
        List<ResultItem> Help()
        {
            var list = new List<ResultItem>();
            list.Add(Example("見積 2024", "スペースで区切ると、すべてを含むものに絞り込み (親フォルダの名前も対象)"));
            list.Add(Example("見積 .xlsx", "拡張子で絞り込み (.xls だけで .xlsx / .xlsm も)。.* はファイルだけ"));
            list.Add(Example("営業/", "末尾に / を付けるとフォルダだけ (/ だけなら、すべてのフォルダ)"));
            list.Add(Example(".pdf @今週", "更新日で絞り込み: @今日 @昨日 @今週 @先週 @今月 @先月 @今年 @去年"));
            list.Add(Example("@7日", "最近 N 日に更新したもの (@2週 @3か月 @1年 も)。新しい順に並びます"));
            list.Add(Example("議事録 @2024-05", "年・月・日で指定: @2024 @2024-05 @2024-05-12 @2024年度 @r6"));
            list.Add(Example("契約 @2024-04以降", "以降 / 以前 を付けると、その日より後 / 前"));
            list.Add(Example("議事録 -旧", "- を付けた言葉を含むものを除外"));
            list.Add(Example("R6 見積", "和暦と西暦は同じものとして探します (2024 = 令和6 = R6)"));
            list.Add(Example("見積もり", "表記ゆれ辞書: 見積 / 見積り / お見積 なども探します (> 設定ファイル で追加)"));
            list.Add(Example(">", "コマンド (再スキャン・見た目の設定など)"));
            return list;
        }

        ResultItem Example(string example, string description)
        {
            string text = example;
            ResultItem item = Cmd("\uE721", example, description, delegate { host.SetQueryText(text); }, true);
            item.Badge = "例";
            return item;
        }

        static ResultItem Cmd(string glyph, string title, string subtitle, Action action, bool keepOpen)
        {
            return new ResultItem { Kind = ItemKind.Command, Glyph = glyph, Title = title, Subtitle = subtitle, Command = action, KeepOpen = keepOpen };
        }

        static ResultItem Header(string title)
        {
            return new ResultItem { Kind = ItemKind.Header, Title = title };
        }

        public static string FormatDate(long filetime)
        {
            if (filetime <= 0) return null;
            try { return DateTime.FromFileTimeUtc(filetime).ToLocalTime().ToString("yyyy/MM/dd"); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        /// <summary>名前のどこがキーワードに一致したか (正規化で長さが変わる名前は諦める)。</summary>
        public static int[] Highlight(string name, string key, string[][] alts)
        {
            if (name == null || key == null || name.Length != key.Length || alts.Length == 0) return null;
            var marks = new bool[name.Length];
            bool any = false;
            foreach (string[] a in alts)
            {
                int pos;
                string term = FileIndex.FirstMatch(key, a, out pos);
                if (term == null) continue;
                for (int i = pos; i < pos + term.Length; i++) marks[i] = true;
                any = true;
            }
            if (!any) return null;
            var spans = new List<int>();
            int start = -1;
            for (int i = 0; i <= marks.Length; i++)
            {
                bool on = i < marks.Length && marks[i];
                if (on && start < 0) start = i;
                if (!on && start >= 0) { spans.Add(start); spans.Add(i - start); start = -1; }
            }
            return spans.ToArray();
        }
    }
}
