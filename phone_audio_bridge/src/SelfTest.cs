using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PhoneBridge
{
    /// <summary>
    /// 自己テスト。
    ///   /selftest   … 機器を使わず、音声処理（周波数変換・時計ずれ補正・音量調整など）だけを確認する。
    ///   /devicetest … VB-CABLE が入った PC で、実際に仮想ケーブルへ音を流して録り直せるかを確認する。
    /// </summary>
    internal static class SelfTest
    {
        static StringBuilder report;
        static int failures;

        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        static void Check(bool ok, string name, string detail)
        {
            report.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
            if (!ok) failures++;
        }

        static void Finish(string logPath)
        {
            File.WriteAllText(logPath, report.ToString(), new UTF8Encoding(false));
            Console.Write(report.ToString());
        }

        public static int Run(string logPath)
        {
            report = new StringBuilder();
            failures = 0;
            report.AppendLine("PhoneBridge " + Program.Version + " self test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            var clock = Stopwatch.StartNew();
            try
            {
                TestResampler(48000, 16000, 1000);
                TestResampler(44100, 16000, 1000);
                TestResampler(8000, 16000, 1000);
                TestResampler(16000, 16000, 1000);
                TestResampler(16000, 48000, 1000);
                TestResampler(16000, 44100, 3000);
                TestAntiAlias();
                TestLink(0);
                TestLink(500e-6);
                TestLink(-500e-6);
                TestLinkOverflow();
                TestAgc();
                TestDucker();
                TestLimiter();
                TestWav();
                TestSettings();
                TestDeviceNameMatching();
                if (IsWindows) TestEnumerate();
            }
            catch (Exception ex) { Check(false, "unexpected exception", ex.ToString()); }
            report.AppendLine((failures == 0 ? "ALL PASSED" : failures + " FAILED") + "  (" + clock.ElapsedMilliseconds + " ms)");
            Finish(logPath);
            return failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ helpers

        static float[] Sine(int count, double freq, double rate, double amplitude, int startIndex)
        {
            var y = new float[count];
            for (int i = 0; i < count; i++) y[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * (startIndex + i) / rate));
            return y;
        }

        /// <summary>y[start..start+count) を正弦波に最小二乗で当てはめ、振幅と SN 比 (dB) を返す。</summary>
        static void FitSine(float[] y, int start, int count, double freq, double rate, out double amplitude, out double snrDb)
        {
            double w = 2 * Math.PI * freq / rate;
            double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
            for (int i = 0; i < count; i++)
            {
                double s = Math.Sin(w * i), c = Math.Cos(w * i), v = y[start + i];
                ss += s * s; sc += s * c; cc += c * c; ys += v * s; yc += v * c;
            }
            double det = ss * cc - sc * sc;
            double a = (ys * cc - yc * sc) / det;
            double b = (yc * ss - ys * sc) / det;
            amplitude = Math.Sqrt(a * a + b * b);
            double err = 0, sig = 0;
            for (int i = 0; i < count; i++)
            {
                double fit = a * Math.Sin(w * i) + b * Math.Cos(w * i);
                double e = y[start + i] - fit;
                err += e * e;
                sig += fit * fit;
            }
            snrDb = 10 * Math.Log10(sig / Math.Max(err, 1e-30));
        }

        static float[] Resample(SincResampler rs, float[] input, int chunk, int outRate)
        {
            var output = new List<float>();
            var outBuf = new float[outRate / 20 + 64];
            var buf = new float[chunk];
            for (int pos = 0; pos < input.Length; pos += chunk)
            {
                int n = Math.Min(chunk, input.Length - pos);
                Array.Copy(input, pos, buf, 0, n);
                rs.Write(buf, 0, n);
                int m;
                while ((m = rs.Read(outBuf, 0, outBuf.Length)) > 0)
                    for (int k = 0; k < m; k++) output.Add(outBuf[k]);
            }
            return output.ToArray();
        }

        static double Rms(float[] y, int start, int count)
        {
            double sum = 0;
            for (int i = start; i < start + count; i++) sum += y[i] * y[i];
            return Math.Sqrt(sum / Math.Max(1, count));
        }

        // ------------------------------------------------------------------ tests

        static void TestResampler(int inRate, int outRate, double freq)
        {
            const double Amp = 0.5;
            var rs = new SincResampler(inRate, outRate, 16);
            float[] y = Resample(rs, Sine(inRate, freq, inRate, Amp, 0), inRate / 100, outRate);
            int expected = outRate;
            int skip = outRate / 10;
            double amplitude, snr;
            FitSine(y, skip, y.Length - 2 * skip, freq, outRate, out amplitude, out snr);
            string name = "resample " + inRate + " -> " + outRate + " (" + freq + " Hz)";
            Check(Math.Abs(y.Length - expected) < 100, name + ": length", y.Length + " samples, expected about " + expected);
            Check(Math.Abs(amplitude - Amp) < 0.01 && snr > 60, name + ": quality",
                string.Format("amplitude {0:0.0000}, SNR {1:0.0} dB", amplitude, snr));
        }

        static void TestAntiAlias()
        {
            // 16kHz にすると表現できない 12kHz の音は、折り返さずに消えること。
            var rs = new SincResampler(48000, 16000, 16);
            float[] y = Resample(rs, Sine(48000, 12000, 48000, 0.5, 0), 480, 16000);
            double level = 20 * Math.Log10(Math.Max(1e-12, Rms(y, 1600, y.Length - 3200) / (0.5 / Math.Sqrt(2))));
            Check(level < -50, "resample 48000 -> 16000 removes 12 kHz", string.Format("{0:0.0} dB", level));
        }

        /// <summary>
        /// 録音側の時計が ppm だけずれていても、10 分間、音切れも遅延の増加も起きないこと。
        /// 録音側のパケットがときどき 1 回ぶん遅れて届く揺れも入れている。
        /// </summary>
        static void TestLink(double ppm)
        {
            var link = new Link("test", 30);
            var rnd = new Random(12345);
            double producerRate = Dsp.InternalRate * (1 + ppm);
            double pending = 0;
            long producerIndex = 0;
            var block = new float[1024];
            var output = new float[160];
            const int Ticks = 60000;
            const int Warmup = 3000;
            int underrunsAtWarmup = 0, overrunsAtWarmup = 0;
            double minMs = double.MaxValue, maxMs = 0, maxD2 = 0;
            float p1 = 0, p2 = 0;
            long outIndex = 0;
            bool lateLastTick = false;

            for (int t = 0; t < Ticks; t++)
            {
                pending += producerRate / 100.0;
                bool late = !lateLastTick && rnd.Next(10) == 0;
                bool produceFirst = rnd.Next(2) == 0;
                if (produceFirst && !late) Produce(link, block, ref pending, ref producerIndex, producerRate);
                if (t == Warmup)
                {
                    underrunsAtWarmup = link.Underruns;
                    overrunsAtWarmup = link.Overruns;
                }
                if (t >= Warmup)
                {
                    minMs = Math.Min(minMs, link.PendingMs);
                    maxMs = Math.Max(maxMs, link.PendingMs);
                }
                link.Read(output, 0, output.Length);
                for (int i = 0; i < output.Length; i++, outIndex++)
                {
                    if (t >= Warmup && outIndex >= 2)
                        maxD2 = Math.Max(maxD2, Math.Abs(output[i] - 2 * p1 + p2));
                    p2 = p1;
                    p1 = output[i];
                }
                if (!produceFirst && !late) Produce(link, block, ref pending, ref producerIndex, producerRate);
                lateLastTick = late;
            }

            string name = string.Format("link with clock drift {0:+0;-0;0} ppm", ppm * 1e6);
            Check(link.Underruns == underrunsAtWarmup && link.Overruns == overrunsAtWarmup, name + ": no dropouts after warm-up",
                "underruns " + (link.Underruns - underrunsAtWarmup) + ", overruns " + (link.Overruns - overrunsAtWarmup));
            Check(minMs > 5 && maxMs < 90, name + ": delay stays bounded", string.Format("{0:0.0} .. {1:0.0} ms", minMs, maxMs));
            // 440Hz・振幅0.5 の正弦波の2階差分は最大 0.015 程度。音切れがあれば大きく跳ねる。
            Check(maxD2 < 0.03, name + ": waveform is continuous", string.Format("max 2nd difference {0:0.0000}", maxD2));
        }

        static void Produce(Link link, float[] block, ref double pending, ref long index, double rate)
        {
            int n = (int)pending;
            pending -= n;
            if (n > block.Length) n = block.Length;
            for (int i = 0; i < n; i++, index++) block[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * index / rate));
            link.Write(block, 0, n);
        }

        static void TestLinkOverflow()
        {
            // 読み出し側が止まっても、溜まりすぎずに上限で捨てること（遅延が伸び続けない）。
            var link = new Link("test", 30);
            var block = new float[160];
            for (int i = 0; i < 300; i++) link.Write(block, 0, block.Length);
            Check(link.Overruns > 0 && link.PendingMs < 250, "link drops old audio when the reader stops",
                string.Format("overruns {0}, pending {1:0} ms", link.Overruns, link.PendingMs));
        }

        static void TestAgc()
        {
            var agc = new Agc();
            float[] quiet = Sine(160, 300, Dsp.InternalRate, 0.01 * Math.Sqrt(2), 0);   // -40 dBFS
            var buf = new float[160];
            for (int i = 0; i < 2000; i++)
            {
                Array.Copy(quiet, buf, 160);
                agc.Process(buf, 160);
            }
            float boosted = agc.Gain;
            Check(boosted > 7f && boosted <= 8.001f, "AGC boosts quiet speech up to +18 dB", string.Format("gain {0:0.00}", boosted));

            var rnd = new Random(1);
            for (int i = 0; i < 1000; i++)
            {
                for (int k = 0; k < 160; k++) buf[k] = (float)((rnd.NextDouble() - 0.5) * 2e-4);
                agc.Process(buf, 160);
            }
            Check(Math.Abs(agc.Gain - boosted) < 0.01f, "AGC holds its gain during silence", string.Format("gain {0:0.00}", agc.Gain));

            float[] loud = Sine(160, 300, Dsp.InternalRate, 0.5, 0);
            for (int i = 0; i < 100; i++)
            {
                Array.Copy(loud, buf, 160);
                agc.Process(buf, 160);
            }
            Check(agc.Gain < 0.6f, "AGC reduces loud speech within 1 s", string.Format("gain {0:0.00}", agc.Gain));
        }

        static void TestDucker()
        {
            var ducker = new Ducker();
            var target = new float[16000];
            float[] talk = Sine(1600, 300, Dsp.InternalRate, 0.3, 0);
            for (int i = 0; i < target.Length; i++) target[i] = 1f;
            ducker.Process(target, talk, 1600, 0.02f, 0.18f, true);
            float ducked = target[1599];
            for (int i = 0; i < target.Length; i++) target[i] = 1f;
            ducker.Process(target, new float[16000], 16000, 0.02f, 0.18f, true);
            float recovered = target[15999];
            Check(ducked < 0.25f && recovered > 0.9f, "ducking lowers the other side while talking and recovers",
                string.Format("while talking {0:0.00}, 1 s later {1:0.00}", ducked, recovered));

            var off = new Ducker();
            for (int i = 0; i < 1600; i++) target[i] = 1f;
            off.Process(target, talk, 1600, 0.02f, 0.18f, false);
            Check(Math.Abs(target[1599] - 1f) < 1e-4f, "ducking does nothing when disabled", null);
        }

        static void TestLimiter()
        {
            var y = new float[] { 2f, -2f, 0.5f, 0.8f, -0.95f };
            Dsp.Limit(y, y.Length);
            bool ok = y[0] <= 1f && y[0] > 0.9f && y[1] >= -1f && y[1] < -0.9f && y[2] == 0.5f && y[3] > 0.75f && y[3] < 0.8f;
            Check(ok, "limiter keeps peaks under 0 dBFS", string.Format("{0:0.000}, {1:0.000}, {2:0.000}, {3:0.000}", y[0], y[1], y[2], y[3]));
        }

        static void TestWav()
        {
            string path = Path.Combine(Path.GetTempPath(), "phonebridge_selftest.wav");
            using (var w = new WavWriter(path, 2, Dsp.InternalRate))
            {
                var l = Sine(1000, 440, Dsp.InternalRate, 0.5, 0);
                w.Write(l, l, 1000);
            }
            byte[] b = File.ReadAllBytes(path);
            File.Delete(path);
            bool ok = b.Length == 44 + 4000 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && BitConverter.ToInt32(b, 40) == 4000
                && BitConverter.ToInt16(b, 22) == 2 && BitConverter.ToInt32(b, 24) == Dsp.InternalRate;
            Check(ok, "WAV file header", b.Length + " bytes");
        }

        static void TestSettings()
        {
            string path = Path.Combine(Path.GetTempPath(), "phonebridge_selftest.ini");
            var s = new Settings();
            s.PhoneInName = "マイク (USB Audio Device)";
            s.PhoneInId = "{0.0.1.00000000}.{abc=def}";
            s.AiCallerDb = -5;
            s.DuckAi = true;
            s.Save(path);
            Settings t = Settings.Load(path);
            bool roundTrip = t.PhoneInName == s.PhoneInName && t.PhoneInId == s.PhoneInId && t.AiCallerDb == -5 && t.DuckAi && !t.IsNew;
            File.WriteAllText(path, "AiCallerDb=99\nLinkTargetMs=abc\n", Encoding.UTF8);
            Settings u = Settings.Load(path);
            File.Delete(path);
            Check(roundTrip && u.AiCallerDb == 20 && u.LinkTargetMs == 30, "settings save / load", null);
        }

        static void TestDeviceNameMatching()
        {
            var list = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo("id-1", "マイク (2- USB Audio Device)", true),
                new AudioDeviceInfo("id-2", "ヘッドセット マイク (Jabra EVOLVE 20)", true)
            };
            AudioDeviceInfo byName = AudioDevices.Find(list, "old-id", "マイク (USB Audio Device)");
            AudioDeviceInfo byId = AudioDevices.Find(list, "id-2", "");
            AudioDeviceInfo none = AudioDevices.Find(list, "", "存在しない機器");
            Check(byName != null && byName.Id == "id-1" && byId != null && byId.Id == "id-2" && none == null,
                "device is found again after the USB port changes", null);
        }

        static void TestEnumerate()
        {
            try
            {
                List<AudioDeviceInfo> captures = AudioDevices.List(true);
                List<AudioDeviceInfo> renders = AudioDevices.List(false);
                report.AppendLine("[INFO] audio devices: " + captures.Count + " capture, " + renders.Count + " render");
                foreach (AudioDeviceInfo d in captures) report.AppendLine("         capture: " + d.Name);
                foreach (AudioDeviceInfo d in renders) report.AppendLine("         render:  " + d.Name);
            }
            catch (Exception ex)
            {
                // 音声機器の無い環境（CI など）では失敗することがあるので、警告にとどめる。
                report.AppendLine("[WARN] could not enumerate audio devices: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ device test

        public static int RunDeviceTest(string logPath)
        {
            report = new StringBuilder();
            failures = 0;
            report.AppendLine("PhoneBridge " + Program.Version + " device test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int rc;
            try
            {
                List<AudioDeviceInfo> captures = AudioDevices.List(true);
                List<AudioDeviceInfo> renders = AudioDevices.List(false);
                AudioDeviceInfo cableIn = null, cableOut = null;
                report.AppendLine("capture devices:");
                foreach (AudioDeviceInfo d in captures)
                {
                    report.AppendLine("  " + d.Name + "  [" + AudioDevices.DescribeMixFormat(d.Id) + "]");
                    if (cableOut == null && d.Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0) cableOut = d;
                }
                report.AppendLine("render devices:");
                foreach (AudioDeviceInfo d in renders)
                {
                    report.AppendLine("  " + d.Name + "  [" + AudioDevices.DescribeMixFormat(d.Id) + "]");
                    if (cableIn == null && d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) cableIn = d;
                }
                if (cableIn == null || cableOut == null)
                {
                    report.AppendLine("VB-CABLE (CABLE Input / CABLE Output) was not found. Skipping the loop test.");
                    rc = 2;
                }
                else
                {
                    LoopTest(cableIn, cableOut);
                    rc = failures == 0 ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Check(false, "unexpected exception", ex.ToString());
                rc = 1;
            }
            string line;
            report.AppendLine("engine log:");
            while (Log.TryDequeue(out line)) report.AppendLine("  " + line);
            report.AppendLine(rc == 0 ? "DEVICE TEST PASSED" : rc == 2 ? "DEVICE TEST SKIPPED" : "DEVICE TEST FAILED");
            Finish(logPath);
            return rc;
        }

        /// <summary>
        /// 1kHz の音を本番と同じ RenderNode で CABLE Input へ流し、本番と同じ CaptureNode で
        /// CABLE Output から録り直して、音の大きさ・周波数・途切れを確かめる。
        /// </summary>
        static void LoopTest(AudioDeviceInfo cableIn, AudioDeviceInfo cableOut)
        {
            const double Amp = 0.3;
            const double Freq = 1000;
            var toneLink = new Link("tone", 60);
            var captured = new Link("captured", 100);
            var render = new RenderNode("test render", new DeviceRef("test", false, cableIn.Id, cableIn.Name), new[] { toneLink }, 1,
                (inp, outp, n) => Array.Copy(inp[0], outp[0], n), 20);
            var capture = new CaptureNode("test capture", new DeviceRef("test", true, cableOut.Id, cableOut.Name));
            capture.AddOutput(captured);

            render.Start();
            capture.Start();
            var samples = new List<float>();
            var toneBuf = new float[Dsp.InternalRate];
            var readBuf = new float[Dsp.InternalRate];
            Stopwatch clock = Stopwatch.StartNew();
            long produced = 0, consumed = 0;
            while (clock.Elapsed.TotalSeconds < 5.0)
            {
                Thread.Sleep(10);
                long due = (long)(clock.Elapsed.TotalSeconds * Dsp.InternalRate);
                int n = (int)Math.Min(due - produced, toneBuf.Length);
                if (n > 0)
                {
                    for (int i = 0; i < n; i++) toneBuf[i] = (float)(Amp * Math.Sin(2 * Math.PI * Freq * (produced + i) / Dsp.InternalRate));
                    toneLink.Write(toneBuf, 0, n);
                    produced += n;
                }
                int m = (int)Math.Min(due - consumed, readBuf.Length);
                if (m > 0)
                {
                    captured.Read(readBuf, 0, m);
                    for (int i = 0; i < m; i++) samples.Add(readBuf[i]);
                    consumed += m;
                }
            }
            report.AppendLine("status: " + render.Label + " = " + render.Status);
            report.AppendLine("status: " + capture.Label + " = " + capture.Status);
            capture.RequestStop();
            render.RequestStop();
            capture.Join();
            render.Join();

            float[] y = samples.ToArray();
            int arrival = -1;
            for (int i = 0; i < y.Length; i++)
            {
                if (Math.Abs(y[i]) > 0.02f)
                {
                    arrival = i;
                    break;
                }
            }
            if (arrival < 0)
            {
                Check(false, "tone arrives through VB-CABLE", "only silence was captured");
                return;
            }
            report.AppendLine(string.Format("[INFO] tone arrived after {0} ms (includes the test's own buffering)", arrival * 1000 / Dsp.InternalRate));
            int start = arrival + Dsp.InternalRate / 2;
            int end = y.Length - Dsp.InternalRate / 10;
            if (end - start < Dsp.InternalRate)
            {
                Check(false, "tone arrives through VB-CABLE", "signal too short");
                return;
            }
            double amplitude = Rms(y, start, end - start) * Math.Sqrt(2);
            int crossings = 0, spikes = 0;
            for (int i = start + 1; i < end; i++)
            {
                if (y[i - 1] < 0 && y[i] >= 0) crossings++;
                if (i >= start + 2 && Math.Abs(y[i] - 2 * y[i - 1] + y[i - 2]) > 0.1f) spikes++;
            }
            double freq = crossings / ((end - start) / (double)Dsp.InternalRate);
            Check(amplitude > 0.05, "tone level through VB-CABLE", string.Format("amplitude {0:0.000} (sent {1})", amplitude, Amp));
            Check(Math.Abs(freq - Freq) < 20, "tone frequency through VB-CABLE", string.Format("{0:0.0} Hz", freq));
            Check(spikes <= 3, "no dropouts through VB-CABLE", spikes + " glitches");
        }
    }
}
