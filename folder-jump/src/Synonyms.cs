using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FolderJump
{
    /// <summary>
    /// 表記ゆれ辞書。「見積もり」で「見積」「見積り」「お見積」も探せるようにする。
    /// 加えて、和暦と西暦 (2024 = 令和6 = R6) とカタカナ語末尾の長音 (サーバー = サーバ) も同一視する。
    /// </summary>
    internal sealed class Synonyms
    {
        public static readonly string[] Defaults =
        {
            "見積, 見積り, 見積もり, お見積, 御見積",
            "打合せ, 打ち合わせ, 打合わせ, 打ち合せ, ミーティング, MTG",
            "問合せ, 問い合わせ, 問合わせ, 問い合せ",
            "申込, 申し込み, 申込み",
            "引継ぎ, 引き継ぎ, 引継",
            "取扱, 取り扱い, 取扱い",
            "振込, 振り込み, 振込み",
            "受付, 受け付け, 受付け",
            "売上, 売り上げ, 売上げ",
            "株式会社, (株)",
            "有限会社, (有)",
            "議事録, 会議録, 議事メモ",
            "請求, インボイス, invoice",
            "マニュアル, 手順書, 手引き",
            "ヶ月, ケ月, か月, カ月, ヵ月, 箇月"
        };

        readonly List<string[]> groups = new List<string[]>();
        readonly bool era;

        public Synonyms(IEnumerable<string> lines, bool japaneseEra)
        {
            era = japaneseEra;
            foreach (string line in lines)
            {
                var words = new List<string>();
                foreach (string raw in line.Split(new[] { ',', '、', '，', ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string w = TextNorm.Key(raw.Trim());
                    if (w.Length > 0 && !words.Contains(w)) words.Add(w);
                }
                if (words.Count < 2) continue;
                words.Sort((a, b) => b.Length.CompareTo(a.Length)); // 長い語から当てる
                groups.Add(words.ToArray());
            }
        }

        public int GroupCount { get { return groups.Count; } }

        /// <summary>検索語 1 つを、同じ意味で探すべき語の一覧に広げる (余分な語は省く)。</summary>
        public string[] Expand(string term)
        {
            var set = new List<string> { term };
            foreach (string[] g in groups)
            {
                foreach (string w in g)
                {
                    if (term.IndexOf(w, StringComparison.Ordinal) < 0) continue;
                    foreach (string other in g)
                        if (other != w) Add(set, term.Replace(w, other));
                    break;
                }
            }
            if (era) foreach (string a in EraAlternatives(term)) Add(set, a);
            int n = set.Count;
            for (int i = 0; i < n; i++)
            {
                string lv = DropLongVowels(set[i]);
                if (lv.Length > 0) Add(set, lv);
            }
            return Prune(set);
        }

        static void Add(List<string> set, string s)
        {
            if (s.Length > 0 && !set.Contains(s)) set.Add(s);
        }

        /// <summary>他の候補を含むだけの長い候補は省く (短い方に一致すれば十分なので)。</summary>
        static string[] Prune(List<string> set)
        {
            set.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
            var kept = new List<string>();
            foreach (string s in set)
            {
                bool redundant = false;
                foreach (string k in kept)
                    if (s.IndexOf(k, StringComparison.Ordinal) >= 0) { redundant = true; break; }
                if (!redundant) kept.Add(s);
            }
            return kept.ToArray();
        }

        /// <summary>カナの語の終わりにある長音を取る (さーばー → さーば)。</summary>
        static string DropLongVowels(string s)
        {
            if (s.IndexOf('ー') < 0) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == 'ー' && i > 0 && IsKana(s[i - 1]) && (i + 1 == s.Length || !IsKana(s[i + 1]))) continue;
                sb.Append(c);
            }
            string r = sb.ToString();
            return r == s ? "" : r;
        }

        static bool IsKana(char c)
        {
            return (c >= 'ぁ' && c <= 'ゟ') || (c >= '゠' && c <= 'ヿ');
        }

        static readonly Regex Gregorian = new Regex(@"^(\d{4})(\D.*)?$", RegexOptions.CultureInvariant);
        static readonly Regex Japanese = new Regex(@"^(令和|r|平成|h)(元|\d{1,2})(\D.*)?$", RegexOptions.CultureInvariant);

        /// <summary>2024 → 令和6 / r6 / r06、令和6 → 2024 …</summary>
        public static IEnumerable<string> EraAlternatives(string term)
        {
            var list = new List<string>();
            Match m = Gregorian.Match(term);
            if (m.Success)
            {
                int y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                foreach (string e in EraNames(y)) list.Add(e + m.Groups[2].Value);
                return list;
            }
            m = Japanese.Match(term);
            if (m.Success)
            {
                int n = m.Groups[2].Value == "元" ? 1 : int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                bool reiwa = m.Groups[1].Value == "令和" || m.Groups[1].Value == "r";
                if (n < 1 || (!reiwa && n > 31)) return list;
                int y = (reiwa ? 2018 : 1988) + n;
                string rest = m.Groups[3].Value;
                list.Add(y + rest);
                foreach (string e in EraNames(y)) list.Add(e + rest);
            }
            return list;
        }

        static IEnumerable<string> EraNames(int y)
        {
            var names = new List<string>();
            if (y >= 2019 && y <= 2099)
            {
                int n = y - 2018;
                if (n == 1) names.Add("令和元");
                names.Add("令和" + n);
                names.Add("r" + n);
                if (n < 10) names.Add("r0" + n);
            }
            if (y >= 1989 && y <= 2019)
            {
                int n = y - 1988;
                if (n == 1) names.Add("平成元");
                names.Add("平成" + n);
                names.Add("h" + n);
                if (n < 10) names.Add("h0" + n);
            }
            return names;
        }
    }
}
