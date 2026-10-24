using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

[assembly: AssemblyTitle("FolderJump")]
[assembly: AssemblyProduct("FolderJump")]
[assembly: AssemblyDescription("ファイルサーバーのフォルダに一瞬でジャンプするランチャー")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace FolderJump
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // モニターごとの DPI に追従させる (高 DPI のノート PC + 外部モニターでもくっきり表示)
            try { AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false); }
            catch (Exception) { }

            bool background = args.Any(a => a == "--background" || a == "/background");
            bool created;
            using (var mutex = new Mutex(true, @"Local\FolderJump.Instance", out created))
            {
                if (!created)
                {
                    // すでに起動中なら、そちらのウィンドウを呼び出して終わる
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(@"Local\FolderJump.Show"))
                        {
                            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                            ev.Set();
                        }
                    }
                    catch (Exception) { }
                    return 0;
                }
                var app = new App(background);
                return app.Run();
            }
        }
    }

    internal static class HotkeyParser
    {
        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string raw in text.Split('+'))
            {
                string p = raw.Trim().ToLowerInvariant();
                if (p.Length == 0) continue;
                switch (p)
                {
                    case "ctrl": case "control": mods |= Native.MOD_CONTROL; continue;
                    case "alt": mods |= Native.MOD_ALT; continue;
                    case "shift": mods |= Native.MOD_SHIFT; continue;
                    case "win": case "windows": mods |= Native.MOD_WIN; continue;
                }
                Key key;
                if (p.Length == 1 && p[0] >= '0' && p[0] <= '9') key = Key.D0 + (p[0] - '0');
                else if (p == "esc") key = Key.Escape;
                else if (p == "enter") key = Key.Return;
                else if (!Enum.TryParse(raw.Trim(), true, out key) || char.IsDigit(p[0])) return false;
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            }
            return vk != 0;
        }
    }

    /// <summary>常駐部分: ホットキー、タスクトレイ、スキャン、設定の監視。</summary>
    internal sealed class App : Application, ICommandHost
    {
        const int HotkeyId = 0x4A46;

        readonly bool startHidden;
        Settings config;
        volatile FileIndex index = FileIndex.Empty;
        public readonly UsageStore Usage;
        public readonly QueryEngine Engine;

        LauncherWindow window;
        System.Windows.Forms.NotifyIcon tray;
        HwndSource hwndSource;
        bool hotkeyRegistered;
        ExplorerWatcher watcher;
        ScanJob job;
        DispatcherTimer tick, iniDebounce;
        FileSystemWatcher iniWatcher;
        EventWaitHandle showEvent;
        bool indexLoaded;
        int tickCount;
        string lastOpenedPath;
        DateTime lastOpenedUtc;

        public App(bool startHidden)
        {
            this.startHidden = startHidden;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Usage = new UsageStore(AppPaths.HistoryFile, AppPaths.PinsFile);
            Engine = new QueryEngine(this, Usage);
            DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("ui", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("[fatal] " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("task", e.Exception);
                e.SetObserved();
            };
        }

        public FileIndex Index { get { return index; } }
        public Settings CurrentSettings { get { return config; } }
        public bool IsScanning { get { return job != null && job.IsRunning; } }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            System.Windows.Forms.Application.EnableVisualStyles();
            AppPaths.Ensure();
            bool firstRun = !File.Exists(AppPaths.SettingsFile);
            config = Settings.Load(AppPaths.SettingsFile);
            if (firstRun || NeedsUpgrade(AppPaths.SettingsFile)) TrySave(config); // 新しい項目を設定ファイルに書き足す
            Usage.Load();

            window = new LauncherWindow(this, Theme.From(config));
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            hwndSource = HwndSource.FromHwnd(hwnd);
            hwndSource.AddHook(WndProc);

            CreateTray();
            RegisterHotkey();
            StartShowListener();
            WatchSettingsFile();
            ApplyWatcher();

            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            tick.Tick += delegate { OnTick(); };
            tick.Start();
            UpdateStatus();

            LoadIndexAsync();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s2, e2) =>
                Dispatcher.BeginInvoke(new Action(ApplyAppearance)); // Windows のテーマやアクセント色の変更に追従
            if (!startHidden) window.ShowLauncher(IntPtr.Zero, IntPtr.Zero);
            if (firstRun) Notify("FolderJump を起動しました", config.Hotkey + " でいつでも呼び出せます。タスクトレイに常駐します。");
        }

        // ------------------------------------------------------------------
        // ホットキー
        // ------------------------------------------------------------------

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                if (window.IsVisible && window.IsActive)
                {
                    window.HideLauncher();
                }
                else
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    if (config.SaveDialogJump && FileDialogBridge.IsFileDialog(fg))
                        window.ShowLauncher(IntPtr.Zero, fg); // 「名前を付けて保存」などの画面から呼ばれた
                    else
                        window.ShowLauncher(ExplorerBridge.IsExplorerWindow(fg) ? fg : IntPtr.Zero, IntPtr.Zero);
                }
            }
            return IntPtr.Zero;
        }

        void RegisterHotkey()
        {
            IntPtr hwnd = hwndSource.Handle;
            if (hotkeyRegistered)
            {
                Native.UnregisterHotKey(hwnd, HotkeyId);
                hotkeyRegistered = false;
            }
            uint mods, vk;
            if (!HotkeyParser.TryParse(config.Hotkey, out mods, out vk))
            {
                Notify("ホットキーの指定が読み取れません", "「" + config.Hotkey + "」 → 設定ファイルの Hotkey を確認してください (例: Ctrl+Alt+Space)");
                return;
            }
            hotkeyRegistered = Native.RegisterHotKey(hwnd, HotkeyId, mods | Native.MOD_NOREPEAT, vk);
            if (!hotkeyRegistered)
                Notify("ホットキーを登録できませんでした", config.Hotkey + " は他のアプリが使っています。タスクトレイ → 設定ファイル で変更してください。");
            if (tray != null) tray.Text = Clip("FolderJump  (" + config.Hotkey + ")", 63);
        }

        static string Clip(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // ------------------------------------------------------------------
        // タスクトレイ
        // ------------------------------------------------------------------

        void CreateTray()
        {
            tray = new System.Windows.Forms.NotifyIcon();
            tray.Icon = LoadTrayIcon();
            tray.Text = Clip("FolderJump  (" + config.Hotkey + ")", 63);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("開く", null, delegate { window.ShowLauncher(IntPtr.Zero, IntPtr.Zero); });
            menu.Items.Add("今すぐ再スキャン", null, delegate { Rescan(); });
            menu.Items.Add("フォルダを検索対象に追加…", null, delegate { window.ShowLauncher(IntPtr.Zero, IntPtr.Zero); PickRootFolder(); });
            menu.Items.Add("設定ファイルを開く", null, delegate { OpenSettingsFile(); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("終了", null, delegate { Quit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) window.ShowLauncher(IntPtr.Zero, IntPtr.Zero);
            };
            tray.Visible = true;
        }

        static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FolderJump.ico"))
                    if (s != null) return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
            catch (Exception) { }
            try
            {
                System.Drawing.Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
                if (ic != null) return ic;
            }
            catch (Exception) { }
            return System.Drawing.SystemIcons.Application;
        }

        public void Notify(string title, string text)
        {
            try
            {
                if (tray != null) tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Error("notify", ex);
            }
        }

        void StartShowListener()
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FolderJump.Show");
            var t = new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    Dispatcher.BeginInvoke(new Action(() => window.ShowLauncher(IntPtr.Zero, IntPtr.Zero)));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------------
        // インデックスとスキャン
        // ------------------------------------------------------------------

        async void LoadIndexAsync()
        {
            FileIndex loaded = null;
            try
            {
                loaded = await Task.Run(() => FileIndex.Load(AppPaths.IndexFile));
            }
            catch (Exception ex)
            {
                Log.Error("index load", ex); // 壊れていたら作り直す
            }
            indexLoaded = true;
            if (loaded != null && job == null && config.Roots.Count > 0) SetIndex(loaded);
            MaybeAutoRescan();
            UpdateStatus();
        }

        void SetIndex(FileIndex idx)
        {
            index = idx;
            window.RefreshIfIdle();
        }

        void MaybeAutoRescan()
        {
            if (config.Roots.Count == 0 || IsScanning) return;
            FileIndex idx = index;
            bool stale = idx.Count == 0 || !idx.Complete || idx.Signature != config.ScanSignature
                || (config.RescanHours > 0 && (DateTime.UtcNow - idx.BuiltUtc).TotalHours >= config.RescanHours);
            if (stale) StartScan();
        }

        void StartScan()
        {
            if (job != null && job.IsRunning) job.Cancel();
            job = null;
            if (config.Roots.Count == 0)
            {
                SetIndex(FileIndex.Empty);
                UpdateStatus();
                return;
            }
            var j = new ScanJob(config.Clone());
            job = j;
            j.Completed += (sj, result) => Dispatcher.BeginInvoke(new Action(() => OnScanCompleted(sj, result)));
            j.Start();
            UpdateStatus();
        }

        void OnScanCompleted(ScanJob j, FileIndex result)
        {
            if (j != job) return; // 途中で別のスキャンに置き換わった
            job = null;
            if (result != null)
            {
                SetIndex(result);
                Log.Write("scan done: " + result.Count + " entries, " + j.Errors + " errors, "
                    + (DateTime.UtcNow - j.StartedUtc).TotalSeconds.ToString("F0") + " s");
                Task.Run(() =>
                {
                    try { result.Save(AppPaths.IndexFile); }
                    catch (Exception ex) { Log.Error("index save", ex); }
                });
            }
            UpdateStatus();
        }

        void OnTick()
        {
            tickCount++;
            ScanJob j = job;
            // 初回スキャン中は途中結果でも検索できるようにする
            if (j != null && j.IsRunning && tickCount % 2 == 0 && (!index.Complete || index.Count == 0))
            {
                try { SetIndex(j.Snapshot()); }
                catch (Exception ex) { Log.Error("snapshot", ex); }
            }
            if (tickCount % 400 == 0 && indexLoaded) MaybeAutoRescan(); // 約 10 分ごと
            UpdateStatus();
        }

        void UpdateStatus()
        {
            if (window == null) return;
            FileIndex idx = index;
            ScanJob j = job;
            if (config.Roots.Count == 0)
                window.SetStatus("検索対象が未登録です", false, false);
            else if (j != null && j.IsRunning)
                window.SetStatus("スキャン中…  " + j.Count.ToString("N0") + " 件", true, false);
            else if (!indexLoaded)
                window.SetStatus("インデックスを読み込み中…", false, false);
            else
                window.SetStatus(idx.Count.ToString("N0") + " 件  ·  " + idx.BuiltUtc.ToLocalTime().ToString("M/d HH:mm") + " 更新"
                    + (idx.Truncated ? "  (上限件数に達したため一部のみ)" : ""), false, idx.Count > 0);
        }

        // ------------------------------------------------------------------
        // 開く
        // ------------------------------------------------------------------

        public async void OpenItem(ResultItem item, OpenMode mode, IntPtr previousExplorer, IntPtr previousDialog)
        {
            string path = item.Path;
            if (string.IsNullOrEmpty(path)) return;
            window.HideLauncher();
            int kind = await Task.Run(() => Probe(path)); // 0: なし, 1: ファイル, 2: フォルダ
            if (kind == 0)
            {
                Notify("見つかりませんでした", path + "\n移動・削除されたか、サーバーにつながっていない可能性があります。");
                return;
            }
            bool isDir = kind == 2;
            if (previousDialog != IntPtr.Zero && mode == OpenMode.Open)
            {
                // 保存/開くダイアログをそのフォルダへ移動 (ファイルを選んだときはその場所へ)
                string folder = isDir ? path : PathUtil.Parent(path);
                if (folder == null) return;
                Usage.Record(folder, true);
                bool moved = false;
                try { moved = await FileDialogBridge.NavigateAsync(previousDialog, folder, isDir ? null : PathUtil.LastSegment(path)); }
                catch (Exception ex) { Log.Error("file dialog", ex); }
                if (!moved)
                    Notify("保存画面を移動できませんでした", "画面が閉じられたか、対応していない種類の画面です。Ctrl+Enter でエクスプローラーで開けます。");
                return;
            }
            Usage.Record(path, isDir);
            lastOpenedPath = PathUtil.Trim(path);
            lastOpenedUtc = DateTime.UtcNow;
            bool reuse = config.ReuseExplorer && previousExplorer != IntPtr.Zero;
            try
            {
                if (mode == OpenMode.Reveal || (mode == OpenMode.NewWindow && !isDir))
                {
                    string parent = PathUtil.Parent(path);
                    if (mode == OpenMode.Reveal && reuse && parent != null
                        && ExplorerBridge.TryNavigate(previousExplorer, parent, PathUtil.LastSegment(path), Dispatcher)) return;
                    ExplorerBridge.RevealNew(path);
                }
                else if (isDir)
                {
                    if (mode == OpenMode.Open && reuse && ExplorerBridge.TryNavigate(previousExplorer, path, null, Dispatcher)) return;
                    ExplorerBridge.OpenFolderNew(path);
                }
                else
                {
                    ExplorerBridge.OpenFile(path);
                }
            }
            catch (Exception ex)
            {
                Log.Error("open " + path, ex);
                Notify("開けませんでした", path + "\n" + ex.Message);
            }
        }

        static int Probe(string path)
        {
            try
            {
                string p = path.Length >= 240 ? PathUtil.Extended(path) : path;
                if (Directory.Exists(p)) return 2;
                if (File.Exists(p)) return 1;
            }
            catch (Exception) { }
            return 0;
        }

        void OnExplorerVisit(string path)
        {
            Settings s = config;
            if (!s.IsCovered(path)) return;
            string p = PathUtil.Trim(path);
            if (lastOpenedPath != null && string.Equals(p, lastOpenedPath, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - lastOpenedUtc).TotalSeconds < 20) return; // FolderJump から開いた分は記録済み
            Usage.Record(p, true);
        }

        void ApplyWatcher()
        {
            if (config.LearnFromExplorer)
            {
                if (watcher == null)
                {
                    watcher = new ExplorerWatcher(OnExplorerVisit);
                    watcher.Start();
                }
            }
            else if (watcher != null)
            {
                watcher.Stop();
                watcher = null;
            }
        }

        // ------------------------------------------------------------------
        // 設定ファイル
        // ------------------------------------------------------------------

        void WatchSettingsFile()
        {
            iniDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            iniDebounce.Tick += delegate
            {
                iniDebounce.Stop();
                ReloadSettings();
            };
            try
            {
                iniWatcher = new FileSystemWatcher(AppPaths.DataDir, "FolderJump.ini")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    iniDebounce.Stop();
                    iniDebounce.Start();
                }));
                iniWatcher.Changed += h;
                iniWatcher.Created += h;
                iniWatcher.Renamed += (s, e) => h(s, e);
                iniWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Error("ini watcher", ex);
            }
        }

        void ReloadSettings()
        {
            Settings s;
            try { s = Settings.Load(AppPaths.SettingsFile); }
            catch (Exception ex)
            {
                Log.Error("settings reload", ex);
                return;
            }
            Settings old = config;
            config = s;
            ApplyAppearance();
            if (!string.Equals(old.Hotkey, s.Hotkey, StringComparison.OrdinalIgnoreCase)) RegisterHotkey();
            if (old.LearnFromExplorer != s.LearnFromExplorer) ApplyWatcher();
            if (old.ScanSignature != s.ScanSignature)
            {
                StartScan();
                window.Toast("設定を反映しました。インデックスを作り直しています…");
            }
            UpdateStatus();
        }

        void ApplyAppearance()
        {
            if (window == null) return;
            try { window.ApplyTheme(Theme.From(config)); }
            catch (Exception ex) { Log.Error("theme", ex); }
        }

        /// <summary>以前のバージョンで作った設定ファイルには新しい項目がないので書き足す。</summary>
        static bool NeedsUpgrade(string file)
        {
            try
            {
                string text = File.ReadAllText(file);
                return text.IndexOf("[Synonyms]", StringComparison.OrdinalIgnoreCase) < 0
                    || text.IndexOf("[Appearance]", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        bool TrySave(Settings s)
        {
            try
            {
                s.Save(AppPaths.SettingsFile);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("settings save", ex);
                if (window != null) window.Toast("設定ファイルを保存できませんでした: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // コマンド (ICommandHost)
        // ------------------------------------------------------------------

        public void Rescan()
        {
            if (config.Roots.Count == 0)
            {
                window.Toast("先に検索対象のフォルダを登録してください");
                return;
            }
            StartScan();
            window.Toast("スキャンを開始しました。終わるまでは前回のインデックスで検索できます");
        }

        public void PickRootFolder()
        {
            window.PickFolder(AddRoot);
        }

        public async void AddRoot(string path)
        {
            string root = Settings.NormalizeRoot(path);
            if (root.Length == 0) return;
            if (config.HasRoot(root))
            {
                window.Toast("すでに登録されています: " + root);
                return;
            }
            window.Toast("確認中… " + root);
            bool exists = await Task.Run(() => Probe(root) == 2);
            if (!exists)
            {
                window.Toast("フォルダが見つかりません: " + root);
                return;
            }
            Settings s = config.Clone();
            s.Roots.Add(root);
            config = s;
            TrySave(s);
            StartScan();
            window.Toast("追加しました。スキャンを開始します: " + root);
        }

        public void RemoveRoot(string path)
        {
            Settings s = config.Clone();
            s.Roots.RemoveAll(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
            config = s;
            TrySave(s);
            StartScan();
            window.Toast("検索対象から外しました: " + path);
        }

        public void OpenSettingsFile()
        {
            if (!File.Exists(AppPaths.SettingsFile)) TrySave(config);
            Process.Start("notepad.exe", "\"" + AppPaths.SettingsFile + "\"");
        }

        public void OpenDataFolder()
        {
            Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\"");
        }

        static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "FolderJump.lnk"); }
        }

        public bool IsStartupRegistered { get { return File.Exists(StartupLink); } }

        public void ToggleStartup()
        {
            try
            {
                if (File.Exists(StartupLink))
                {
                    File.Delete(StartupLink);
                    window.Toast("Windows 起動時の自動起動をやめました");
                    return;
                }
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                try
                {
                    object lnk = ExplorerBridge.Call(shell, "CreateShortcut", StartupLink);
                    string exe = Assembly.GetEntryAssembly().Location;
                    SetProp(lnk, "TargetPath", exe);
                    SetProp(lnk, "Arguments", "--background");
                    SetProp(lnk, "WorkingDirectory", Path.GetDirectoryName(exe));
                    SetProp(lnk, "Description", "FolderJump");
                    ExplorerBridge.Call(lnk, "Save");
                    ExplorerBridge.Release(lnk);
                }
                finally
                {
                    ExplorerBridge.Release(shell);
                }
                window.Toast("Windows 起動時に自動で起動するようにしました");
            }
            catch (Exception ex)
            {
                Log.Error("startup", ex);
                window.Toast("自動起動の設定に失敗しました: " + ex.Message);
            }
        }

        static void SetProp(object o, string name, object value)
        {
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });
        }

        public void SetQueryText(string text)
        {
            window.SetQuery(text);
        }

        /// <summary>コマンド (「>見た目」など) から設定を 1 つ変えて保存し、すぐ反映する。</summary>
        public void ChangeSetting(string key, string value)
        {
            Settings s = config.Clone();
            if (!s.Set(key, value)) return;
            config = s;
            TrySave(s);
            ApplyAppearance();
        }

        public void ClearHistory()
        {
            Usage.ClearHistory();
            window.Toast("履歴をクリアしました");
        }

        public void Quit()
        {
            try { if (job != null) job.Cancel(); }
            catch (Exception) { }
            if (watcher != null) watcher.Stop();
            if (hotkeyRegistered) Native.UnregisterHotKey(hwndSource.Handle, HotkeyId);
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            window.Exit();
            Shutdown();
        }
    }
}
