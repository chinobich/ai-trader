using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace FolderJump
{
    /// <summary>エクスプローラーの操作 (既存ウィンドウ/タブの移動、選択、新規ウィンドウ)。</summary>
    internal static class ExplorerBridge
    {
        static Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");

        public static bool IsExplorerWindow(IntPtr hwnd)
        {
            return Native.ClassNameOf(hwnd) == "CabinetWClass";
        }

        public static void OpenFolderNew(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
        }

        public static void RevealNew(string path)
        {
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        public static void OpenFile(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        /// <summary>
        /// 直前にアクティブだったエクスプローラー (Windows 11 のタブも考慮) を folder に移動させる。
        /// selectName を渡すと移動後にその項目を選択する。
        /// </summary>
        public static bool TryNavigate(IntPtr explorerHwnd, string folder, string selectName, Dispatcher dispatcher)
        {
            if (explorerHwnd == IntPtr.Zero || !IsExplorerWindow(explorerHwnd)) return false;
            object shell = null, windows = null, target = null;
            try
            {
                shell = CreateShell();
                windows = Call(shell, "Windows");
                int count = Convert.ToInt32(Get(windows, "Count"));
                IntPtr activeTab = Native.FindWindowEx(explorerHwnd, IntPtr.Zero, "ShellTabWindowClass", null);
                for (int i = 0; i < count && target == null; i++)
                {
                    object w = null;
                    try { w = Call(windows, "Item", i); }
                    catch (Exception) { }
                    if (w == null) continue;
                    bool match = false;
                    try
                    {
                        var h = new IntPtr(Convert.ToInt64(Get(w, "HWND")));
                        if (h == explorerHwnd) match = activeTab == IntPtr.Zero || TabHandle(w) == activeTab;
                    }
                    catch (Exception) { }
                    if (match) target = w;
                    else Release(w);
                }
                if (target == null) return false;

                Call(target, "Navigate2", folder);
                if (Native.IsIconic(explorerHwnd)) Native.ShowWindow(explorerHwnd, Native.SW_RESTORE);
                Native.SetForegroundWindow(explorerHwnd);
                if (selectName != null && dispatcher != null)
                {
                    SelectWhenReady(target, folder, selectName, dispatcher);
                    target = null; // 解放は SelectWhenReady 側で行う
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("navigate explorer", ex);
                return false;
            }
            finally
            {
                Release(target);
                Release(windows);
                Release(shell);
            }
        }

        /// <summary>移動が終わるのを待ってからファイルを選択状態にする。</summary>
        static void SelectWhenReady(object window, string folder, string name, Dispatcher dispatcher)
        {
            int tries = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += delegate
            {
                tries++;
                bool done = tries > 30;
                object doc = null, fld = null, self = null, item = null;
                try
                {
                    doc = Get(window, "Document");
                    fld = doc == null ? null : Get(doc, "Folder");
                    self = fld == null ? null : Get(fld, "Self");
                    string p = self == null ? null : Get(self, "Path") as string;
                    if (p != null && string.Equals(PathUtil.Trim(p), PathUtil.Trim(folder), StringComparison.OrdinalIgnoreCase))
                    {
                        item = Call(fld, "ParseName", name);
                        if (item != null)
                        {
                            // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
                            Call(doc, "SelectItem", item, 1 | 4 | 8 | 16);
                            done = true;
                        }
                    }
                }
                catch (Exception)
                {
                    // まだ読み込み中。次のタイミングで再試行
                }
                finally
                {
                    Release(item); Release(self); Release(fld); Release(doc);
                }
                if (done)
                {
                    timer.Stop();
                    Release(window);
                }
            };
            timer.Start();
        }

        /// <summary>そのウィンドウ (タブ) 固有のハンドル。Windows 11 のタブを見分けるのに使う。</summary>
        public static IntPtr TabHandle(object shellWindow)
        {
            var sp = shellWindow as IOleServiceProvider;
            if (sp == null) return IntPtr.Zero;
            Guid sid = IID_IShellBrowser, iid = IID_IShellBrowser;
            IntPtr ppv;
            if (sp.QueryService(ref sid, ref iid, out ppv) != 0 || ppv == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var browser = (IShellBrowserWindow)Marshal.GetObjectForIUnknown(ppv);
                IntPtr tab;
                return browser.GetWindow(out tab) == 0 ? tab : IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(ppv);
            }
        }

        /// <summary>エクスプローラーのウィンドウが今表示しているフォルダのパス。</summary>
        public static string FolderOf(object shellWindow)
        {
            object doc = null, fld = null, self = null;
            try
            {
                doc = Get(shellWindow, "Document");
                if (doc == null) return null;
                fld = Get(doc, "Folder");
                if (fld == null) return null;
                self = Get(fld, "Self");
                if (self == null) return null;
                string p = Get(self, "Path") as string;
                if (string.IsNullOrEmpty(p) || p.StartsWith("::")) return null;
                return p;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Release(self); Release(fld); Release(doc);
            }
        }

        public static object CreateShell()
        {
            return Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
        }

        public static object Get(object o, string name)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
        }

        public static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        public static void Release(object o)
        {
            if (o == null || !Marshal.IsComObject(o)) return;
            try { Marshal.ReleaseComObject(o); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// エクスプローラーで普通に開いたフォルダを数秒おきに観察して「よく使う」に学習させる。
    /// </summary>
    internal sealed class ExplorerWatcher
    {
        readonly Action<string> onVisit;
        readonly Dictionary<string, string> last = new Dictionary<string, string>();
        Thread thread;
        volatile bool stop;

        public ExplorerWatcher(Action<string> onVisit)
        {
            this.onVisit = onVisit;
        }

        public void Start()
        {
            if (thread != null) return;
            stop = false;
            thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "FolderJump explorer watcher" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            thread = null;
        }

        void Loop()
        {
            object shell = null;
            bool first = true;
            while (!stop)
            {
                try
                {
                    if (shell == null) shell = ExplorerBridge.CreateShell();
                    Poll(shell, first);
                    first = false;
                }
                catch (Exception)
                {
                    ExplorerBridge.Release(shell);
                    shell = null;
                }
                for (int i = 0; i < 30 && !stop; i++) Thread.Sleep(100);
            }
            ExplorerBridge.Release(shell);
        }

        void Poll(object shell, bool first)
        {
            var seen = new HashSet<string>();
            object windows = ExplorerBridge.Call(shell, "Windows");
            try
            {
                int count = Convert.ToInt32(ExplorerBridge.Get(windows, "Count"));
                for (int i = 0; i < count; i++)
                {
                    object w = null;
                    try
                    {
                        w = ExplorerBridge.Call(windows, "Item", i);
                        if (w == null) continue;
                        string path = ExplorerBridge.FolderOf(w);
                        if (path == null) continue;
                        string id = Convert.ToInt64(ExplorerBridge.Get(w, "HWND")) + ":" + ExplorerBridge.TabHandle(w).ToInt64();
                        seen.Add(id);
                        string prev;
                        if (!last.TryGetValue(id, out prev) || !string.Equals(prev, path, StringComparison.OrdinalIgnoreCase))
                        {
                            last[id] = path;
                            if (!first) onVisit(path);
                        }
                    }
                    catch (Exception)
                    {
                        // 閉じかけのウィンドウなどは無視
                    }
                    finally
                    {
                        ExplorerBridge.Release(w);
                    }
                }
            }
            finally
            {
                ExplorerBridge.Release(windows);
            }
            var gone = new List<string>();
            foreach (string id in last.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (string id in gone) last.Remove(id);
        }
    }
}
