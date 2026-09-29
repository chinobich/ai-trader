using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace PhoneBridge
{
    /// <summary>画面のログ欄とログファイル (PhoneBridge.log) の両方に出す。どのスレッドからでも呼べる。</summary>
    internal static class Log
    {
        const long MaxFileBytes = 5L * 1024 * 1024;
        static readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
        static readonly object fileLock = new object();
        static string filePath;

        public static string FilePath { get { return filePath; } }

        public static void Init(string path)
        {
            filePath = path;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    string old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            pending.Enqueue(line);
            string dropped;
            while (pending.Count > 1000) pending.TryDequeue(out dropped);
            if (filePath == null) return;
            lock (fileLock)
            {
                try { File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static bool TryDequeue(out string line)
        {
            return pending.TryDequeue(out line);
        }
    }
}
