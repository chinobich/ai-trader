using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace FolderJump
{
    internal struct SearchHit
    {
        public int Node;
        public double Score;
    }

    /// <summary>
    /// ファイルサーバーのフォルダ/ファイル一覧をメモリ上に持つ木構造。
    /// 親は必ず子より前に並ぶ (Parent[i] &lt; i) ので、先頭から1回なめるだけで
    /// 「パスのどこかにキーワードを含むか」を判定できる。
    /// </summary>
    internal sealed class FileIndex
    {
        public const byte FlagDir = 1, FlagRoot = 2;
        const int FileVersion = 3;
        const int ScopeBit = 1 << 30, ExclBit = 1 << 29;
        const long Day = 864000000000L; // FILETIME の 1 日

        public readonly int Count;
        public readonly int[] Parent;
        public readonly string[] Name;
        public readonly string[] Key;
        public readonly long[] Mtime;
        public readonly byte[] Flags;
        public readonly ushort[] Depth;
        public readonly string[] Roots;
        public readonly string Signature;
        public readonly DateTime BuiltUtc;
        public readonly bool Complete;
        public readonly bool Truncated;

        int[] spare; // 検索用の作業バッファ (使い回し)

        public static readonly FileIndex Empty = new FileIndex(0, new int[0], new string[0], new string[0], new long[0], new byte[0],
            new string[0], "", DateTime.MinValue, false, false);

        public FileIndex(int count, int[] parent, string[] name, string[] key, long[] mtime, byte[] flags,
            string[] roots, string signature, DateTime builtUtc, bool complete, bool truncated)
        {
            Count = count;
            Parent = parent;
            Name = name;
            Key = key;
            Mtime = mtime;
            Flags = flags;
            Roots = roots;
            Signature = signature ?? "";
            BuiltUtc = builtUtc;
            Complete = complete;
            Truncated = truncated;
            Depth = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int p = parent[i];
                Depth[i] = p < 0 ? (ushort)0 : (ushort)Math.Min(ushort.MaxValue, Depth[p] + 1);
            }
        }

        public bool IsDir(int i) { return (Flags[i] & FlagDir) != 0; }
        public bool IsRoot(int i) { return (Flags[i] & FlagRoot) != 0; }

        public string FullPath(int i)
        {
            if (i < 0 || i >= Count) return null;
            if (Parent[i] < 0) return Name[i];
            var chain = new List<string>(16);
            int cur = i;
            while (cur >= 0)
            {
                chain.Add(Name[cur]);
                cur = Parent[cur];
            }
            var sb = new StringBuilder(128);
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                string part = chain[k];
                if (sb.Length > 0 && sb[sb.Length - 1] != '\\' && sb[sb.Length - 1] != '/') sb.Append(PathUtil.Sep);
                sb.Append(part);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 検索
        // ------------------------------------------------------------------

        /// <summary>
        /// すべてのキーワードを「名前 or 親フォルダ名」に含み、かつ少なくとも1つは自分の名前に含む項目を返す。
        /// scope を指定するとそのフォルダの中だけを探す。
        /// </summary>
        public List<SearchHit> Search(Query q, int scope, IDictionary<int, double> boost, int limit, CancellationToken ct)
        {
            var heap = new TopK(limit);
            int n = Count;
            int k = Math.Min(q.Terms.Length, 24);
            if (k == 0 || n == 0 || (scope >= n)) return heap.ToSortedList();

            string[] terms = q.Terms;
            string[] excl = q.Excludes;
            int all = (1 << k) - 1;
            int start = scope >= 0 ? scope : 0;
            long now = DateTime.UtcNow.ToFileTimeUtc();

            int[] anc = Interlocked.Exchange(ref spare, null);
            if (anc == null || anc.Length < n) anc = new int[n];
            try
            {
                for (int i = start; i < n; i++)
                {
                    if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    int p = Parent[i];
                    int inherited = p >= start ? anc[p] : 0;
                    if (scope >= 0)
                    {
                        if (i == scope) { anc[i] = ScopeBit; continue; }
                        if ((inherited & ScopeBit) == 0) { anc[i] = 0; continue; }
                    }
                    if ((inherited & ExclBit) != 0) { anc[i] = inherited; continue; }

                    string key = Key[i];
                    int own = 0;
                    for (int t = 0; t < k; t++)
                        if (key.IndexOf(terms[t], StringComparison.Ordinal) >= 0) own |= 1 << t;
                    int m = inherited | own;
                    for (int e = 0; e < excl.Length; e++)
                        if (key.IndexOf(excl[e], StringComparison.Ordinal) >= 0) { m |= ExclBit; break; }
                    anc[i] = m;

                    if (own == 0 || (m & ExclBit) != 0 || (m & all) != all) continue;

                    double score = Score(i, key, terms, k, own, all, now);
                    double b;
                    if (boost != null && boost.TryGetValue(i, out b)) score += b;
                    heap.Offer(score, i);
                }
            }
            finally
            {
                Interlocked.Exchange(ref spare, anc);
            }
            return heap.ToSortedList();
        }

        double Score(int i, string key, string[] terms, int k, int own, int all, long now)
        {
            double s = 0;
            int matched = 0;
            for (int t = 0; t < k; t++)
            {
                if ((own & (1 << t)) == 0) { s += 1.5; continue; } // 親フォルダ側で一致
                string term = terms[t];
                int pos = key.IndexOf(term, StringComparison.Ordinal);
                s += 10;
                matched += term.Length;
                if (pos == 0) s += 6;
                else if (IsBoundary(key, pos)) s += 3;
            }
            if (own == all) s += 6;

            int baseLen = key.Length;
            if ((Flags[i] & FlagDir) == 0)
            {
                int dot = key.LastIndexOf('.');
                if (dot > 0) baseLen = dot;
            }
            if (baseLen > 0) s += 10.0 * Math.Min(1.0, (double)matched / baseLen);

            if ((Flags[i] & FlagDir) != 0) s += 3;
            s -= Depth[i] * 0.35;

            long mt = Mtime[i];
            if (mt > 0)
            {
                long age = now - mt;
                if (age < 30 * Day) s += 2;
                else if (age < 365 * Day) s += 1;
            }
            return s;
        }

        static bool IsBoundary(string s, int pos)
        {
            char prev = s[pos - 1], cur = s[pos];
            if (" _-.()[]{}（）【】「」『』・,、。+&#~".IndexOf(prev) >= 0) return true;
            if (char.IsDigit(prev) != char.IsDigit(cur)) return true;
            return (prev < 0x80) != (cur < 0x80);
        }

        /// <summary>フォルダ直下の項目 (フォルダが先、名前は自然順)。</summary>
        public List<int> Children(int dir, int max)
        {
            var list = new List<int>();
            if (dir < 0 || dir >= Count) return list;
            for (int i = dir + 1; i < Count; i++)
                if (Parent[i] == dir) list.Add(i);
            list.Sort((a, b) =>
            {
                bool da = IsDir(a), db = IsDir(b);
                if (da != db) return da ? -1 : 1;
                return NameOrder.Compare(Name[a], Name[b]);
            });
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        /// <summary>フルパスからノード番号を引く (まとめて1パスで解決)。</summary>
        public Dictionary<string, int> Resolve(ICollection<string> paths, CancellationToken ct)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (paths == null || paths.Count == 0 || Count == 0) return result;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lastKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in paths)
            {
                string p = PathUtil.Trim(raw);
                wanted.Add(p);
                lastKeys.Add(TextNorm.Key(PathUtil.LastSegment(p)));
            }
            for (int i = 0; i < Count; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (Parent[i] < 0)
                {
                    string rp = PathUtil.Trim(Name[i]);
                    if (wanted.Contains(rp)) result[rp] = i;
                    continue;
                }
                if (!lastKeys.Contains(Key[i])) continue;
                string fp = FullPath(i);
                if (wanted.Contains(fp)) result[fp] = i;
            }
            return result;
        }

        public int Find(string path, CancellationToken ct)
        {
            var r = Resolve(new[] { path }, ct);
            int node;
            return r.TryGetValue(PathUtil.Trim(path), out node) ? node : -1;
        }

        // ------------------------------------------------------------------
        // 保存 / 読み込み
        // ------------------------------------------------------------------

        public void Save(string file)
        {
            string tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(0x584A4646); // "FFJX"
                w.Write(FileVersion);
                w.Write(BuiltUtc.Ticks);
                w.Write(Signature);
                w.Write(Truncated);
                w.Write(Roots.Length);
                foreach (string r in Roots) w.Write(r);
                w.Write(Count);
                for (int i = 0; i < Count; i++)
                {
                    w.Write(Parent[i]);
                    w.Write(Flags[i]);
                    w.Write(Mtime[i]);
                    w.Write(Name[i]);
                    bool same = ReferenceEquals(Key[i], Name[i]) || Key[i] == Name[i];
                    w.Write(!same);
                    if (!same) w.Write(Key[i]);
                }
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        public static FileIndex Load(string file)
        {
            if (!File.Exists(file)) return null;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var r = new BinaryReader(fs, Encoding.UTF8))
            {
                if (r.ReadInt32() != 0x584A4646) return null;
                if (r.ReadInt32() != FileVersion) return null;
                var built = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                string sig = r.ReadString();
                bool truncated = r.ReadBoolean();
                var roots = new string[r.ReadInt32()];
                for (int i = 0; i < roots.Length; i++) roots[i] = r.ReadString();
                int n = r.ReadInt32();
                var parent = new int[n];
                var flags = new byte[n];
                var mtime = new long[n];
                var name = new string[n];
                var key = new string[n];
                for (int i = 0; i < n; i++)
                {
                    parent[i] = r.ReadInt32();
                    if (parent[i] >= i) throw new InvalidDataException("index order");
                    flags[i] = r.ReadByte();
                    mtime[i] = r.ReadInt64();
                    name[i] = r.ReadString();
                    key[i] = r.ReadBoolean() ? r.ReadString() : name[i];
                }
                return new FileIndex(n, parent, name, key, mtime, flags, roots, sig, built, true, truncated);
            }
        }

        /// <summary>上位 K 件だけを保持する最小ヒープ。</summary>
        sealed class TopK
        {
            readonly int cap;
            readonly double[] score;
            readonly int[] node;
            int size;

            public TopK(int capacity)
            {
                cap = Math.Max(1, capacity);
                score = new double[cap];
                node = new int[cap];
            }

            public void Offer(double s, int n)
            {
                if (size < cap)
                {
                    int i = size++;
                    score[i] = s; node[i] = n;
                    while (i > 0)
                    {
                        int p = (i - 1) / 2;
                        if (score[p] <= score[i]) break;
                        Swap(i, p); i = p;
                    }
                }
                else if (s > score[0])
                {
                    score[0] = s; node[0] = n;
                    int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < size && score[l] < score[m]) m = l;
                        if (r < size && score[r] < score[m]) m = r;
                        if (m == i) break;
                        Swap(i, m); i = m;
                    }
                }
            }

            void Swap(int a, int b)
            {
                double ts = score[a]; score[a] = score[b]; score[b] = ts;
                int tn = node[a]; node[a] = node[b]; node[b] = tn;
            }

            public List<SearchHit> ToSortedList()
            {
                var list = new List<SearchHit>(size);
                for (int i = 0; i < size; i++) list.Add(new SearchHit { Node = node[i], Score = score[i] });
                list.Sort((a, b) =>
                {
                    int c = b.Score.CompareTo(a.Score);
                    return c != 0 ? c : a.Node.CompareTo(b.Node);
                });
                return list;
            }
        }
    }
}
