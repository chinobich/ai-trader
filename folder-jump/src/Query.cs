using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FolderJump
{
    /// <summary>
    /// 入力された検索語。
    ///   見積 2024     スペース区切りで AND (親フォルダの名前も対象)
    ///   -旧           除外
    ///   .xlsx         拡張子 (.xls で .xlsx/.xlsm も)。.* でファイルだけ
    ///   / または 語/  フォルダだけ
    ///   @今週 @7日 @2024-05 @2024年度 @r6 @2024以降   更新日
    /// </summary>
    internal sealed class Query
    {
        public const int KindAll = 0, KindFolders = 1, KindFiles = 2;

        public string[] Terms = new string[0];      // 入力どおりの語 (正規化済み)
        public string[][] Alts = new string[0][];   // 各語の言い換え (表記ゆれ・和暦を含む)
        public string[] Excludes = new string[0];
        public string[] Exts = new string[0];
        public int Kind;
        public long MinTime, MaxTime;               // 更新日時の範囲 (FILETIME UTC、0 は指定なし)
        public bool PreferFolders;                  // 保存ダイアログから呼ばれたときはフォルダを優先
        public string DateLabel;

        public bool HasFilters { get { return Exts.Length > 0 || Kind != KindAll || MinTime > 0 || MaxTime > 0; } }
        public bool IsEmpty { get { return Terms.Length == 0 && !HasFilters; } }

        /// <summary>絞り込み条件の説明 (例: 「.xlsx · 今週」)。</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Kind == KindFolders) parts.Add("フォルダのみ");
            else if (Kind == KindFiles && Exts.Length == 0) parts.Add("ファイルのみ");
            if (Exts.Length > 0) parts.Add("." + string.Join(" .", Exts));
            if (DateLabel != null) parts.Add(DateLabel);
            return string.Join("  ·  ", parts);
        }

        public static Query Parse(string text)
        {
            return Parse(text, null, DateTime.Now);
        }

        public static Query Parse(string text, Synonyms synonyms, DateTime now)
        {
            var q = new Query();
            if (string.IsNullOrEmpty(text)) return q;
            string norm = TextNorm.Key(text);
            var terms = new List<string>();
            var excl = new List<string>();
            var exts = new List<string>();
            foreach (string tok in norm.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok == "-") continue;
                if (tok == "/" || tok == "\\") { q.Kind = KindFolders; continue; }
                if (tok.Length > 1 && tok[0] == '@' && q.TryDate(tok.Substring(1), now)) continue;
                if (ExtToken.IsMatch(tok))
                {
                    string e = tok.Substring(1);
                    if (e == "*") q.Kind = KindFiles;
                    else if (!exts.Contains(e)) exts.Add(e);
                    continue;
                }
                string body = tok;
                bool exclude = body.Length > 1 && body[0] == '-';
                if (exclude) body = body.Substring(1);
                char last = body[body.Length - 1];
                if (!exclude && body.Length > 1 && (last == '/' || last == '\\')) q.Kind = KindFolders; // 「営業/」はフォルダだけ
                foreach (string part in body.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    List<string> target = exclude ? excl : terms;
                    if (!target.Contains(part)) target.Add(part);
                }
                if (terms.Count >= 20) break;
            }
            if (exts.Count > 0 && q.Kind == KindFolders) q.Kind = KindAll; // 拡張子の指定を優先
            q.Terms = terms.ToArray();
            q.Exts = exts.ToArray();
            q.Alts = new string[q.Terms.Length][];
            for (int i = 0; i < q.Terms.Length; i++)
                q.Alts[i] = synonyms != null ? synonyms.Expand(q.Terms[i]) : new[] { q.Terms[i] };
            var flat = new List<string>();
            foreach (string e in excl)
                foreach (string a in synonyms != null ? synonyms.Expand(e) : new[] { e })
                    if (!flat.Contains(a)) flat.Add(a);
            q.Excludes = flat.ToArray();
            return q;
        }

        static readonly Regex ExtToken = new Regex(@"^\.([a-z0-9]{1,10}|\*)$", RegexOptions.CultureInvariant);

        // ------------------------------------------------------------------
        // 更新日の指定
        // ------------------------------------------------------------------

        static readonly Regex Relative = new Regex(
            @"^(\d{1,3})(日|日間|d|days?|週|週間|w|weeks?|[かゖゕけ箇]月|か月間|m|months?|年|年間|y|years?)$", RegexOptions.CultureInvariant);
        static readonly Regex FiscalYear = new Regex(@"^(\d{4})年度$", RegexOptions.CultureInvariant);
        static readonly Regex Year = new Regex(@"^(\d{4})年?$", RegexOptions.CultureInvariant);
        static readonly Regex Month = new Regex(@"^(\d{4})(?:[-/.年](\d{1,2})月?|(\d{2}))$", RegexOptions.CultureInvariant);
        static readonly Regex Day = new Regex(@"^(\d{4})(?:[-/.年](\d{1,2})[-/.月](\d{1,2})日?|(\d{2})(\d{2}))$", RegexOptions.CultureInvariant);
        static readonly Regex MonthOfYear = new Regex(@"^(\d{1,2})月$", RegexOptions.CultureInvariant);
        static readonly Regex Era = new Regex(@"^(令和|r|平成|h)(元|\d{1,2})年?(度)?$", RegexOptions.CultureInvariant);

        bool TryDate(string s, DateTime now)
        {
            bool openEnd = false, openStart = false;
            foreach (string suf in new[] { "以降", "から", "以後" })
                if (s.Length > suf.Length && s.EndsWith(suf, StringComparison.Ordinal)) { openEnd = true; s = s.Substring(0, s.Length - suf.Length); break; }
            foreach (string suf in new[] { "以前", "まで" })
                if (s.Length > suf.Length && s.EndsWith(suf, StringComparison.Ordinal)) { openStart = true; s = s.Substring(0, s.Length - suf.Length); break; }

            DateTime today = now.Date;
            DateTime from, to;
            string label;
            if (!Resolve(s, now, today, out from, out to, out label)) return false;
            if (openEnd) { to = DateTime.MaxValue; label += " 以降"; }
            if (openStart) { from = DateTime.MinValue; label += " 以前"; }

            long min = from == DateTime.MinValue ? 0 : Ft(from);
            long max = to == DateTime.MaxValue ? 0 : Ft(to);
            if (min > MinTime) MinTime = min;
            if (max > 0 && (MaxTime == 0 || max < MaxTime)) MaxTime = max;
            DateLabel = DateLabel == null ? label : DateLabel + " / " + label;
            return true;
        }

        static bool Resolve(string s, DateTime now, DateTime today, out DateTime from, out DateTime to, out string label)
        {
            from = DateTime.MinValue;
            to = DateTime.MaxValue;
            label = s;
            DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            DateTime month1 = new DateTime(today.Year, today.Month, 1);
            DateTime jan1 = new DateTime(today.Year, 1, 1);
            switch (s)
            {
                case "今日": case "きょう": case "today": from = today; to = today.AddDays(1); label = "今日"; return true;
                case "昨日": case "きのう": case "yesterday": from = today.AddDays(-1); to = today; label = "昨日"; return true;
                case "今週": case "こんしゅう": case "week": case "thisweek": from = monday; label = "今週"; return true;
                case "先週": case "せんしゅう": case "lastweek": from = monday.AddDays(-7); to = monday; label = "先週"; return true;
                case "今月": case "こんげつ": case "month": case "thismonth": from = month1; label = "今月"; return true;
                case "先月": case "せんげつ": case "lastmonth": from = month1.AddMonths(-1); to = month1; label = "先月"; return true;
                case "今年": case "ことし": case "year": case "thisyear": from = jan1; label = "今年"; return true;
                case "去年": case "昨年": case "きょねん": case "さくねん": case "lastyear": from = jan1.AddYears(-1); to = jan1; label = "去年"; return true;
            }

            Match m = Relative.Match(s);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string u = m.Groups[2].Value;
                if (n <= 0) return false;
                if (u[0] == '日' || u[0] == 'd') { from = now.AddDays(-n); label = "直近" + n + "日"; }
                else if (u[0] == '週' || u[0] == 'w') { from = now.AddDays(-7 * n); label = "直近" + n + "週間"; }
                else if (u[0] == '年' || u[0] == 'y') { from = now.AddYears(-n); label = "直近" + n + "年"; }
                else { from = now.AddMonths(-n); label = "直近" + n + "か月"; }
                return true;
            }

            m = FiscalYear.Match(s);
            if (m.Success) return FiscalRange(Int(m.Groups[1]), out from, out to, out label);

            m = Era.Match(s);
            if (m.Success)
            {
                int n = m.Groups[2].Value == "元" ? 1 : Int(m.Groups[2]);
                bool reiwa = m.Groups[1].Value == "令和" || m.Groups[1].Value == "r";
                if (n < 1 || (!reiwa && n > 31)) return false;
                int y = (reiwa ? 2018 : 1988) + n;
                if (m.Groups[3].Success) return FiscalRange(y, out from, out to, out label);
                from = new DateTime(y, 1, 1);
                to = from.AddYears(1);
                label = y + "年";
                return true;
            }

            m = Year.Match(s);
            if (m.Success)
            {
                int y = Int(m.Groups[1]);
                if (y < 1900 || y > 2200) return false;
                from = new DateTime(y, 1, 1);
                to = from.AddYears(1);
                label = y + "年";
                return true;
            }

            m = Month.Match(s);
            if (m.Success)
            {
                int y = Int(m.Groups[1]);
                int mo = m.Groups[2].Success ? Int(m.Groups[2]) : Int(m.Groups[3]);
                if (y < 1900 || y > 2200 || mo < 1 || mo > 12) return false;
                from = new DateTime(y, mo, 1);
                to = from.AddMonths(1);
                label = y + "年" + mo + "月";
                return true;
            }

            m = Day.Match(s);
            if (m.Success)
            {
                int y = Int(m.Groups[1]);
                int mo = m.Groups[2].Success ? Int(m.Groups[2]) : Int(m.Groups[4]);
                int d = m.Groups[3].Success ? Int(m.Groups[3]) : Int(m.Groups[5]);
                if (y < 1900 || y > 2200 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return false;
                from = new DateTime(y, mo, d);
                to = from.AddDays(1);
                label = y + "/" + mo + "/" + d;
                return true;
            }

            m = MonthOfYear.Match(s);
            if (m.Success)
            {
                int mo = Int(m.Groups[1]);
                if (mo < 1 || mo > 12) return false;
                int y = mo > today.Month ? today.Year - 1 : today.Year; // まだ来ていない月なら去年
                from = new DateTime(y, mo, 1);
                to = from.AddMonths(1);
                label = y + "年" + mo + "月";
                return true;
            }
            return false;
        }

        static bool FiscalRange(int y, out DateTime from, out DateTime to, out string label)
        {
            from = DateTime.MinValue;
            to = DateTime.MaxValue;
            label = null;
            if (y < 1900 || y > 2200) return false;
            from = new DateTime(y, 4, 1); // 日本の年度 (4 月始まり)
            to = from.AddYears(1);
            label = y + "年度";
            return true;
        }

        static int Int(Group g)
        {
            return int.Parse(g.Value, CultureInfo.InvariantCulture);
        }

        static long Ft(DateTime local)
        {
            return local.ToUniversalTime().ToFileTimeUtc();
        }
    }
}
