using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PhoneBridge
{
    internal static class Program
    {
        public const string Version = "1.0.0";

        // 使い方:
        //   PhoneBridge.exe                  画面を開く
        //   PhoneBridge.exe /start           画面を開いてすぐ開始する
        //   PhoneBridge.exe /start /min      最小化した状態で開始する（スタートアップ登録向け）
        //   PhoneBridge.exe /selftest [log]  音声処理の自己テスト（機器は使わない）
        //   PhoneBridge.exe /devicetest [log] VB-CABLE を使った実機テスト
        [STAThread]
        static int Main(string[] args)
        {
            string selfTestLog = ArgValue(args, "/selftest");
            if (selfTestLog != null) return SelfTest.Run(selfTestLog);
            string deviceTestLog = ArgValue(args, "/devicetest");
            if (deviceTestLog != null) return SelfTest.RunDeviceTest(deviceTestLog);
            return RunGui(HasArg(args, "/start"), HasArg(args, "/min"));
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>「/name 値」の値。値が無ければ既定のログファイル。name 自体が無ければ null。</summary>
        static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("/")) return args[i + 1];
                return Path.Combine(Settings.DataDir(), name.TrimStart('/') + ".log");
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunGui(bool startNow, bool minimized)
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\PhoneBridge.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("電話音声ブリッジはすでに起動しています。", "PhoneBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                try { SetProcessDPIAware(); }
                catch (Exception) { }

                string dataDir = Settings.DataDir();
                Log.Init(Path.Combine(dataDir, "PhoneBridge.log"));
                Log.Write("起動しました v" + Version + " / " + Environment.OSVersion + " / " + (Environment.Is64BitProcess ? "64bit" : "32bit"));
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("画面の処理で例外：" + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("予期しない例外：" + e.ExceptionObject);
                Application.Run(new MainForm(dataDir, startNow, minimized));
                Log.Write("終了しました");
            }
            return 0;
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }
}
