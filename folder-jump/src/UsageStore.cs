using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderJump
{
    internal sealed class UsageEntry
    {
        public string Path;
        public bool IsDir;
        public int Count;
        public long LastUtcTicks;
    }

    /// <summary>
    /// 「よく使う/最近使った」履歴とピン留め。
    /// 使った回数と最後に使った時期から重み (frecency) を出して検索順位に反映する。
    /// </summary>
    internal sealed class UsageStore
    {
        const int MaxHistory = 2000;
        public const double PinBonus = 25;

        readonly object gate = new object();
        readonly Dictionary<string, UsageEntry> history = new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
        readonly List<UsageEntry> pins = new List<UsageEntry>();
        readonly string historyFile, pinsFile;
        int version;

        public UsageStore(string historyFile, string pinsFile)
        {
            this.historyFile = historyFile;
            this.pinsFile = pinsFile;
        }

        /// <summary>変更のたびに増える番号 (検索側のキャッシュ判定用)。</summary>
        public int Version { get { lock (gate) return version; } }

        public void Load()
        {
            lock (gate)
            {
                history.Clear();
                pins.Clear();
                foreach (UsageEntry e in Read(historyFile))
                    if (!history.ContainsKey(e.Path)) history.Add(e.Path, e);
                foreach (UsageEntry e in Read(pinsFile))
                    if (!pins.Any(p => string.Equals(p.Path, e.Path, StringComparison.OrdinalIgnoreCase))) pins.Add(e);
                version++;
            }
        }

        public void Record(string path, bool isDir)
        {
            path = PathUtil.Trim(path);
            lock (gate)
            {
                UsageEntry e;
                if (!history.TryGetValue(path, out e))
                {
                    e = new UsageEntry { Path = path, IsDir = isDir };
                    history.Add(path, e);
                }
                e.IsDir = isDir;
                e.Count++;
                e.LastUtcTicks = DateTime.UtcNow.Ticks;
                version++;
                if (history.Count > MaxHistory)
                {
                    var now = DateTime.UtcNow;
                    foreach (UsageEntry old in history.Values.OrderBy(x => Frecency(x, now)).Take(history.Count - MaxHistory).ToList())
                        history.Remove(old.Path);
                }
                SaveHistoryLocked();
            }
        }

        public bool IsPinned(string path)
        {
            path = PathUtil.Trim(path);
            lock (gate)
            {
                foreach (UsageEntry p in pins)
                    if (string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        /// <summary>ピン留めを切り替え、新しい状態 (true = ピン留め中) を返す。</summary>
        public bool TogglePin(string path, bool isDir)
        {
            path = PathUtil.Trim(path);
            lock (gate)
            {
                int idx = pins.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
                bool pinned;
                if (idx >= 0) { pins.RemoveAt(idx); pinned = false; }
                else { pins.Add(new UsageEntry { Path = path, IsDir = isDir, LastUtcTicks = DateTime.UtcNow.Ticks }); pinned = true; }
                version++;
                Write(pinsFile, pins);
                return pinned;
            }
        }

        public List<UsageEntry> Pins()
        {
            lock (gate) return new List<UsageEntry>(pins);
        }

        public List<UsageEntry> TopHistory(int n)
        {
            var now = DateTime.UtcNow;
            lock (gate) return history.Values.OrderByDescending(e => Frecency(e, now)).Take(n).ToList();
        }

        public void ClearHistory()
        {
            lock (gate)
            {
                history.Clear();
                version++;
                SaveHistoryLocked();
            }
        }

        /// <summary>パス → 検索スコアへの加点。</summary>
        public Dictionary<string, double> BoostByPath()
        {
            var now = DateTime.UtcNow;
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                foreach (UsageEntry e in history.Values) map[e.Path] = Frecency(e, now);
                foreach (UsageEntry p in pins)
                {
                    double v;
                    map.TryGetValue(p.Path, out v);
                    map[p.Path] = v + PinBonus;
                }
            }
            return map;
        }

        public static double Frecency(UsageEntry e, DateTime nowUtc)
        {
            double hours = (nowUtc - new DateTime(e.LastUtcTicks, DateTimeKind.Utc)).TotalHours;
            double recency = hours < 4 ? 4 : hours < 24 ? 2.5 : hours < 24 * 7 ? 1.6 : hours < 24 * 30 ? 1 : 0.5;
            return Math.Min(40, Math.Log(1 + e.Count) * recency * 6);
        }

        void SaveHistoryLocked()
        {
            try { Write(historyFile, history.Values); }
            catch (Exception ex) { Log.Error("history save", ex); }
        }

        static IEnumerable<UsageEntry> Read(string file)
        {
            var list = new List<UsageEntry>();
            if (file == null || !File.Exists(file)) return list;
            try
            {
                foreach (string line in TextFile.ReadLines(file))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 4 || f[0].Length == 0) continue;
                    int count;
                    long ticks;
                    int.TryParse(f[2], out count);
                    long.TryParse(f[3], out ticks);
                    list.Add(new UsageEntry { Path = PathUtil.Trim(f[0]), IsDir = f[1] == "d", Count = count, LastUtcTicks = ticks });
                }
            }
            catch (Exception ex)
            {
                Log.Error("usage read " + file, ex);
            }
            return list;
        }

        static void Write(string file, IEnumerable<UsageEntry> entries)
        {
            if (file == null) return;
            var sb = new StringBuilder();
            foreach (UsageEntry e in entries)
            {
                sb.Append(e.Path).Append('\t').Append(e.IsDir ? "d" : "f").Append('\t')
                  .Append(e.Count.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.LastUtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            AppPaths.Ensure();
            TextFile.WriteAtomic(file, sb.ToString());
        }
    }
}
