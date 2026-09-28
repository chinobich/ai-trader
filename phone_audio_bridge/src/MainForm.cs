using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PhoneBridge
{
    internal sealed class MainForm : Form
    {
        readonly string dataDir;
        readonly string settingsPath;
        readonly Settings settings;
        readonly bool startNow;
        BridgeEngine engine;
        int tickCount;

        TabControl tabs;
        ComboBox cbPhoneIn, cbPhoneOut, cbMic, cbSpk, cbAi;
        Button btnStart, btnStop;
        Label lblState;
        CheckBox chkMute, chkRecord, chkAutoStart, chkAgc, chkStereo, chkDuckAi, chkDuckHs;
        TrackBar tbCallerHs, tbOpPhone, tbAiCaller, tbAiOp;
        Label lbCallerHs, lbOpPhone, lbAiCaller, lbAiOp;
        LevelMeter mCaller, mSelf, mAi;
        TextBox txtStatus, txtLog;
        Timer timer;

        public MainForm(string dataDir, bool startNow, bool startMinimized)
        {
            this.dataDir = dataDir;
            this.startNow = startNow;
            settingsPath = Path.Combine(dataDir, "PhoneBridge.ini");
            settings = Settings.Load(settingsPath);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "電話音声ブリッジ (PhoneBridge) " + Program.Version;
            ClientSize = new Size(660, 540);
            MinimumSize = new Size(560, 460);
            StartPosition = FormStartPosition.CenterScreen;
            if (startMinimized) WindowState = FormWindowState.Minimized;

            tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildCallTab());
            tabs.TabPages.Add(BuildDeviceTab());
            tabs.TabPages.Add(BuildAudioTab());
            tabs.TabPages.Add(BuildLogTab());
            Controls.Add(tabs);
            ResumeLayout(false);
            PerformLayout();

            LoadUiFromSettings();
            RefreshDevices();
            SetRunningUi(false);

            timer = new Timer { Interval = 50 };
            timer.Tick += OnTick;
            Shown += OnShown;
            FormClosing += OnClosing;
        }

        // ------------------------------------------------------------------ 画面の組み立て

        TabPage BuildCallTab()
        {
            var page = new TabPage("通話") { Padding = new Padding(10) };
            var g = NewGrid(2);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            btnStart = new Button { Text = "▶ 開始", AutoSize = true, Padding = new Padding(18, 4, 18, 4) };
            btnStop = new Button { Text = "■ 停止", AutoSize = true, Padding = new Padding(18, 4, 18, 4) };
            lblState = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(14, 12, 3, 3), Font = new Font(Font, FontStyle.Bold) };
            btnStart.Click += delegate { StartEngine(); };
            btnStop.Click += delegate { StopEngine(); };
            buttons.Controls.AddRange(new Control[] { btnStart, btnStop, lblState });
            AddSpan(g, buttons);

            chkMute = NewCheck("マイクをミュート（相手とAI基盤に自分の声を送らない）");
            chkMute.CheckedChanged += delegate { if (engine != null) engine.MicMute = chkMute.Checked; UpdateStateLabel(); };
            AddSpan(g, chkMute);
            chkRecord = NewCheck("通話を録音する（WAV形式：左＝相手／右＝自分）");
            chkRecord.CheckedChanged += delegate { if (engine != null) engine.Recording = chkRecord.Checked; UpdateStateLabel(); };
            AddSpan(g, chkRecord);

            mCaller = new LevelMeter();
            mSelf = new LevelMeter();
            mAi = new LevelMeter();
            AddRow(g, "相手の声（電話から）", mCaller);
            AddRow(g, "自分の声（マイク）", mSelf);
            AddRow(g, "AI基盤へ送っている音", mAi);

            var statusLabel = new Label { Text = "各機器の状態", AutoSize = true, Margin = new Padding(3, 10, 3, 0) };
            AddSpan(g, statusLabel);
            txtStatus = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true, BackColor = SystemColors.Window };
            AddSpan(g, txtStatus);
            FinishRows(g, true);
            page.Controls.Add(g);
            return page;
        }

        TabPage BuildDeviceTab()
        {
            var page = new TabPage("機器の設定") { Padding = new Padding(10), AutoScroll = true };
            var g = NewGrid(2);
            cbPhoneIn = NewCombo();
            cbPhoneOut = NewCombo();
            cbMic = NewCombo();
            cbSpk = NewCombo();
            cbAi = NewCombo();
            AddRow(g, "電話の音\n（白い箱のマイク側）", cbPhoneIn);
            AddRow(g, "電話へ送る音\n（白い箱のスピーカー側）", cbPhoneOut);
            AddRow(g, "ヘッドセットのマイク", cbMic);
            AddRow(g, "ヘッドセットのスピーカー", cbSpk);
            AddRow(g, "AI基盤へ送る先\n（仮想ケーブル）", cbAi);

            var refresh = new Button { Text = "機器の一覧を更新", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8) };
            refresh.Click += delegate { ReadSelections(); RefreshDevices(); };
            AddSpan(g, refresh);

            var hint = new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text =
                    "・白い箱は「USB Audio Device」などの名前で表示されることが多いです。\n" +
                    "・AI基盤へ送る先は「CABLE Input (VB-Audio Virtual Cable)」を選びます。\n" +
                    "　AI基盤（仮想ブラウザ）側では、マイクに「CABLE Output」を選んでください。\n" +
                    "・どれが白い箱か分からないときは、開始して「通話」タブのメーターで確認できます\n" +
                    "　（ヘッドセットに話すと「自分の声」、通話中の相手が話すと「相手の声」が動きます）。\n" +
                    "・機器の変更は、停止中にだけできます。"
            };
            AddSpan(g, hint);
            FinishRows(g, false);
            page.Controls.Add(g);
            return page;
        }

        TabPage BuildAudioTab()
        {
            var page = new TabPage("音量・音質") { Padding = new Padding(10), AutoScroll = true };
            var g = NewGrid(3);
            tbCallerHs = AddSlider(g, "相手の声の大きさ\n（ヘッドセットで聞く音）", out lbCallerHs);
            tbOpPhone = AddSlider(g, "自分の声の大きさ\n（相手に届く音）", out lbOpPhone);
            tbAiCaller = AddSlider(g, "AI基盤へ：相手の声", out lbAiCaller);
            tbAiOp = AddSlider(g, "AI基盤へ：自分の声", out lbAiOp);

            chkAgc = NewCheck("AI基盤へ送る音量を自動でそろえる（おすすめ）");
            chkDuckAi = NewCheck("自分が話している間、AI基盤へ送る相手側の音を下げる\n（自分の発言が二重に文字起こしされるとき）");
            chkDuckHs = NewCheck("自分が話している間、ヘッドセットの相手側の音を下げる\n（自分の声が遅れて聞こえて話しにくいとき）");
            chkStereo = NewCheck("AI基盤へ2chで送る（左＝相手／右＝自分）\n※AI基盤が2chに対応してから使ってください");
            chkAutoStart = NewCheck("このツールを起動したら自動で開始する");
            foreach (CheckBox c in new[] { chkAgc, chkDuckAi, chkDuckHs, chkStereo, chkAutoStart })
            {
                c.CheckedChanged += OnOptionChanged;
                AddSpan(g, c);
            }
            FinishRows(g, false);
            page.Controls.Add(g);
            return page;
        }

        TabPage BuildLogTab()
        {
            var page = new TabPage("ログ・診断") { Padding = new Padding(10) };
            var g = NewGrid(1);
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            g.Controls.Add(txtLog);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            var diag = new Button { Text = "診断情報を保存", AutoSize = true };
            var rec = new Button { Text = "録音フォルダを開く", AutoSize = true };
            var folder = new Button { Text = "設定・ログのフォルダを開く", AutoSize = true };
            diag.Click += delegate { SaveDiagnostics(); };
            rec.Click += delegate { OpenFolder(RecordDirectory()); };
            folder.Click += delegate { OpenFolder(dataDir); };
            buttons.Controls.AddRange(new Control[] { diag, rec, folder });
            g.Controls.Add(buttons);
            g.RowCount = 2;
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(g);
            return page;
        }

        static TableLayoutPanel NewGrid(int columns)
        {
            var g = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, AutoSize = false };
            for (int i = 0; i < columns; i++)
            {
                if (columns > 1 && i == 1) g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                else g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }
            if (columns == 1) g.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
            return g;
        }

        static void FinishRows(TableLayoutPanel g, bool fillLast)
        {
            g.RowStyles.Clear();
            int rows = 0;
            foreach (Control c in g.Controls) rows = Math.Max(rows, g.GetRow(c) + 1);
            rows = Math.Max(rows, g.RowCount);
            for (int i = 0; i < rows; i++)
                g.RowStyles.Add(fillLast && i == rows - 1 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            if (!fillLast)
            {
                // 余った高さを受け止める空の行（上の行が縦に引き伸ばされないように）
                g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                rows++;
            }
            g.RowCount = rows;
        }

        static void AddRow(TableLayoutPanel g, string label, Control control)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 6) };
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(3, 6, 3, 6);
            g.Controls.Add(l, 0, g.RowCount);
            g.Controls.Add(control, 1, g.RowCount);
            g.RowCount++;
        }

        static void AddSpan(TableLayoutPanel g, Control control)
        {
            g.Controls.Add(control, 0, g.RowCount);
            g.SetColumnSpan(control, g.ColumnCount);
            g.RowCount++;
        }

        TrackBar AddSlider(TableLayoutPanel g, string label, out Label valueLabel)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 6) };
            var tb = new TrackBar
            {
                Minimum = -20,
                Maximum = 20,
                TickFrequency = 5,
                SmallChange = 1,
                LargeChange = 3,
                AutoSize = false,
                Height = 30,
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };
            valueLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(60, 0), Margin = new Padding(6, 6, 3, 6) };
            tb.ValueChanged += OnOptionChanged;
            g.Controls.Add(l, 0, g.RowCount);
            g.Controls.Add(tb, 1, g.RowCount);
            g.Controls.Add(valueLabel, 2, g.RowCount);
            g.RowCount++;
            return tb;
        }

        static ComboBox NewCombo()
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, MinimumSize = new Size(260, 0) };
        }

        static CheckBox NewCheck(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 5, 3, 5) };
        }

        // ------------------------------------------------------------------ 設定と画面の同期

        bool loadingUi;

        void LoadUiFromSettings()
        {
            loadingUi = true;
            tbCallerHs.Value = settings.CallerToHeadsetDb;
            tbOpPhone.Value = settings.OperatorToPhoneDb;
            tbAiCaller.Value = settings.AiCallerDb;
            tbAiOp.Value = settings.AiOperatorDb;
            chkAgc.Checked = settings.AiAgc;
            chkStereo.Checked = settings.AiStereo;
            chkDuckAi.Checked = settings.DuckAi;
            chkDuckHs.Checked = settings.DuckHeadset;
            chkAutoStart.Checked = settings.AutoStart;
            loadingUi = false;
            UpdateSliderLabels();
        }

        void OnOptionChanged(object sender, EventArgs e)
        {
            UpdateSliderLabels();
            if (loadingUi) return;
            settings.CallerToHeadsetDb = tbCallerHs.Value;
            settings.OperatorToPhoneDb = tbOpPhone.Value;
            settings.AiCallerDb = tbAiCaller.Value;
            settings.AiOperatorDb = tbAiOp.Value;
            settings.AiAgc = chkAgc.Checked;
            settings.AiStereo = chkStereo.Checked;
            settings.DuckAi = chkDuckAi.Checked;
            settings.DuckHeadset = chkDuckHs.Checked;
            settings.AutoStart = chkAutoStart.Checked;
            if (engine != null) engine.ApplySettings(settings);
        }

        void UpdateSliderLabels()
        {
            lbCallerHs.Text = FormatDb(tbCallerHs.Value);
            lbOpPhone.Text = FormatDb(tbOpPhone.Value);
            lbAiCaller.Text = FormatDb(tbAiCaller.Value);
            lbAiOp.Text = FormatDb(tbAiOp.Value);
        }

        static string FormatDb(int db)
        {
            return (db > 0 ? "+" : "") + db + " dB";
        }

        sealed class DeviceItem
        {
            public readonly AudioDeviceInfo Info;
            readonly string display;

            public DeviceItem(AudioDeviceInfo info, string display)
            {
                Info = info;
                this.display = display;
            }

            public override string ToString() { return display; }
        }

        void RefreshDevices()
        {
            List<AudioDeviceInfo> captures, renders;
            try
            {
                captures = AudioDevices.List(true);
                renders = AudioDevices.List(false);
            }
            catch (Exception ex)
            {
                Log.Write("機器の一覧を取得できませんでした：" + ex.Message);
                captures = new List<AudioDeviceInfo>();
                renders = new List<AudioDeviceInfo>();
            }

            // 初回だけ、仮想ケーブルが入っていれば自動で選んでおく。
            string aiId = settings.AiOutId, aiName = settings.AiOutName;
            if (settings.IsNew && aiId.Length == 0 && aiName.Length == 0)
            {
                foreach (AudioDeviceInfo d in renders)
                {
                    if (d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        aiId = d.Id;
                        aiName = d.Name;
                        break;
                    }
                }
            }

            Fill(cbPhoneIn, captures, settings.PhoneInId, settings.PhoneInName, false);
            Fill(cbPhoneOut, renders, settings.PhoneOutId, settings.PhoneOutName, false);
            Fill(cbMic, captures, settings.HeadsetMicId, settings.HeadsetMicName, false);
            Fill(cbSpk, renders, settings.HeadsetSpkId, settings.HeadsetSpkName, false);
            Fill(cbAi, renders, aiId, aiName, true);
            Log.Write("機器の一覧：録音 " + captures.Count + " 台、再生 " + renders.Count + " 台");
        }

        static void Fill(ComboBox cb, List<AudioDeviceInfo> list, string id, string name, bool allowNone)
        {
            cb.BeginUpdate();
            cb.Items.Clear();
            if (allowNone) cb.Items.Add(new DeviceItem(null, "（使わない）"));
            AudioDeviceInfo match = AudioDevices.Find(list, id, name);
            DeviceItem selected = null;
            foreach (AudioDeviceInfo d in list)
            {
                var item = new DeviceItem(d, d.Name);
                cb.Items.Add(item);
                if (d == match) selected = item;
            }
            if (selected == null && (id.Length > 0 || name.Length > 0))
            {
                // 保存してある機器が今つながっていない。設定は消さずに残す。
                selected = new DeviceItem(new AudioDeviceInfo(id, name, false), "（未接続）" + name);
                cb.Items.Add(selected);
            }
            if (selected != null) cb.SelectedItem = selected;
            else cb.SelectedIndex = allowNone ? 0 : -1;
            cb.EndUpdate();
        }

        static void Selection(ComboBox cb, out string id, out string name)
        {
            var item = cb.SelectedItem as DeviceItem;
            if (item == null || item.Info == null)
            {
                id = "";
                name = "";
            }
            else
            {
                id = item.Info.Id;
                name = item.Info.Name;
            }
        }

        void ReadSelections()
        {
            Selection(cbPhoneIn, out settings.PhoneInId, out settings.PhoneInName);
            Selection(cbPhoneOut, out settings.PhoneOutId, out settings.PhoneOutName);
            Selection(cbMic, out settings.HeadsetMicId, out settings.HeadsetMicName);
            Selection(cbSpk, out settings.HeadsetSpkId, out settings.HeadsetSpkName);
            Selection(cbAi, out settings.AiOutId, out settings.AiOutName);
        }

        void SaveSettings()
        {
            try { settings.Save(settingsPath); }
            catch (Exception ex) { Log.Write("設定を保存できませんでした：" + ex.Message); }
        }

        // ------------------------------------------------------------------ 開始・停止

        bool HasRequiredDevices()
        {
            return cbPhoneIn.SelectedItem != null && cbPhoneOut.SelectedItem != null && cbMic.SelectedItem != null && cbSpk.SelectedItem != null;
        }

        void StartEngine()
        {
            if (engine != null) return;
            ReadSelections();
            var missing = new List<string>();
            if (cbPhoneIn.SelectedItem == null) missing.Add("電話の音（白い箱のマイク側）");
            if (cbPhoneOut.SelectedItem == null) missing.Add("電話へ送る音（白い箱のスピーカー側）");
            if (cbMic.SelectedItem == null) missing.Add("ヘッドセットのマイク");
            if (cbSpk.SelectedItem == null) missing.Add("ヘッドセットのスピーカー");
            if (missing.Count > 0)
            {
                tabs.SelectedIndex = 1;
                MessageBox.Show(this, "「機器の設定」タブで次の機器を選んでください。\n\n・" + string.Join("\n・", missing.ToArray()), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string warning = CheckConflicts();
            if (warning != null && MessageBox.Show(this, warning + "\n\nこのまま開始しますか？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            SaveSettings();
            engine = new BridgeEngine(settings);
            engine.MicMute = chkMute.Checked;
            engine.Recording = chkRecord.Checked;
            engine.Start();
            SetRunningUi(true);
        }

        string CheckConflicts()
        {
            var problems = new List<string>();
            if (Same(settings.PhoneInId, settings.PhoneInName, settings.HeadsetMicId, settings.HeadsetMicName))
                problems.Add("「電話の音」と「ヘッドセットのマイク」に同じ機器が選ばれています。");
            if (Same(settings.PhoneOutId, settings.PhoneOutName, settings.HeadsetSpkId, settings.HeadsetSpkName))
                problems.Add("「電話へ送る音」と「ヘッドセットのスピーカー」に同じ機器が選ばれています。");
            if (Same(settings.AiOutId, settings.AiOutName, settings.PhoneOutId, settings.PhoneOutName))
                problems.Add("「AI基盤へ送る先」に白い箱が選ばれています（相手に自分の声が二重に届きます）。");
            if (Same(settings.AiOutId, settings.AiOutName, settings.HeadsetSpkId, settings.HeadsetSpkName))
                problems.Add("「AI基盤へ送る先」にヘッドセットが選ばれています。");
            return problems.Count == 0 ? null : string.Join("\n", problems.ToArray());
        }

        static bool Same(string idA, string nameA, string idB, string nameB)
        {
            if (idA.Length == 0 && nameA.Length == 0) return false;
            if (idA.Length > 0 && idA == idB) return true;
            return idA.Length == 0 && nameA.Length > 0 && nameA == nameB;
        }

        void StopEngine()
        {
            if (engine == null) return;
            Cursor = Cursors.WaitCursor;
            try { engine.Stop(); }
            finally { Cursor = Cursors.Default; }
            engine = null;
            SetRunningUi(false);
        }

        void SetRunningUi(bool running)
        {
            foreach (ComboBox cb in new[] { cbPhoneIn, cbPhoneOut, cbMic, cbSpk, cbAi }) cb.Enabled = !running;
            btnStart.Enabled = !running;
            btnStop.Enabled = running;
            UpdateStateLabel();
            UpdateStatusText();
        }

        void UpdateStateLabel()
        {
            if (engine == null)
            {
                lblState.Text = "停止中";
                lblState.ForeColor = SystemColors.GrayText;
                return;
            }
            string text = "動作中";
            if (chkMute.Checked) text += "（ミュート中）";
            if (chkRecord.Checked) text += "　● 録音中";
            lblState.Text = text;
            lblState.ForeColor = chkMute.Checked ? Color.DarkOrange : Color.ForestGreen;
        }

        void UpdateStatusText()
        {
            if (engine == null)
            {
                txtStatus.Text = "停止中です。「▶ 開始」を押すと、電話とヘッドセットの間で音の中継を始めます。";
                return;
            }
            var lines = engine.StatusLines();
            string text = string.Join("\r\n", lines.ToArray());
            if (txtStatus.Text != text) txtStatus.Text = text;
        }

        // ------------------------------------------------------------------ 定期更新

        void OnShown(object sender, EventArgs e)
        {
            timer.Start();
            if (!HasRequiredDevices()) tabs.SelectedIndex = 1;
            if (startNow || settings.AutoStart)
            {
                if (HasRequiredDevices()) StartEngine();
                else Log.Write("自動開始：機器が選ばれていないため開始しませんでした");
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (engine != null)
            {
                mCaller.SetPeak(engine.PhoneIn.Meter.TakePeak());
                mSelf.SetPeak(engine.HeadsetMic.Meter.TakePeak());
                mAi.SetPeak(engine.AiOut != null ? engine.AiOut.Meter.TakePeak() : 0f);
            }
            else
            {
                mCaller.SetPeak(0f);
                mSelf.SetPeak(0f);
                mAi.SetPeak(0f);
            }

            var sb = new StringBuilder();
            string line;
            for (int i = 0; i < 200 && Log.TryDequeue(out line); i++) sb.Append(line).Append("\r\n");
            if (sb.Length > 0)
            {
                if (txtLog.TextLength > 200000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 100000);
                txtLog.AppendText(sb.ToString());
            }

            if (++tickCount % 10 == 0) UpdateStatusText();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            timer.Stop();
            StopEngine();
            ReadSelections();
            SaveSettings();
        }

        // ------------------------------------------------------------------ ログ・診断

        string RecordDirectory()
        {
            return settings.RecordDir.Length > 0 ? settings.RecordDir : Path.Combine(dataDir, "recordings");
        }

        void SaveDiagnostics()
        {
            if (engine == null) ReadSelections();
            string text = Diagnostics.Build(settings, engine);
            string path = Path.Combine(dataDir, "PhoneBridge_診断_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            try
            {
                File.WriteAllText(path, text, Encoding.UTF8);
                Log.Write("診断情報を保存しました：" + path);
            }
            catch (Exception ex)
            {
                Log.Write("診断情報を保存できませんでした：" + ex.Message);
                path = null;
            }
            try
            {
                Clipboard.SetText(text);
                Log.Write("診断情報をクリップボードにもコピーしました");
            }
            catch (Exception) { }
            if (path != null)
            {
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch (Exception) { }
            }
        }

        static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log.Write("フォルダを開けませんでした：" + ex.Message); }
        }
    }

    /// <summary>音の大きさを -60dB〜0dB の棒で表示する。</summary>
    internal sealed class LevelMeter : Control
    {
        float db = -90f;

        public LevelMeter()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Height = 20;
            MinimumSize = new Size(120, 16);
        }

        public void SetPeak(float peak)
        {
            float d = peak > 1e-5f ? (float)(20.0 * Math.Log10(peak)) : -90f;
            float next = d > db ? d : Math.Max(d, db - 1.5f);
            if (Math.Abs(next - db) < 0.05f) return;
            db = next;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            var rect = ClientRectangle;
            using (var bg = new SolidBrush(Color.FromArgb(40, 40, 40))) g.FillRectangle(bg, rect);
            float frac = Math.Max(0f, Math.Min(1f, (db + 60f) / 60f));
            int w = (int)(rect.Width * frac);
            Color color = db > -3f ? Color.Red : db > -12f ? Color.Gold : Color.LimeGreen;
            using (var fg = new SolidBrush(color)) g.FillRectangle(fg, 0, 0, w, rect.Height);
            using (var tick = new Pen(Color.FromArgb(90, 90, 90)))
            {
                foreach (int mark in new[] { -48, -36, -24, -12 })
                {
                    int x = (int)(rect.Width * (mark + 60) / 60f);
                    g.DrawLine(tick, x, 0, x, rect.Height);
                }
            }
            string label = db <= -60f ? "無音" : ((int)Math.Round(db)) + " dB";
            TextRenderer.DrawText(g, label, Font, rect, Color.White, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }
}
