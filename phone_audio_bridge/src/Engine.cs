using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PhoneBridge
{
    /// <summary>
    /// 再生機器 1 台ぶんの音を作る処理。inputs は各 Link から読んだ 16kHz の音、
    /// outputs に 1 系統（モノラル）または 2 系統（左右）の音を書く。
    /// </summary>
    internal delegate void MixFunc(float[][] inputs, float[][] outputs, int count);

    /// <summary>設定で選ばれた機器。USB の挿し直しで ID が変わっても名前で見つけ直す。</summary>
    internal sealed class DeviceRef
    {
        public readonly string Role;
        public readonly bool Capture;
        public readonly string Id;
        public readonly string Name;

        public DeviceRef(string role, bool capture, string id, string name)
        {
            Role = role;
            Capture = capture;
            Id = id ?? "";
            Name = name ?? "";
        }

        public bool IsSet { get { return Id.Length > 0 || Name.Length > 0; } }

        public AudioDeviceInfo Resolve()
        {
            AudioDeviceInfo found = AudioDevices.Find(AudioDevices.List(Capture), Id, Name);
            if (found == null)
                throw new AudioException("「" + Name + "」が見つかりません（USBの接続を確認してください。つながると自動で再開します）");
            return found;
        }
    }

    /// <summary>
    /// 機器 1 台を担当するスレッド。エラーや切断が起きても 2 秒ごとに開き直し続ける。
    /// </summary>
    internal abstract class Node
    {
        public readonly string Label;
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        Thread thread;
        volatile bool stopRequested;
        volatile string status = "準備中";

        protected Node(string label) { Label = label; }

        public string Status { get { return status; } protected set { status = value; } }

        protected bool StopRequested { get { return stopRequested; } }

        protected bool WaitForStop(int ms) { return stopEvent.WaitOne(ms); }

        public void Start()
        {
            thread = new Thread(ThreadMain);
            thread.IsBackground = true;
            thread.Name = Label;
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
        }

        public void RequestStop()
        {
            stopRequested = true;
            stopEvent.Set();
        }

        public void Join()
        {
            if (thread != null && !thread.Join(3000)) Log.Write(Label + "：停止を待ちきれませんでした");
        }

        void ThreadMain()
        {
            IntPtr mmcss = Mmcss.Enter();
            string lastError = null;
            try
            {
                while (!stopRequested)
                {
                    try
                    {
                        RunOnce();
                        lastError = null;
                    }
                    catch (Exception ex)
                    {
                        string message = ex is AudioException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                        Status = "エラー：" + message;
                        if (message != lastError) Log.Write(Label + "：" + message);
                        lastError = message;
                    }
                    if (!stopRequested) WaitForStop(2000);
                }
            }
            finally
            {
                Mmcss.Leave(mmcss);
                Status = "停止";
            }
        }

        /// <summary>機器を開いて停止要求まで動かす。問題があれば例外を投げる（2 秒後に再実行）。</summary>
        protected abstract void RunOnce();
    }

    /// <summary>録音機器（白い箱の録音側・ヘッドセットのマイク）から音を取り込み、各 Link に配る。</summary>
    internal sealed class CaptureNode : Node
    {
        readonly DeviceRef device;
        readonly List<Link> outputs = new List<Link>();
        public readonly Meter Meter = new Meter();

        public CaptureNode(string label, DeviceRef device) : base(label) { this.device = device; }

        /// <summary>Start より前に呼ぶこと。</summary>
        public void AddOutput(Link link) { outputs.Add(link); }

        protected override void RunOnce()
        {
            AudioDeviceInfo info = device.Resolve();
            using (WasapiStream stream = WasapiStream.Open(info.Id, true, false, 100, true))
            {
                var resampler = new SincResampler(stream.Format.SampleRate, Dsp.InternalRate, 16);
                float[] raw = new float[stream.BufferFrames + 1024];
                float[] block = new float[4096];
                stream.Start();
                Status = "動作中：" + info.Name + "（" + stream.Format + "）";
                Log.Write(Label + "：接続しました " + info.Name + "（" + stream.Format + "）");
                int timeouts = 0;
                int reportedGaps = 0;
                while (!StopRequested)
                {
                    if (!stream.Wait(200))
                    {
                        if (++timeouts >= 10) throw new AudioException("2秒以上音声が届きません");
                        continue;
                    }
                    timeouts = 0;
                    int frames = stream.ReadAvailable(ref raw);
                    if (frames == 0) continue;
                    resampler.Write(raw, 0, frames);
                    int n;
                    while ((n = resampler.Read(block, 0, block.Length)) > 0)
                    {
                        Meter.Update(block, 0, n);
                        for (int i = 0; i < outputs.Count; i++) outputs[i].Write(block, 0, n);
                    }
                    if (stream.Discontinuities != reportedGaps)
                    {
                        reportedGaps = stream.Discontinuities;
                        if (reportedGaps == 1 || reportedGaps % 100 == 0)
                            Log.Write(Label + "：取り込みの途切れを検出（累計 " + reportedGaps + " 回）");
                    }
                }
            }
        }
    }

    /// <summary>Link から音を集めて MixFunc で加工し、再生機器（ヘッドセット・白い箱・仮想ケーブル）へ出す。</summary>
    internal sealed class RenderNode : Node
    {
        readonly DeviceRef device;
        readonly Link[] inputs;
        readonly int streams;
        readonly MixFunc mix;
        readonly int targetMs;
        float[][] inBufs;
        float[][] mixBufs;
        public readonly Meter Meter = new Meter();

        public RenderNode(string label, DeviceRef device, Link[] inputs, int streams, MixFunc mix, int targetMs) : base(label)
        {
            this.device = device;
            this.inputs = inputs;
            this.streams = streams;
            this.mix = mix;
            this.targetMs = targetMs;
            inBufs = new float[inputs.Length][];
            for (int i = 0; i < inputs.Length; i++) inBufs[i] = new float[1024];
            mixBufs = new float[streams][];
            for (int s = 0; s < streams; s++) mixBufs[s] = new float[1024];
        }

        protected override void RunOnce()
        {
            AudioDeviceInfo info = device.Resolve();
            using (WasapiStream stream = WasapiStream.Open(info.Id, false, false, Math.Max(50, targetMs * 2 + 20), true))
            {
                int rate = stream.Format.SampleRate;
                var resamplers = new SincResampler[streams];
                for (int s = 0; s < streams; s++) resamplers[s] = new SincResampler(Dsp.InternalRate, rate, 16);
                foreach (Link link in inputs) link.Reset();

                int targetFrames = Math.Max(rate * targetMs / 1000, stream.PeriodFrames * 2);
                targetFrames = Math.Min(targetFrames, stream.BufferFrames);
                var deviceBufs = new float[streams][];
                for (int s = 0; s < streams; s++) deviceBufs[s] = new float[stream.BufferFrames];

                stream.WriteSilence(targetFrames);
                stream.Start();
                Status = "動作中：" + info.Name + "（" + stream.Format + "）";
                Log.Write(Label + "：接続しました " + info.Name + "（" + stream.Format + "、バッファ " + (targetFrames * 1000 / rate) + "ms）");
                int timeouts = 0;
                while (!StopRequested)
                {
                    if (!stream.Wait(200))
                    {
                        if (++timeouts >= 10) throw new AudioException("再生機器が2秒以上応答しません");
                        continue;
                    }
                    timeouts = 0;
                    int want = targetFrames - stream.GetPadding();
                    if (want <= 0) continue;
                    Fill(resamplers, deviceBufs, want);
                    stream.Write(deviceBufs, streams, want);
                }
            }
        }

        /// <summary>機器のサンプリング周波数で want フレームぶんの音を作る。</summary>
        void Fill(SincResampler[] resamplers, float[][] deviceBufs, int want)
        {
            int produced = 0;
            while (produced < want)
            {
                int got = resamplers[0].Read(deviceBufs[0], produced, want - produced);
                for (int s = 1; s < streams; s++) resamplers[s].Read(deviceBufs[s], produced, want - produced);
                produced += got;
                if (produced < want)
                {
                    int need = Math.Max(1, resamplers[0].InputNeeded(want - produced));
                    Pull(need);
                    for (int s = 0; s < streams; s++) resamplers[s].Write(mixBufs[s], 0, need);
                }
            }
        }

        /// <summary>16kHz で count 個ぶん、各 Link から読んで混ぜる。</summary>
        void Pull(int count)
        {
            if (inBufs.Length > 0 && inBufs[0].Length < count)
            {
                for (int i = 0; i < inBufs.Length; i++) inBufs[i] = new float[count * 2];
            }
            if (mixBufs[0].Length < count)
            {
                for (int s = 0; s < streams; s++) mixBufs[s] = new float[count * 2];
            }
            for (int i = 0; i < inputs.Length; i++) inputs[i].Read(inBufs[i], 0, count);
            mix(inBufs, mixBufs, count);
            for (int s = 0; s < streams; s++) Meter.Update(mixBufs[s], 0, count);
        }
    }

    /// <summary>
    /// 通話の録音（WAV、左=相手・右=自分、16kHz）。機器を持たないので PC の時計で動く。
    /// 録音しないときも Link を読み続けて、溜まりすぎないようにしている。
    /// </summary>
    internal sealed class RecorderNode : Node
    {
        readonly Link caller;
        readonly Link self;
        readonly string directory;
        readonly Func<bool> isRecording;
        WavWriter writer;

        public RecorderNode(string label, Link caller, Link self, string directory, Func<bool> isRecording) : base(label)
        {
            this.caller = caller;
            this.self = self;
            this.directory = directory;
            this.isRecording = isRecording;
        }

        protected override void RunOnce()
        {
            caller.Reset();
            self.Reset();
            float[] left = new float[Dsp.InternalRate];
            float[] right = new float[Dsp.InternalRate];
            Stopwatch clock = Stopwatch.StartNew();
            long done = 0;
            Status = "録音していません";
            try
            {
                while (!StopRequested)
                {
                    WaitForStop(20);
                    long due = (long)(clock.Elapsed.TotalSeconds * Dsp.InternalRate) - done;
                    if (due <= 0) continue;
                    if (due > left.Length)
                    {
                        done += due - left.Length;
                        due = left.Length;
                    }
                    int n = (int)due;
                    caller.Read(left, 0, n);
                    self.Read(right, 0, n);
                    done += n;

                    if (isRecording())
                    {
                        if (writer == null) OpenWriter();
                        writer.Write(left, right, n);
                    }
                    else if (writer != null) CloseWriter();
                }
            }
            finally { CloseWriter(); }
        }

        void OpenWriter()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "通話_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
            writer = new WavWriter(path, 2, Dsp.InternalRate);
            Status = "録音中：" + path;
            Log.Write("録音を開始しました：" + path);
        }

        void CloseWriter()
        {
            if (writer == null) return;
            long seconds = writer.DataBytes / (Dsp.InternalRate * 4);
            writer.Dispose();
            Log.Write("録音を終了しました（" + seconds + "秒）：" + writer.FilePath);
            writer = null;
            Status = "録音していません";
        }
    }

    /// <summary>
    /// 全体の配線。
    ///   白い箱(録音側) ─┬→ ヘッドセット(再生)            … 相手の声を聞く
    ///                   ├→ 仮想ケーブル → AI基盤         … 相手の声
    ///                   └→ 録音
    ///   ヘッドセット(マイク) ─┬→ 白い箱(再生側)         … 自分の声を相手に届ける
    ///                         ├→ 仮想ケーブル → AI基盤   … 自分の声
    ///                         └→ 録音
    /// </summary>
    internal sealed class BridgeEngine
    {
        readonly Settings cfg;
        readonly List<Node> nodes = new List<Node>();
        readonly List<Link> links = new List<Link>();

        volatile float callerToHeadset = 1f;
        volatile float operatorToPhone = 1f;
        volatile float aiCaller = 1f;
        volatile float aiOperator = 1f;
        volatile float duckThreshold = 0.02f;
        volatile float duckDepth = 0.18f;
        volatile bool aiAgc = true;
        volatile bool aiStereo;
        volatile bool duckAi;
        volatile bool duckHeadset;
        volatile bool micMute;
        volatile bool recording;

        public CaptureNode PhoneIn { get; private set; }
        public CaptureNode HeadsetMic { get; private set; }
        public RenderNode PhoneOut { get; private set; }
        public RenderNode HeadsetSpk { get; private set; }
        public RenderNode AiOut { get; private set; }
        public RecorderNode Recorder { get; private set; }
        public bool Running { get; private set; }

        public bool MicMute { get { return micMute; } set { micMute = value; } }
        public bool Recording { get { return recording; } set { recording = value; } }

        public BridgeEngine(Settings cfg)
        {
            this.cfg = cfg;
            ApplySettings(cfg);
        }

        /// <summary>音量などの設定を動作中に反映する（機器の変更は開始し直しが必要）。</summary>
        public void ApplySettings(Settings s)
        {
            callerToHeadset = Dsp.DbToGain(s.CallerToHeadsetDb);
            operatorToPhone = Dsp.DbToGain(s.OperatorToPhoneDb);
            aiCaller = Dsp.DbToGain(s.AiCallerDb);
            aiOperator = Dsp.DbToGain(s.AiOperatorDb);
            duckThreshold = Dsp.DbToGain(s.DuckThresholdDb);
            duckDepth = Dsp.DbToGain(s.DuckDepthDb);
            aiAgc = s.AiAgc;
            aiStereo = s.AiStereo;
            duckAi = s.DuckAi;
            duckHeadset = s.DuckHeadset;
        }

        public string RecordDirectory
        {
            get { return cfg.RecordDir.Length > 0 ? cfg.RecordDir : Path.Combine(Settings.DataDir(), "recordings"); }
        }

        Link NewLink(string name, int targetMs)
        {
            var link = new Link(name, targetMs);
            links.Add(link);
            return link;
        }

        public void Start()
        {
            if (Running) return;
            var phoneInRef = new DeviceRef("電話の音", true, cfg.PhoneInId, cfg.PhoneInName);
            var phoneOutRef = new DeviceRef("電話へ送る音", false, cfg.PhoneOutId, cfg.PhoneOutName);
            var micRef = new DeviceRef("ヘッドセットのマイク", true, cfg.HeadsetMicId, cfg.HeadsetMicName);
            var spkRef = new DeviceRef("ヘッドセットのスピーカー", false, cfg.HeadsetSpkId, cfg.HeadsetSpkName);
            var aiRef = new DeviceRef("AI基盤へ（仮想ケーブル）", false, cfg.AiOutId, cfg.AiOutName);
            int linkMs = cfg.LinkTargetMs;
            int renderMs = cfg.RenderTargetMs;

            PhoneIn = new CaptureNode("電話の音（白い箱）", phoneInRef);
            HeadsetMic = new CaptureNode("自分の声（ヘッドセット）", micRef);

            // 相手の声 → ヘッドセット
            Link callerToSpk = NewLink("相手→ヘッドセット", linkMs);
            Link micToSpk = NewLink("自分→ヘッドセット（エコー抑制の判定用）", linkMs);
            PhoneIn.AddOutput(callerToSpk);
            HeadsetMic.AddOutput(micToSpk);
            var spkGain = new GainRamp(callerToHeadset);
            var spkDucker = new Ducker();
            HeadsetSpk = new RenderNode("ヘッドセットへ", spkRef, new[] { callerToSpk, micToSpk }, 1, (inp, outp, n) =>
            {
                spkGain.Apply(inp[0], outp[0], n, callerToHeadset);
                spkDucker.Process(outp[0], inp[1], n, duckThreshold, duckDepth, duckHeadset && !micMute);
                Dsp.Limit(outp[0], n);
            }, renderMs);

            // 自分の声 → 電話
            Link micToPhone = NewLink("自分→電話", linkMs);
            HeadsetMic.AddOutput(micToPhone);
            var phoneGain = new GainRamp(operatorToPhone);
            PhoneOut = new RenderNode("電話へ（白い箱）", phoneOutRef, new[] { micToPhone }, 1, (inp, outp, n) =>
            {
                phoneGain.Apply(inp[0], outp[0], n, micMute ? 0f : operatorToPhone);
                Dsp.Limit(outp[0], n);
            }, renderMs);

            // 相手＋自分 → 仮想ケーブル → AI基盤
            if (aiRef.IsSet)
            {
                Link callerToAi = NewLink("相手→AI基盤", linkMs);
                Link micToAi = NewLink("自分→AI基盤", linkMs);
                PhoneIn.AddOutput(callerToAi);
                HeadsetMic.AddOutput(micToAi);
                var callerAgc = new Agc();
                var selfAgc = new Agc();
                var callerGain = new GainRamp(aiCaller);
                var selfGain = new GainRamp(aiOperator);
                var aiDucker = new Ducker();
                float[] c = new float[0];
                float[] m = new float[0];
                AiOut = new RenderNode("AI基盤へ（仮想ケーブル）", aiRef, new[] { callerToAi, micToAi }, 2, (inp, outp, n) =>
                {
                    if (c.Length < n)
                    {
                        c = new float[n * 2];
                        m = new float[n * 2];
                    }
                    Array.Copy(inp[0], c, n);
                    Array.Copy(inp[1], m, n);
                    if (aiAgc)
                    {
                        callerAgc.Process(c, n);
                        selfAgc.Process(m, n);
                    }
                    callerGain.Apply(c, c, n, aiCaller);
                    selfGain.Apply(m, m, n, micMute ? 0f : aiOperator);
                    aiDucker.Process(c, inp[1], n, duckThreshold, duckDepth, duckAi && !micMute);
                    if (aiStereo)
                    {
                        Array.Copy(c, outp[0], n);
                        Array.Copy(m, outp[1], n);
                    }
                    else
                    {
                        float[] l = outp[0], r = outp[1];
                        for (int k = 0; k < n; k++)
                        {
                            float v = c[k] + m[k];
                            l[k] = v;
                            r[k] = v;
                        }
                    }
                    Dsp.Limit(outp[0], n);
                    Dsp.Limit(outp[1], n);
                }, renderMs);
            }

            // 録音（PC の時計で読むので、揺れに備えて多めに溜める）
            Link callerToRec = NewLink("相手→録音", 120);
            Link micToRec = NewLink("自分→録音", 120);
            PhoneIn.AddOutput(callerToRec);
            HeadsetMic.AddOutput(micToRec);
            Recorder = new RecorderNode("録音", callerToRec, micToRec, RecordDirectory, () => recording);

            nodes.Add(HeadsetSpk);
            nodes.Add(PhoneOut);
            if (AiOut != null) nodes.Add(AiOut);
            nodes.Add(PhoneIn);
            nodes.Add(HeadsetMic);
            nodes.Add(Recorder);

            Log.Write("開始します（遅延の目安：中継 " + linkMs + "ms＋再生 " + renderMs + "ms）");
            if (!aiRef.IsSet) Log.Write("AI基盤へ送る機器が「使わない」になっています");
            foreach (Node node in nodes) node.Start();
            Running = true;
        }

        public void Stop()
        {
            if (!Running) return;
            foreach (Node node in nodes) node.RequestStop();
            foreach (Node node in nodes) node.Join();
            nodes.Clear();
            Running = false;
            Log.Write("停止しました");
        }

        public List<string> StatusLines()
        {
            var lines = new List<string>();
            foreach (Node node in nodes) lines.Add(node.Label + "：" + node.Status);
            return lines;
        }

        public string LinkReport()
        {
            var sb = new StringBuilder();
            foreach (Link link in links)
            {
                sb.AppendLine(string.Format("  {0}: 溜まり {1:0}ms / 速度補正 {2:+0.000;-0.000;0.000}% / 不足 {3} 回 / 溢れ {4} 回",
                    link.Name, link.PendingMs, (link.Adjust - 1.0) * 100.0, link.Underruns, link.Overruns));
            }
            return sb.ToString();
        }
    }

    /// <summary>困ったときに送ってもらう診断情報。</summary>
    internal static class Diagnostics
    {
        public static string Build(Settings settings, BridgeEngine engine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 電話音声ブリッジ 診断情報 ===");
            sb.AppendLine("作成日時: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("バージョン: " + Program.Version);
            sb.AppendLine("OS: " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (64bit)" : " (32bit)"));
            sb.AppendLine(".NET: " + Environment.Version + (Environment.Is64BitProcess ? " (64bitプロセス)" : " (32bitプロセス)"));
            sb.AppendLine();
            AppendDevices(sb, true);
            AppendDevices(sb, false);
            sb.AppendLine("--- 動作状況 ---");
            if (engine == null || !engine.Running) sb.AppendLine("  停止中");
            else
            {
                foreach (string line in engine.StatusLines()) sb.AppendLine("  " + line);
                sb.Append(engine.LinkReport());
            }
            sb.AppendLine();
            sb.AppendLine("--- 設定 ---");
            sb.Append(settings.ToIniText());
            return sb.ToString();
        }

        static void AppendDevices(StringBuilder sb, bool capture)
        {
            sb.AppendLine(capture ? "--- 録音機器（マイク側）---" : "--- 再生機器（スピーカー側）---");
            try
            {
                string console = AudioDevices.GetDefaultId(capture, ERole.Console);
                string comm = AudioDevices.GetDefaultId(capture, ERole.Communications);
                foreach (AudioDeviceInfo d in AudioDevices.List(capture))
                {
                    string marks = "";
                    if (d.Id == console) marks += " [既定]";
                    if (d.Id == comm) marks += " [既定の通信機器]";
                    sb.AppendLine("  " + d.Name + marks);
                    sb.AppendLine("      形式: " + AudioDevices.DescribeMixFormat(d.Id));
                    sb.AppendLine("      ID: " + d.Id);
                }
            }
            catch (Exception ex) { sb.AppendLine("  取得できません: " + ex.Message); }
            sb.AppendLine();
        }
    }
}
