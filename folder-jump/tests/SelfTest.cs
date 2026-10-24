using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderJump
{
    /// <summary>
    /// 検索エンジン部分 (UI 以外) のセルフテスト。
    /// tests\run-selftest.bat で実行できます。
    /// </summary>
    internal static class SelfTest
    {
        static int failures;

        static int Main()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception) { }
            string tmp = Path.Combine(Path.GetTempPath(), "FolderJumpSelfTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            AppPaths.DataDir = Path.Combine(tmp, "data");
            try
            {
                TestTextNorm();
                TestQueryParse();
                TestFiltersAndSynonyms();
                TestPathUtil();
                TestSettings(tmp);
                TestScanAndSearch(tmp);
                TestPerformance();
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("EXCEPTION: " + ex);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
            }
            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }

        static void Check(bool ok, string what)
        {
            if (!ok) failures++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        }

        static void TestTextNorm()
        {
            Console.WriteLine("[TextNorm]");
            Check(TextNorm.Key("ＰＲＯＪＥＣＴ_Ａ") == "project_a", "全角英字 → 半角小文字");
            Check(TextNorm.Key("ﾐﾂﾓﾘ") == "みつもり", "半角カナ → ひらがな");
            Check(TextNorm.Key("ミツモリ") == "みつもり", "カタカナ → ひらがな");
            Check(TextNorm.Key("見積書") == "見積書", "漢字はそのまま");
            string ascii = "report";
            Check(ReferenceEquals(TextNorm.Key(ascii), ascii), "変化がない時は同じインスタンス");
            Check(TextNorm.Key("Report.XLSX") == "report.xlsx", "ASCII 小文字化");
            Check(TextNorm.Key("２０２４年") == "2024年", "全角数字");
        }

        static void TestQueryParse()
        {
            Console.WriteLine("[Query]");
            Query q = Query.Parse("見積　2024 -旧");
            Check(q.Terms.Length == 2 && q.Terms[0] == "見積" && q.Terms[1] == "2024", "全角スペースでも区切る");
            Check(q.Excludes.Length == 1 && q.Excludes[0] == "旧", "-語 で除外");
            Check(Query.Parse("営業\\2024/見積").Terms.Length == 3, "\\ や / も区切りとして扱う");
            Check(Query.Parse("2024-05").Terms[0] == "2024-05", "途中のハイフンは除外扱いしない");
            Check(PathInput.TryParse("\"\\\\fileserver\\share\\営業\\\"") == @"\\fileserver\share\営業", "引用符付きの UNC パス");
            Check(PathInput.TryParse("Z:/営業/2024/") == @"Z:\営業\2024", "ドライブ + スラッシュ");
            Check(PathInput.TryParse("Z:\\") == @"Z:\", "ドライブ直下");
            Check(PathInput.TryParse("見積 2024") == null, "普通の検索語はパス扱いしない");
        }

        static void TestPathUtil()
        {
            Console.WriteLine("[PathUtil]");
            Check(PathUtil.Parent(@"\\srv\share\a\b") == @"\\srv\share\a", "UNC の親");
            Check(PathUtil.Parent(@"\\srv\share") == null, "共有の親は無し");
            Check(PathUtil.Parent(@"Z:\a") == @"Z:\", "ドライブ直下が親");
            Check(PathUtil.Parent(@"Z:\") == null, "ドライブの親は無し");
            Check(PathUtil.IsUnder(@"\\srv\share\a", @"\\srv\share"), "IsUnder: 中");
            Check(!PathUtil.IsUnder(@"\\srv\share2", @"\\srv\share"), "IsUnder: 似た名前は別");
            Check(PathUtil.IsUnder(@"Z:\a\b", @"Z:\"), "IsUnder: ドライブ直下");
            Check(PathUtil.Pretty(@"\\srv\share\営業", 90) == "srv  ›  share  ›  営業", "表示用パス");
            Check(QueryEngine.Highlight("見積書_2024.xlsx", TextNorm.Key("見積書_2024.xlsx"), new[] { new[] { "見積" }, new[] { "2024" } }).SequenceEqual(new[] { 0, 2, 4, 4 }), "ハイライト範囲");
        }

        static void TestSettings(string tmp)
        {
            Console.WriteLine("[Settings]");
            var s = new Settings();
            s.Roots.Add(@"\\fileserver\共有");
            s.Roots.Add(@"Z:\");
            s.Exclude.AddRange(Settings.DefaultExclude);
            s.SynonymLines.AddRange(Synonyms.Defaults);
            s.Hotkey = "Ctrl+Shift+J";
            s.ScanThreads = 4;
            string file = Path.Combine(tmp, "t.ini");
            s.Save(file);
            Settings r = Settings.Load(file);
            Check(r.Roots.Count == 2 && r.Roots[0] == @"\\fileserver\共有" && r.Roots[1] == @"Z:\", "Roots を保存/読み込み");
            Check(r.Hotkey == "Ctrl+Shift+J" && r.ScanThreads == 4, "General を保存/読み込み");
            Check(r.Exclude.Contains("#recycle"), "# で始まる除外名もコメント扱いしない");
            Check(r.ScanSignature == s.ScanSignature, "同じ設定なら同じシグネチャ");
            Check(r.SynonymLines.Count == Synonyms.Defaults.Length && r.Theme == "dark" && r.Zoom == 100, "辞書と見た目の既定値");
            s.Theme = "light"; s.Accent = "#FF8800"; s.Zoom = 120; s.Compact = true; s.SaveDialogJump = false;
            s.SynonymLines.Add("稟議, りんぎ, 決裁");
            s.Save(file);
            r = Settings.Load(file);
            Check(r.Theme == "light" && r.Accent == "#FF8800" && r.Zoom == 120 && r.Compact && !r.SaveDialogJump, "見た目の保存/読み込み");
            Check(r.SynonymLines.Count == Synonyms.Defaults.Length + 1 && r.Synonyms.Expand("決裁").Length == 3, "辞書の保存/読み込み");
            Check(!r.Set("zoom", "999") || r.Zoom == 160, "範囲外の値は丸める");
            Check(!r.Set("nosuchkey", "1"), "知らないキーは false");
            string old = Path.Combine(tmp, "old.ini");
            File.WriteAllText(old, "[General]\nHotkey = Alt+Q\n[Roots]\nZ:\\\n");
            r = Settings.Load(old);
            Check(r.Hotkey == "Alt+Q" && r.SynonymLines.Count == Synonyms.Defaults.Length && r.Exclude.Count > 0, "以前の設定ファイルでも既定の辞書を使う");
        }

        sealed class FakeHost : ICommandHost
        {
            public Settings S = new Settings();
            public Settings CurrentSettings { get { return S; } }
            public bool IsStartupRegistered { get { return false; } }
            public void Rescan() { }
            public void PickRootFolder() { }
            public void AddRoot(string path) { }
            public void RemoveRoot(string path) { }
            public void OpenSettingsFile() { }
            public void OpenDataFolder() { }
            public void ToggleStartup() { }
            public void ClearHistory() { }
            public void Quit() { }
            public string LastQuery;
            public void SetQueryText(string text) { LastQuery = text; }
            public void ChangeSetting(string key, string value) { S = S.Clone(); S.Set(key, value); }
        }

        static void Touch(string root, string rel)
        {
            string p = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
            if (rel.EndsWith("\\")) { Directory.CreateDirectory(p); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, "x");
        }

        static void TestScanAndSearch(string tmp)
        {
            Console.WriteLine("[Scan + Search]");
            string root = Path.Combine(tmp, "share");
            string[] tree =
            {
                @"営業部\2024\見積\見積書_青葉商事_2024-05.xlsx",
                @"営業部\2024\見積\見積書_北斗工業.xlsx",
                @"営業部\2024\見積\単価表.xlsx",
                @"営業部\2024\請求書\請求書_青葉商事.pdf",
                @"営業部\2023\見積\見積書_旧版.xlsx",
                @"営業部\顧客リスト.xlsx",
                @"総務部\規程\就業規則.docx",
                @"総務部\ミツモリ参考\",
                @"開発部\ＰＲＯＪＥＣＴ_Ａ\設計書.docx",
                @"開発部\フォルダ10\",
                @"開発部\フォルダ2\",
                @".hidden\secret.txt",
                @"$RECYCLE.BIN\gomi.txt",
                @"営業部\~$見積書.xlsx",
            };
            foreach (string t in tree) Touch(root, t);
            if (Native.IsWindows) File.SetAttributes(Path.Combine(root, ".hidden"), FileAttributes.Directory | FileAttributes.Hidden);

            var settings = new Settings();
            settings.Roots.Add(root);
            settings.Exclude.AddRange(Settings.DefaultExclude);
            settings.SynonymLines.AddRange(Synonyms.Defaults);
            settings.ScanThreads = 4;
            var job = new ScanJob(settings);
            FileIndex idx = null;
            var done = new ManualResetEvent(false);
            job.Completed += (j, result) => { idx = result; done.Set(); };
            job.Start();
            Check(done.WaitOne(20000), "スキャンが終わる");
            Check(idx != null && idx.Complete, "インデックスが完成");
            if (idx == null) return;

            var names = new HashSet<string>(Enumerable.Range(0, idx.Count).Select(i => idx.Name[i]));
            Check(names.Contains("見積書_北斗工業.xlsx") && names.Contains("ミツモリ参考"), "ファイルもフォルダも入る");
            Check(!names.Contains("secret.txt") && !names.Contains(".hidden"), "隠し(ドット)フォルダは入らない");
            Check(!names.Contains("gomi.txt") && !names.Contains("$RECYCLE.BIN"), "除外フォルダは入らない");
            Check(!names.Contains("~$見積書.xlsx"), "Office の一時ファイルは入らない");
            bool ordered = true;
            for (int i = 0; i < idx.Count; i++) if (idx.Parent[i] >= i) ordered = false;
            Check(ordered, "親は必ず子より前");

            var host = new FakeHost();
            host.S = settings;
            var usage = new UsageStore(Path.Combine(tmp, "h.tsv"), Path.Combine(tmp, "p.tsv"));
            var engine = new QueryEngine(host, usage);
            var ct = CancellationToken.None;

            List<ResultItem> r = engine.Compute("見積", -1, idx, ct);
            var titles = r.Select(x => x.Title).ToList();
            Check(titles.Count(t => t == "見積") == 2, "「見積」フォルダが 2 つ出る");
            Check(titles.Contains("見積書_青葉商事_2024-05.xlsx"), "「見積書」ファイルも出る");
            Check(!titles.Contains("単価表.xlsx"), "名前に含まないファイルは出ない (フォルダ名だけ一致は除外)");
            Check(r.Count > 0 && r[0].Title == "見積" && r[0].IsDir, "一番上はフォルダ「見積」");

            r = engine.Compute("2024 見積", -1, idx, ct);
            titles = r.Select(x => x.Title).ToList();
            Check(titles.Contains("見積書_北斗工業.xlsx") && !titles.Contains("見積書_旧版.xlsx"), "親フォルダ名で絞り込み (2024 だけ)");
            Check(titles.Contains("単価表.xlsx") == false, "2024 見積 でも単価表は出ない");

            r = engine.Compute("見積 -旧", -1, idx, ct);
            Check(!r.Any(x => x.Title == "見積書_旧版.xlsx"), "-旧 で除外");

            r = engine.Compute("見積もり", -1, idx, ct);
            Check(r.Any(x => x.Title == "見積書_北斗工業.xlsx") && r.Any(x => x.Title == "見積"), "表記ゆれ: 見積もり で 見積 も見つかる");

            r = engine.Compute("令和6 見積書", -1, idx, ct);
            Check(r.Any(x => x.Title == "見積書_青葉商事_2024-05.xlsx"), "和暦: 令和6 で 2024 も見つかる");

            r = engine.Compute("青葉 .pdf", -1, idx, ct);
            Check(r.Count == 1 && r[0].Title == "請求書_青葉商事.pdf", "拡張子で絞り込み");
            r = engine.Compute(".xls", -1, idx, ct);
            Check(r.Count == 5 && r.All(x => x.Title.EndsWith(".xlsx")), "キーワードなしで拡張子だけ (.xls で .xlsx も): " + r.Count);
            r = engine.Compute("見積/", -1, idx, ct);
            Check(r.Count == 2 && r.All(x => x.IsDir), "末尾 / でフォルダだけ");
            r = engine.Compute("@今日", -1, idx, ct);
            Check(r.Count > 5 && r.All(x => x.Kind == ItemKind.Entry && x.Path != root), "@今日 (今作ったファイル) ・ルートは出さない: " + r.Count);
            r = engine.Compute("@2019", -1, idx, ct);
            Check(r.Count == 0, "@2019 には何もない");
            r = engine.Compute("見積", -1, idx, ct, true);
            Check(r.Count > 0 && r[0].IsDir, "保存ダイアログからはフォルダ優先");

            r = engine.Compute("?", -1, idx, ct);
            Check(r.Count > 5 && r.All(x => x.Kind == ItemKind.Command), "? で書き方の一覧");
            r[1].Command();
            Check(host.LastQuery == "見積 .xlsx", "例を選ぶと入力される");
            r = engine.Compute(">見た目", -1, idx, ct);
            Check(r.Count > 10 && r.Any(x => x.Title == "テーマ: ダーク" && x.Badge == "使用中"), ">見た目 で設定の一覧 (使用中の表示)");
            r.First(x => x.Title == "テーマ: ライト").Command();
            Check(host.S.Theme == "light", "コマンドから設定を変更");
            r = engine.Compute(">", -1, idx, ct);
            Check(!r.Any(x => x.Title.StartsWith("テーマ")), "> だけでは見た目の細かい項目は出さない");
            r = engine.Compute(">設定", -1, idx, ct);
            Check(r.Count <= 3 && r[0].Title == "設定ファイルを開く" && !r.Any(x => x.Title.StartsWith("アクセント色")),
                ">設定 は「設定ファイルを開く」が先頭で、見た目の項目がまとめて混ざらない: " + string.Join(" / ", r.Select(x => x.Title)));

            r = engine.Compute("みつもり", -1, idx, ct);
            Check(r.Any(x => x.Title == "ミツモリ参考"), "ひらがなでカタカナの名前に一致");

            r = engine.Compute("project_a", -1, idx, ct);
            Check(r.Any(x => x.Title == "ＰＲＯＪＥＣＴ_Ａ"), "半角で全角の名前に一致");

            r = engine.Compute("青葉", -1, idx, ct);
            ResultItem first = r.FirstOrDefault(x => x.Title.StartsWith("見積書_青葉"));
            Check(first != null && first.Highlights != null && first.Highlights[0] == 4 && first.Highlights[1] == 2, "一致部分のハイライト位置");

            int n2024 = -1;
            for (int i = 0; i < idx.Count; i++) if (idx.Name[i] == "2024") n2024 = i;
            r = engine.Compute("青葉", n2024, idx, ct);
            Check(r.Count == 2 && r.All(x => x.Path.Contains("2024")), "Tab で入ったフォルダの中だけを検索");

            r = engine.Compute("", n2024, idx, ct);
            Check(r.Count == 2 && r[0].Title == "見積" && r[1].Title == "請求書", "空入力ならフォルダの中身を一覧");

            int dev = -1;
            for (int i = 0; i < idx.Count; i++) if (idx.Name[i] == "開発部") dev = i;
            var kids = idx.Children(dev, 100).Select(i => idx.Name[i]).ToList();
            Check(kids.IndexOf("ＰＲＯＪＥＣＴ_Ａ") >= 0 && kids.Count == 3, "子の一覧");

            // 履歴で順位が上がる
            r = engine.Compute("見積", -1, idx, ct);
            // 同点で 2 番目にいる「見積」フォルダを使って、履歴で 1 番に上がるかを見る
            string oldPath = r.Where(x => x.Title == "見積").Select(x => x.Path).Last();
            int before = r.FindIndex(x => x.Path == oldPath);
            for (int i = 0; i < 5; i++) usage.Record(oldPath, true);
            r = engine.Compute("見積", -1, idx, ct);
            int after = r.FindIndex(x => x.Path == oldPath);
            Check(before > 0 && after == 0, "よく使うフォルダが一番上に来る (" + before + " → " + after + ")");

            // ピン留めとホーム画面
            usage.TogglePin(Path.Combine(root, "総務部", "規程"), true);
            r = engine.Compute("", -1, idx, ct);
            Check(r.Count >= 4 && r[0].Kind == ItemKind.Header && r[0].Title == "ピン留め" && r[1].Title == "規程" && r[1].Node >= 0, "ピン留めがホームの先頭 (Tab 用にノードも解決)");
            Check(r.Any(x => x.Kind == ItemKind.Header && x.Title == "よく使う"), "よく使うの見出し");
            Check(r.Last().Path == root && r.Last().Node == 0, "検索対象 (ルート) が最後に並ぶ");

            // コマンド
            r = engine.Compute(">再スキャン", -1, idx, ct);
            Check(r.Count == 1 && r[0].Kind == ItemKind.Command, "> でコマンドを絞り込み");

            // パスの貼り付け
            string pasted = Path.Combine(root, "営業部", "2024");
            r = engine.Compute(pasted, -1, idx, ct);
            if (Native.IsWindows)
                Check(r.Count >= 1 && r[0].Node == n2024, "貼り付けたパスをインデックスから解決");
            r = engine.Compute(@"\\otherserver\share", -1, idx, ct);
            Check(r.Count == 2 && r[1].Kind == ItemKind.Command, "登録外のパスなら「検索対象に追加」を出す");

            // 保存と読み込み
            string file = Path.Combine(tmp, "index.bin");
            idx.Save(file);
            FileIndex loaded = FileIndex.Load(file);
            bool same = loaded != null && loaded.Count == idx.Count && loaded.Signature == idx.Signature;
            for (int i = 0; same && i < idx.Count; i++)
                same = loaded.Name[i] == idx.Name[i] && loaded.Key[i] == idx.Key[i] && loaded.Parent[i] == idx.Parent[i]
                    && loaded.Flags[i] == idx.Flags[i] && loaded.Mtime[i] == idx.Mtime[i];
            Check(same, "インデックスの保存/読み込み");

            // 履歴の保存/読み込み
            var usage2 = new UsageStore(Path.Combine(tmp, "h.tsv"), Path.Combine(tmp, "p.tsv"));
            usage2.Load();
            Check(usage2.TopHistory(5).Any(e => e.Path == oldPath && e.Count == 5) && usage2.Pins().Count == 1, "履歴とピンの保存/読み込み");
        }

        static void TestFiltersAndSynonyms()
        {
            Console.WriteLine("[絞り込み・表記ゆれ]");
            var now = new DateTime(2026, 10, 24, 15, 0, 0); // 土曜日
            Query q = Query.Parse("見積 .xlsx .pdf @今週", null, now);
            Check(q.Terms.Length == 1 && q.Exts.Length == 2 && q.Exts[0] == "xlsx", "拡張子の指定");
            Check(q.MinTime == new DateTime(2026, 10, 19).ToUniversalTime().ToFileTimeUtc() && q.MaxTime == 0, "@今週 は月曜から");
            Check(q.Describe() == ".xlsx .pdf  ·  今週", "絞り込みの説明: " + q.Describe());
            q = Query.Parse("営業/ 2024", null, now);
            Check(q.Kind == Query.KindFolders && q.Terms.Length == 2, "末尾 / でフォルダだけ");
            q = Query.Parse("/", null, now);
            Check(q.Kind == Query.KindFolders && q.Terms.Length == 0 && !q.IsEmpty, "/ だけでもフォルダの絞り込み");
            q = Query.Parse("契約 .*", null, now);
            Check(q.Kind == Query.KindFiles, ".* でファイルだけ");
            q = Query.Parse("＠先月", null, now);
            Check(q.MinTime == new DateTime(2026, 9, 1).ToUniversalTime().ToFileTimeUtc()
                && q.MaxTime == new DateTime(2026, 10, 1).ToUniversalTime().ToFileTimeUtc(), "全角の ＠先月");
            q = Query.Parse("@2024年度", null, now);
            Check(q.MinTime == new DateTime(2024, 4, 1).ToUniversalTime().ToFileTimeUtc()
                && q.MaxTime == new DateTime(2025, 4, 1).ToUniversalTime().ToFileTimeUtc() && q.DateLabel == "2024年度", "@2024年度 は 4 月始まり");
            q = Query.Parse("@r6", null, now);
            Check(q.DateLabel == "2024年", "@r6 は 2024 年");
            q = Query.Parse("@2024-05-12", null, now);
            Check(q.DateLabel == "2024/5/12", "@年-月-日");
            q = Query.Parse("@202405以降", null, now);
            Check(q.MaxTime == 0 && q.DateLabel == "2024年5月 以降", "@年月以降");
            q = Query.Parse("@7日", null, now);
            Check(q.MinTime == now.AddDays(-7).ToUniversalTime().ToFileTimeUtc() && q.DateLabel == "直近7日", "@7日");
            q = Query.Parse("@3ヶ月", null, now);
            Check(q.DateLabel == "直近3か月", "@3ヶ月");
            q = Query.Parse("@11月", null, now);
            Check(q.DateLabel == "2025年11月", "まだ来ていない月は去年");
            q = Query.Parse("@あした", null, now);
            Check(q.Terms.Length == 1 && q.Terms[0] == "@あした" && !q.HasFilters, "読めない @ は普通の検索語");

            var syn = new Synonyms(Synonyms.Defaults, true);
            string[] a = syn.Expand(TextNorm.Key("見積もり"));
            Check(a.Length == 1 && a[0] == "見積", "見積もり → 見積 (短い方に集約): " + string.Join(",", a));
            a = syn.Expand(TextNorm.Key("打ち合わせ"));
            Check(Array.IndexOf(a, "打合せ") >= 0 && Array.IndexOf(a, "mtg") >= 0, "打ち合わせ → 打合せ, MTG");
            a = syn.Expand("2024");
            Check(Array.IndexOf(a, "令和6") >= 0 && Array.IndexOf(a, "r6") >= 0 && Array.IndexOf(a, "r06") >= 0, "2024 → 令和6, r6, r06");
            a = syn.Expand(TextNorm.Key("R6年度"));
            Check(Array.IndexOf(a, "2024年度") >= 0, "R6年度 → 2024年度");
            a = syn.Expand("2019");
            Check(Array.IndexOf(a, "令和元") >= 0 && Array.IndexOf(a, "h31") >= 0, "2019 → 令和元, 平成31");
            a = syn.Expand(TextNorm.Key("サーバー"));
            Check(a.Length == 1 && a[0] == TextNorm.Key("サーバ"), "長音: サーバー → サーバ");
            a = syn.Expand("20240611");
            Check(a.Length == 1, "日付の数字は和暦に広げない");
            var custom = new Synonyms(new[] { "稟議, りんぎ, 決裁" }, false);
            Check(custom.Expand("決裁").Length == 3 && custom.Expand("2024").Length == 1, "自分で足した辞書 / 和暦オフ");
            Check(new Synonyms(new[] { "ひとつだけ" }, false).GroupCount == 0, "1 語だけの行は無視");
        }

        static void TestPerformance()
        {
            Console.WriteLine("[Performance]");
            // 100 万件規模の疑似インデックス (10 部署 × 年 × 月 × 案件 × ファイル)
            var rnd = new Random(1);
            string[] words = { "見積", "請求", "契約", "議事録", "提案", "報告", "設計", "仕様", "予算", "売上", "顧客", "マニュアル", "申請", "稟議" };
            string[] companies = { "青葉商事", "北斗工業", "みなと電機", "さくら物流", "あおい建設", "ひかり製薬", "つばさ食品", "かえで不動産" };
            var parent = new List<int>(); var name = new List<string>(); var flags = new List<byte>();
            Action<int, string, byte> add = (p, n, f) => { parent.Add(p); name.Add(n); flags.Add(f); };
            add(-1, @"\\fileserver\share", 3);
            var level = new List<int> { 0 };
            for (int depth = 0; depth < 4; depth++)
            {
                var next = new List<int>();
                foreach (int p in level)
                {
                    int kids = depth == 0 ? 12 : depth == 1 ? 8 : 12;
                    for (int k = 0; k < kids; k++)
                    {
                        string n = depth == 0 ? (k + 1) + "_" + words[k % words.Length] + "部"
                                 : depth == 1 ? (2019 + k) + "年度"
                                 : depth == 2 ? (k + 1).ToString("00") + "月"
                                 : companies[rnd.Next(companies.Length)] + "_" + words[rnd.Next(words.Length)] + "_" + rnd.Next(1000);
                        add(p, n, 1);
                        next.Add(parent.Count - 1);
                    }
                }
                level = next;
            }
            foreach (int p in level)
                for (int k = 0; k < 70; k++)
                    add(p, words[rnd.Next(words.Length)] + "書_" + companies[rnd.Next(companies.Length)] + "_v" + k + ".xlsx", 0);
            int count = name.Count;
            var keys = name.Select(n => TextNorm.Key(n)).ToArray();
            var idx = new FileIndex(count, parent.ToArray(), name.ToArray(), keys, new long[count], flags.ToArray(), new[] { @"\\fileserver\share" }, "", DateTime.UtcNow, true, false);
            var boost = new Dictionary<int, double>();
            var sw = Stopwatch.StartNew();
            List<SearchHit> hits = null;
            Query perfQuery = Query.Parse("見積もり 青葉 2024", new Synonyms(Synonyms.Defaults, true), DateTime.Now); // 辞書・和暦あり
            for (int i = 0; i < 5; i++) hits = idx.Search(perfQuery, -1, boost, 80, CancellationToken.None);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / 5;
            Console.WriteLine("  info " + count.ToString("N0") + " 件で 3 語検索 (表記ゆれ・和暦あり): " + ms.ToString("F1") + " ms / 回, " + hits.Count + " 件ヒット");
            Check(hits.Count == 80, "上位 80 件を返す");
            Check(ms < 1500, "100 万件規模でも実用的な速さ");
            bool sorted = true;
            for (int i = 1; i < hits.Count; i++) if (hits[i].Score > hits[i - 1].Score) sorted = false;
            Check(sorted, "スコア順に並ぶ");
        }
    }
}
