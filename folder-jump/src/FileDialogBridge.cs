using System;
using System.Text;
using System.Threading.Tasks;

namespace FolderJump
{
    /// <summary>
    /// 「名前を付けて保存」「開く」画面 (Windows 標準のファイルダイアログ) を外から操作する。
    /// ファイル名の欄にフォルダのパスを入れて Enter を押すと、ダイアログはそのフォルダへ移動する。
    /// 移動したら、もともと入っていたファイル名を元に戻す。
    /// </summary>
    internal static class FileDialogBridge
    {
        // ファイル名欄のコントロール ID (1148: 開く, 1001: 名前を付けて保存, 1152: 古い形式)
        static readonly int[] FileNameIds = { 0x47C, 0x3E9, 0x480 };
        const uint TimeoutMs = 1500;

        /// <summary>ファイル一覧とファイル名欄を持つ標準のファイルダイアログか。</summary>
        public static bool IsFileDialog(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || Native.ClassNameOf(hwnd) != "#32770") return false;
            return FindFileNameEdit(hwnd) != IntPtr.Zero;
        }

        public static string TitleOf(IntPtr dlg)
        {
            string t = Native.TextOf(dlg);
            return string.IsNullOrEmpty(t) ? "ファイルの保存/選択" : t;
        }

        static IntPtr FindFileNameEdit(IntPtr dlg)
        {
            IntPtr best = IntPtr.Zero;
            int bestRank = int.MaxValue;
            bool hasFileView = false;
            Native.EnumChildWindows(dlg, (h, l) =>
            {
                string cls = Native.ClassNameOf(h);
                if (cls == "DUIViewWndClassName" || cls == "SHELLDLL_DefView") hasFileView = true;
                if (cls != "Edit" || !Native.IsWindowVisible(h)) return true;
                int depth = 0;
                for (IntPtr c = h; c != IntPtr.Zero && c != dlg && depth < 4; c = Native.GetParent(c), depth++)
                {
                    int rank = Array.IndexOf(FileNameIds, Native.GetDlgCtrlID(c));
                    if (rank >= 0)
                    {
                        if (rank < bestRank) { best = h; bestRank = rank; }
                        break;
                    }
                }
                return true;
            }, IntPtr.Zero);
            // ほかのダイアログの入力欄を誤って操作しないよう、ファイル一覧があるものだけを対象にする
            return hasFileView ? best : IntPtr.Zero;
        }

        /// <summary>
        /// ダイアログを folder へ移動させる。ファイル名の欄が空なら fileNameIfEmpty を入れておく
        /// (「開く」画面でファイルを選んだとき、あとは Enter を押すだけになるように)。
        /// </summary>
        public static async Task<bool> NavigateAsync(IntPtr dlg, string folder, string fileNameIfEmpty)
        {
            if (!Native.IsWindow(dlg)) return false;
            IntPtr edit = FindFileNameEdit(dlg);
            if (edit == IntPtr.Zero) return false;
            string old = GetText(edit);
            if (old == null) return false; // 応答がない、または管理者として動いているアプリ

            if (Native.IsIconic(dlg)) Native.ShowWindow(dlg, Native.SW_RESTORE);
            Native.SetForegroundWindow(dlg);
            if (!SetText(edit, folder)) return false;
            PressEnter(edit);
            await Task.Delay(300);
            if (!Native.IsWindow(dlg)) return true;

            string now = GetText(edit);
            if (now != null && string.Equals(now, folder, StringComparison.OrdinalIgnoreCase))
            {
                // Enter が届かない画面向け: 「保存」「開く」ボタンを押したのと同じ通知を送る
                IntPtr r;
                Native.SendMessageTimeout(dlg, Native.WM_COMMAND, new IntPtr(Native.IDOK), Native.GetDlgItem(dlg, Native.IDOK),
                    Native.SMTO_ABORTIFHUNG, TimeoutMs, out r);
                await Task.Delay(250);
            }
            if (!Native.IsWindow(dlg) || !Native.IsWindow(edit)) return true;

            string restore = old.Length > 0 ? old : (fileNameIfEmpty ?? "");
            SetText(edit, restore);
            IntPtr sel;
            Native.SendMessageTimeout(edit, Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1), Native.SMTO_ABORTIFHUNG, TimeoutMs, out sel);
            return true;
        }

        static void PressEnter(IntPtr edit)
        {
            Native.PostMessage(edit, Native.WM_KEYDOWN, new IntPtr(Native.VK_RETURN), new IntPtr(0x001C0001));
            Native.PostMessage(edit, Native.WM_KEYUP, new IntPtr(Native.VK_RETURN), new IntPtr(unchecked((int)0xC01C0001)));
        }

        static string GetText(IntPtr h)
        {
            IntPtr len;
            if (Native.SendMessageTimeout(h, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, TimeoutMs, out len) == IntPtr.Zero)
                return null;
            var sb = new StringBuilder(Math.Max(0, len.ToInt32()) + 2);
            IntPtr r;
            if (Native.SendMessageTimeout(h, Native.WM_GETTEXT, new IntPtr(sb.Capacity), sb, Native.SMTO_ABORTIFHUNG, TimeoutMs, out r) == IntPtr.Zero)
                return null;
            return sb.ToString();
        }

        static bool SetText(IntPtr h, string text)
        {
            IntPtr r;
            return Native.SendMessageTimeout(h, Native.WM_SETTEXT, IntPtr.Zero, text, Native.SMTO_ABORTIFHUNG, TimeoutMs, out r) != IntPtr.Zero;
        }
    }
}
