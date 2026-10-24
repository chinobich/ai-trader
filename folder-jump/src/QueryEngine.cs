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

        public List<ResultItem> Compute(string text, int scope, FileIndex index, CancellationToken ct)
        {
            string t = (text ?? "").Trim();
            if (t.StartsWith(">") || t.StartsWith("＞")) return Commands(t.Substring(1));

            string asPath = PathInput.TryParse(t);
            if (asPath != null) return PathItems(asPath, index, ct);

            Query q = Query.Parse(t);
            if (q.IsEmpty) return scope >= 0 ? Children(index, scope) : Home(index, ct);

            EnsureBoost(index, ct);
            Dictionary<int, double> b;
            lock (boostLock) b = boost;
            List<SearchHit> hits = index.Search(q, scope, b, MaxResults, ct);
            var items = new List<ResultItem>(hits.Count);
            foreach (SearchHit h in hits)
            {
                ResultItem item = FromNode(index, h.Node, true);
                item.Highlights = Highlight(index.Name[h.Node], index.Key[h.Node], q.Terms);
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

        List<ResultItem> Home(FileIndex index, CancellationToken ct)
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
                if (usage.IsPinned(h.Path)) continue;
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
            var all = new List<ResultItem>();
            all.Add(Cmd("", "今すぐ再スキャン", "インデックスを最新の状態に作り直します", host.Rescan, true));
            all.Add(Cmd("", "フォルダを選んで検索対象に追加…", null, host.PickRootFolder, true));
            foreach (string r in host.CurrentSettings.Roots)
            {
                string root = r;
                all.Add(Cmd("", "検索対象から外す: " + root, null, delegate { host.RemoveRoot(root); }, true));
            }
            all.Add(Cmd("", "設定ファイルを開く", "FolderJump.ini をメモ帳で開きます (保存すると自動で反映)", host.OpenSettingsFile, false));
            all.Add(Cmd("", "データフォルダを開く", AppPaths.DataDir, host.OpenDataFolder, false));
            if (host.IsStartupRegistered)
                all.Add(Cmd("", "Windows 起動時の自動起動をやめる", null, host.ToggleStartup, true));
            else
                all.Add(Cmd("", "Windows 起動時に自動で起動する", "スタートアップにショートカットを作ります", host.ToggleStartup, true));
            all.Add(Cmd("", "履歴をクリア", "「よく使う」の記録を消します (ピン留めは残ります)", host.ClearHistory, true));
            all.Add(Cmd("", "FolderJump を終了", null, host.Quit, false));

            Query q = Query.Parse(filter);
            if (q.IsEmpty) return all;
            var matched = new List<ResultItem>();
            foreach (ResultItem item in all)
            {
                string key = TextNorm.Key(item.Title + " " + (item.Subtitle ?? ""));
                bool ok = true;
                foreach (string term in q.Terms)
                    if (key.IndexOf(term, StringComparison.Ordinal) < 0) { ok = false; break; }
                if (ok) matched.Add(item);
            }
            return matched;
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
        public static int[] Highlight(string name, string key, string[] terms)
        {
            if (name == null || key == null || name.Length != key.Length || terms.Length == 0) return null;
            var marks = new bool[name.Length];
            bool any = false;
            foreach (string term in terms)
            {
                int pos = key.IndexOf(term, StringComparison.Ordinal);
                if (pos < 0) continue;
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
