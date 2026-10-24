using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace FolderJump
{
    /// <summary>除外ルール (完全一致 or * ? のワイルドカード、大文字小文字は区別しない)。</summary>
    internal sealed class ExcludeMatcher
    {
        readonly HashSet<string> exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<Regex> patterns = new List<Regex>();

        public ExcludeMatcher(IEnumerable<string> rules)
        {
            foreach (string raw in rules)
            {
                string r = raw.Trim();
                if (r.Length == 0) continue;
                if (r.IndexOf('*') >= 0 || r.IndexOf('?') >= 0)
                {
                    string re = "^" + Regex.Escape(r).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                    patterns.Add(new Regex(re, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
                else exact.Add(r);
            }
        }

        public bool IsMatch(string name)
        {
            if (exact.Contains(name)) return true;
            for (int i = 0; i < patterns.Count; i++)
                if (patterns[i].IsMatch(name)) return true;
            return false;
        }
    }

    /// <summary>
    /// ファイルサーバーを複数スレッドで並列にスキャンしてインデックスを作る。
    /// ネットワーク越しの列挙は「待ち時間」が支配的なので、並列化がよく効く。
    /// </summary>
    internal sealed class ScanJob
    {
        const int MaxDepth = 64;

        struct Work
        {
            public int Node;
            public string Path;
            public int Depth;
        }

        struct Entry
        {
            public string Name;
            public string Key;
            public long Mtime;
            public bool IsDir;
            public bool Descend;
        }

        readonly Settings settings;
        readonly ExcludeMatcher exclude;
        readonly string[] roots;
        readonly object qlock = new object();
        readonly object blk = new object();
        readonly Queue<Work> queue = new Queue<Work>();
        int active;
        int liveWorkers;
        volatile bool cancel;
        bool cancelledByUser;

        readonly List<int> parent = new List<int>(1 << 16);
        readonly List<string> names = new List<string>(1 << 16);
        readonly List<string> keys = new List<string>(1 << 16);
        readonly List<long> mtimes = new List<long>(1 << 16);
        readonly List<byte> flags = new List<byte>(1 << 16);

        int errors;
        bool truncated;

        public readonly string Signature;
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        public bool IsRunning { get; private set; }
        public int Errors { get { return Volatile.Read(ref errors); } }
        public bool IsTruncated { get { return truncated; } }

        public int Count
        {
            get { lock (blk) return names.Count; }
        }

        /// <summary>スキャン完了時 (ワーカースレッド上で呼ばれる)。中止された場合は null。</summary>
        public event Action<ScanJob, FileIndex> Completed;

        public ScanJob(Settings s)
        {
            settings = s;
            roots = s.Roots.ToArray();
            exclude = new ExcludeMatcher(s.Exclude);
            Signature = s.ScanSignature;
        }

        public void Start()
        {
            IsRunning = true;
            lock (blk)
            {
                foreach (string root in roots)
                {
                    int idx = AddLocked(-1, root, TextNorm.Key(root), 0, (byte)(FileIndex.FlagDir | FileIndex.FlagRoot));
                    queue.Enqueue(new Work { Node = idx, Path = root, Depth = 0 });
                }
            }
            int n = Math.Max(1, Math.Min(32, settings.ScanThreads));
            liveWorkers = n;
            for (int i = 0; i < n; i++)
            {
                var t = new Thread(WorkerLoop);
                t.IsBackground = true;
                t.Priority = ThreadPriority.BelowNormal;
                t.Name = "FolderJump scan " + i;
                t.Start();
            }
        }

        public void Cancel()
        {
            cancelledByUser = true;
            cancel = true;
            lock (qlock) Monitor.PulseAll(qlock);
        }

        int AddLocked(int p, string name, string key, long mtime, byte flag)
        {
            int idx = names.Count;
            parent.Add(p);
            names.Add(name);
            keys.Add(key);
            mtimes.Add(mtime);
            flags.Add(flag);
            return idx;
        }

        void WorkerLoop()
        {
            var buf = new List<Entry>(256);
            try
            {
                while (true)
                {
                    Work w;
                    lock (qlock)
                    {
                        while (queue.Count == 0 && active > 0 && !cancel) Monitor.Wait(qlock);
                        if (cancel || queue.Count == 0)
                        {
                            Monitor.PulseAll(qlock);
                            break;
                        }
                        w = queue.Dequeue();
                        active++;
                    }

                    buf.Clear();
                    try
                    {
                        Enumerate(w.Path, buf);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref errors); // アクセス権がない等は飛ばす
                    }

                    List<Work> sub = null;
                    lock (blk)
                    {
                        for (int i = 0; i < buf.Count; i++)
                        {
                            if (names.Count >= settings.MaxEntries)
                            {
                                truncated = true;
                                cancel = true;
                                break;
                            }
                            Entry e = buf[i];
                            int idx = AddLocked(w.Node, e.Name, e.Key, e.Mtime, e.IsDir ? FileIndex.FlagDir : (byte)0);
                            if (e.IsDir && e.Descend && w.Depth < MaxDepth)
                            {
                                if (sub == null) sub = new List<Work>();
                                sub.Add(new Work { Node = idx, Path = PathUtil.Join(w.Path, e.Name), Depth = w.Depth + 1 });
                            }
                        }
                    }

                    lock (qlock)
                    {
                        if (sub != null && !cancel)
                            foreach (Work s in sub) queue.Enqueue(s);
                        active--;
                        Monitor.PulseAll(qlock);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("scan worker", ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref liveWorkers) == 0) Finish();
            }
        }

        void Finish()
        {
            IsRunning = false;
            FileIndex result = null;
            if (!cancelledByUser)
            {
                try { result = Build(true); }
                catch (Exception ex) { Log.Error("scan build", ex); }
            }
            var h = Completed;
            if (h != null) h(this, result);
        }

        /// <summary>スキャン途中の状態をインデックスとして取り出す (初回スキャン中も検索できるように)。</summary>
        public FileIndex Snapshot()
        {
            return Build(false);
        }

        FileIndex Build(bool complete)
        {
            lock (blk)
            {
                int n = names.Count;
                return new FileIndex(n, parent.ToArray(), names.ToArray(), keys.ToArray(), mtimes.ToArray(), flags.ToArray(),
                    roots, Signature, DateTime.UtcNow, complete, truncated);
            }
        }

        // ------------------------------------------------------------------

        void Enumerate(string dir, List<Entry> output)
        {
            if (Native.IsWindows) EnumerateWin32(dir, output);
            else EnumerateManaged(dir, output);
        }

        void EnumerateWin32(string dir, List<Entry> output)
        {
            Native.WIN32_FIND_DATAW fd;
            string pattern = PathUtil.Join(PathUtil.Extended(dir), "*");
            IntPtr h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out fd, Native.FindExSearchNameMatch,
                IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                Interlocked.Increment(ref errors);
                return;
            }
            try
            {
                do
                {
                    string name = fd.cFileName;
                    if (name == "." || name == "..") continue;
                    uint attr = fd.dwFileAttributes;
                    bool isDir = (attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                    if (!isDir && !settings.IncludeFiles) continue;
                    if (settings.SkipHidden && (attr & Native.FILE_ATTRIBUTE_HIDDEN) != 0) continue;
                    if (exclude.IsMatch(name)) continue;

                    bool descend = isDir;
                    if (isDir && (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && !settings.FollowLinks)
                    {
                        // ジャンクション/シンボリックリンクは無限ループ防止のため中に入らない
                        // (DFS のリンクなどその他の種類は普通のフォルダとして扱う)
                        uint tag = fd.dwReserved0;
                        if (tag == Native.IO_REPARSE_TAG_MOUNT_POINT || tag == Native.IO_REPARSE_TAG_SYMLINK) descend = false;
                    }
                    output.Add(new Entry
                    {
                        Name = name,
                        Key = TextNorm.Key(name),
                        Mtime = ((long)fd.ftLastWriteTimeHigh << 32) | fd.ftLastWriteTimeLow,
                        IsDir = isDir,
                        Descend = descend
                    });
                } while (!cancel && Native.FindNextFileW(h, out fd));
            }
            finally
            {
                Native.FindClose(h);
            }
        }

        void EnumerateManaged(string dir, List<Entry> output)
        {
            var di = new DirectoryInfo(dir);
            foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
            {
                if (cancel) break;
                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                string name = fsi.Name;
                if (!isDir && !settings.IncludeFiles) continue;
                if (settings.SkipHidden && ((fsi.Attributes & FileAttributes.Hidden) != 0 || name.StartsWith("."))) continue;
                if (exclude.IsMatch(name)) continue;
                bool link = (fsi.Attributes & FileAttributes.ReparsePoint) != 0;
                long mt = 0;
                try { mt = fsi.LastWriteTimeUtc.ToFileTimeUtc(); }
                catch (ArgumentOutOfRangeException) { }
                output.Add(new Entry
                {
                    Name = name,
                    Key = TextNorm.Key(name),
                    Mtime = mt,
                    IsDir = isDir,
                    Descend = isDir && (!link || settings.FollowLinks)
                });
            }
        }
    }
}
