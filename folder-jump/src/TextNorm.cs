using System;
using System.Collections.Generic;
using System.Text;

namespace FolderJump
{
    /// <summary>
    /// 検索用の文字列正規化。
    /// 全角/半角 (ＡＢＣ→abc, ｶﾅ→カナ)、大文字/小文字、カタカナ/ひらがな の違いを吸収する。
    /// </summary>
    internal static class TextNorm
    {
        public static string Key(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            bool ascii = true, upper = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x80) { ascii = false; break; }
                if (c >= 'A' && c <= 'Z') upper = true;
            }
            if (ascii) return upper ? s.ToLowerInvariant() : s;

            string n = s;
            try
            {
                if (!n.IsNormalized(NormalizationForm.FormKC)) n = n.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                // 不正なサロゲートを含む名前は正規化せずに続行
            }

            char[] buf = null;
            for (int i = 0; i < n.Length; i++)
            {
                char c = n[i];
                char f = Fold(c);
                if (f != c)
                {
                    if (buf == null) buf = n.ToCharArray();
                    buf[i] = f;
                }
            }
            string result = buf == null ? n : new string(buf);
            // 変化がなければ元の文字列インスタンスを使い回してメモリを節約する
            return string.Equals(result, s, StringComparison.Ordinal) ? s : result;
        }

        static char Fold(char c)
        {
            if (c >= 'A' && c <= 'Z') return (char)(c + 32);
            if (c < 0x80) return c;
            if ((c >= 'ァ' && c <= 'ヶ') || c == 'ヽ' || c == 'ヾ') return (char)(c - 0x60); // カタカナ → ひらがな
            if (c == '〜') return '~'; // 波ダッシュ
            if (char.IsUpper(c)) return char.ToLowerInvariant(c);
            return c;
        }
    }

    /// <summary>貼り付けられた文字列がパスなら取り出す。</summary>
    internal static class PathInput
    {
        public static string TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string s = text.Trim().Trim('"', '\'', '<', '>', '「', '」', '“', '”').Trim();
            if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { s = new Uri(s).LocalPath; }
                catch (UriFormatException) { return null; }
            }
            if (s.Length > 2 && s.StartsWith(@"\\") && s[2] != '\\' && s[2] != '?')
                return PathUtil.Trim(s.Replace('/', '\\'));
            if (s.Length >= 3 && IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
                return PathUtil.Trim(s.Replace('/', '\\'));
            return null;
        }

        static bool IsAsciiLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }
    }
}
