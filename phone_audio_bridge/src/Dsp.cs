using System;
using System.IO;
using System.Text;

namespace PhoneBridge
{
    internal static class Dsp
    {
        /// <summary>内部処理のサンプリング周波数。電話音声・音声認識ともに 16kHz あれば十分。</summary>
        public const int InternalRate = 16000;

        public static float DbToGain(double db) { return (float)Math.Pow(10.0, db / 20.0); }

        /// <summary>-3dBFS を超えた部分をなだらかに潰し、音割れ（0dBFS 超え）を防ぐ。</summary>
        public static void Limit(float[] buf, int count)
        {
            const float Knee = 0.7f;
            const float Range = 1f - Knee;
            for (int i = 0; i < count; i++)
            {
                float x = buf[i];
                float ax = x < 0 ? -x : x;
                if (ax <= Knee) continue;
                float y = Knee + Range * (float)Math.Tanh((ax - Knee) / Range);
                buf[i] = x < 0 ? -y : y;
            }
        }
    }

    /// <summary>
    /// 窓付き sinc 補間によるサンプリング周波数変換。変換比は動作中に少しだけ変えられる
    /// （別々の機器の時計のずれを吸収するため）。スレッドセーフではない。
    /// </summary>
    internal sealed class SincResampler
    {
        // 補間位置の端数を Phases 段階に分け、段階ごとの係数を前計算しておく（ポリフェーズ）。
        // 段階の間は線形補間するので、実質的な位置の分解能はさらに細かい。
        const int Phases = 256;
        readonly float[] coeffs;
        readonly float[] diffs;
        readonly int halfTaps;
        readonly int taps;
        readonly double baseStep;
        float[] hist = new float[4096];
        int histLen;
        double pos;
        double adjust = 1.0;

        /// <param name="zeroCrossings">片側の零点数。大きいほど高品質・高負荷。</param>
        public SincResampler(double inRate, double outRate, int zeroCrossings)
        {
            baseStep = inRate / outRate;
            // 出力側のナイキスト周波数の 92% で帯域制限する（折り返し雑音の防止）。
            double cutoff = 0.5 * Math.Min(1.0, outRate / inRate) * 0.92;
            double halfWidth = zeroCrossings / (2.0 * cutoff);
            halfTaps = (int)Math.Ceiling(halfWidth);
            taps = 2 * halfTaps;
            coeffs = new float[(Phases + 1) * taps];
            for (int p = 0; p <= Phases; p++)
            {
                for (int k = 0; k < taps; k++)
                {
                    // 出力位置から k 番目の入力サンプルまでの距離
                    double x = Math.Abs((double)p / Phases + (halfTaps - 1 - k));
                    if (x >= halfWidth) continue;
                    double u = 2.0 * cutoff * x;
                    double sinc = u == 0 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
                    double w = x / halfWidth;
                    double blackman = 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2 * Math.PI * w);
                    coeffs[p * taps + k] = (float)(2.0 * cutoff * sinc * blackman);
                }
            }
            diffs = new float[Phases * taps];
            for (int i = 0; i < diffs.Length; i++) diffs[i] = coeffs[i + taps] - coeffs[i];
            Reset();
        }

        /// <summary>変換比の微調整（1.0 = 公称どおり、1.001 = 入力を 0.1% 速く消費）。</summary>
        public double Adjust { get { return adjust; } set { adjust = value; } }

        /// <summary>まだ出力に使われていない入力サンプル数。</summary>
        public double Pending { get { return histLen - pos; } }

        public void Reset()
        {
            Array.Clear(hist, 0, hist.Length);
            histLen = halfTaps;
            pos = halfTaps;
            adjust = 1.0;
        }

        public void Write(float[] src, int offset, int count)
        {
            if (histLen + count > hist.Length)
            {
                int size = hist.Length;
                while (size < histLen + count) size *= 2;
                Array.Resize(ref hist, size);
            }
            Array.Copy(src, offset, hist, histLen, count);
            histLen += count;
        }

        /// <summary>古い入力を count サンプル読み飛ばす（溜まりすぎたときの遅延解消用）。</summary>
        public void Discard(int count)
        {
            double limit = histLen - halfTaps - 1;
            pos = Math.Min(pos + count, Math.Max(pos, limit));
            Compact();
        }

        /// <summary>あと outCount 個出力するのに追加で必要な入力サンプル数。</summary>
        public int InputNeeded(int outCount)
        {
            if (outCount <= 0) return 0;
            double last = pos + (outCount - 1) * baseStep * adjust;
            int need = (int)last + halfTaps + 1 - histLen;
            return need > 0 ? need + 1 : 0;
        }

        /// <summary>今ある入力から最大 count 個を出力する。戻り値は出力した数。</summary>
        public int Read(float[] dst, int offset, int count)
        {
            double step = baseStep * adjust;
            float[] h = hist;
            float[] c = coeffs;
            float[] d = diffs;
            int n = taps;
            int ht = halfTaps;
            int produced = 0;
            while (produced < count)
            {
                int i0 = (int)pos;
                if (i0 + ht >= histLen) break;
                double phase = (pos - i0) * Phases;
                int p = (int)phase;
                float f = (float)(phase - p);
                int b = p * n;
                int s = i0 - ht + 1;
                float acc = 0f;
                for (int k = 0; k < n; k++) acc += h[s + k] * (c[b + k] + f * d[b + k]);
                dst[offset + produced] = acc;
                produced++;
                pos += step;
            }
            Compact();
            return produced;
        }

        void Compact()
        {
            int drop = (int)pos - halfTaps;
            if (drop <= 0) return;
            if (drop > histLen) drop = histLen;
            Array.Copy(hist, drop, hist, 0, histLen - drop);
            histLen -= drop;
            pos -= drop;
        }
    }

    /// <summary>
    /// ある機器の録音スレッドから別の機器の再生スレッドへ音を渡す橋渡しバッファ（16kHz）。
    /// 2 台の機器は時計がわずかにずれているため、溜まり具合を見て読み出し速度を
    /// 最大 ±0.5% だけ調整し、長時間の通話でも遅延が伸びたり音が途切れたりしないようにする。
    /// </summary>
    internal sealed class Link
    {
        readonly object sync = new object();
        readonly SincResampler rs = new SincResampler(Dsp.InternalRate, Dsp.InternalRate, 8);
        readonly int target;
        readonly int max;
        bool buffering = true;
        double average;
        int underruns;
        int overruns;

        public readonly string Name;

        public Link(string name, int targetMs)
        {
            Name = name;
            target = Math.Max(16, Dsp.InternalRate * targetMs / 1000);
            max = target * 4 + Dsp.InternalRate / 10;
        }

        public int TargetSamples { get { return target; } }
        public int Underruns { get { lock (sync) return underruns; } }
        public int Overruns { get { lock (sync) return overruns; } }
        public double PendingMs { get { lock (sync) return rs.Pending * 1000.0 / Dsp.InternalRate; } }
        public double Adjust { get { lock (sync) return rs.Adjust; } }

        public void Reset()
        {
            lock (sync)
            {
                rs.Reset();
                buffering = true;
            }
        }

        public void Write(float[] src, int offset, int count)
        {
            lock (sync)
            {
                rs.Write(src, offset, count);
                double pending = rs.Pending;
                if (pending > max)
                {
                    rs.Discard((int)(pending - target));
                    average = target;
                    overruns++;
                }
            }
        }

        /// <summary>count 個を読み出す。足りなければ無音で埋める。</summary>
        public void Read(float[] dst, int offset, int count)
        {
            lock (sync)
            {
                double pending = rs.Pending;
                if (buffering)
                {
                    if (pending < target)
                    {
                        Array.Clear(dst, offset, count);
                        return;
                    }
                    buffering = false;
                    average = pending;
                }
                average += (pending - average) * 0.02;
                double adj = 1.0 + (average - target) / target * 0.005;
                if (adj > 1.005) adj = 1.005;
                else if (adj < 0.995) adj = 0.995;
                rs.Adjust = adj;
                int got = rs.Read(dst, offset, count);
                if (got < count)
                {
                    Array.Clear(dst, offset + got, count - got);
                    underruns++;
                    buffering = true;
                }
            }
        }
    }

    /// <summary>
    /// 自動音量調整。声の大きさを -20dBFS 付近にそろえる（上げるのは最大 +18dB まで）。
    /// 無音（-50dBFS 未満）のときはゲインを動かさないので、雑音を持ち上げ続けることはない。
    /// </summary>
    internal sealed class Agc
    {
        const float TargetRms = 0.1f;
        const float GateRms = 0.0032f;
        const float MaxGain = 8f;
        const float MinGain = 0.5f;
        const int Block = Dsp.InternalRate / 100;
        float gain = 1f;

        public float Gain { get { return gain; } }

        public void Process(float[] buf, int count)
        {
            for (int off = 0; off < count; off += Block)
            {
                int n = Math.Min(Block, count - off);
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float x = buf[off + i];
                    sum += x * x;
                }
                float rms = (float)Math.Sqrt(sum / n);
                float before = gain;
                if (rms > GateRms)
                {
                    float desired = TargetRms / rms;
                    if (desired > MaxGain) desired = MaxGain;
                    else if (desired < MinGain) desired = MinGain;
                    // 下げるのは速く（約0.2秒）、上げるのはゆっくり（約2.5秒）。
                    float rate = desired < gain ? 0.05f : 0.004f;
                    gain += (desired - gain) * rate;
                }
                float delta = (gain - before) / n;
                float g = before;
                for (int i = 0; i < n; i++)
                {
                    g += delta;
                    buf[off + i] *= g;
                }
            }
        }
    }

    /// <summary>
    /// 自分が話している間だけ、相手側の音を下げる。電話機が自分の声を相手側の音に
    /// 少し混ぜて返してくる（側音）ために起きる「二重に聞こえる」問題の対策。
    /// </summary>
    internal sealed class Ducker
    {
        const float EnvAttack = 0.02f;
        const float EnvRelease = 0.9995f;
        const float GainDown = 0.01f;
        const float GainUp = 0.0006f;
        float env;
        float gain = 1f;

        public float Gain { get { return gain; } }

        public void Process(float[] target, float[] control, int count, float threshold, float depth, bool enabled)
        {
            for (int i = 0; i < count; i++)
            {
                float a = control[i];
                if (a < 0) a = -a;
                if (a > env) env += (a - env) * EnvAttack;
                else env *= EnvRelease;
                float want = enabled && env > threshold ? depth : 1f;
                gain += (want - gain) * (want < gain ? GainDown : GainUp);
                target[i] *= gain;
            }
        }
    }

    /// <summary>音量をなめらかに変える（スライダー操作やミュートで「プツッ」と鳴らないように）。</summary>
    internal sealed class GainRamp
    {
        float current;

        public GainRamp(float initial) { current = initial; }

        public void Apply(float[] src, float[] dst, int count, float target)
        {
            float g = current;
            if (Math.Abs(target - g) < 1e-5f)
            {
                for (int i = 0; i < count; i++) dst[i] = src[i] * target;
                current = target;
                return;
            }
            for (int i = 0; i < count; i++)
            {
                g += (target - g) * 0.005f;
                dst[i] = src[i] * g;
            }
            current = g;
        }
    }

    /// <summary>画面のレベルメーター用。音声スレッドが書き、画面スレッドが読む。</summary>
    internal sealed class Meter
    {
        volatile float peak;

        public void Update(float[] buf, int offset, int count)
        {
            float p = peak;
            for (int i = 0; i < count; i++)
            {
                float a = buf[offset + i];
                if (a < 0) a = -a;
                if (a > p) p = a;
            }
            peak = p;
        }

        /// <summary>前回呼んでからの最大値を返し、リセットする。</summary>
        public float TakePeak()
        {
            float p = peak;
            peak = 0f;
            return p;
        }
    }

    /// <summary>16bit PCM の WAV ファイル書き出し。1 秒ごとにヘッダを更新するので、途中で落ちても再生できる。</summary>
    internal sealed class WavWriter : IDisposable
    {
        readonly FileStream stream;
        readonly int channels;
        readonly int rate;
        long dataBytes;
        long lastHeaderUpdate;
        byte[] buf = new byte[0];

        public readonly string FilePath;

        public WavWriter(string path, int channels, int rate)
        {
            FilePath = path;
            this.channels = channels;
            this.rate = rate;
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader();
        }

        public long DataBytes { get { return dataBytes; } }

        void WriteHeader()
        {
            var header = new byte[44];
            using (var ms = new MemoryStream(header))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write((int)Math.Min(int.MaxValue, 36 + dataBytes));
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write((int)Math.Min(int.MaxValue, dataBytes));
            }
            long position = stream.Position;
            stream.Position = 0;
            stream.Write(header, 0, header.Length);
            stream.Position = Math.Max(position, header.Length);
        }

        /// <summary>channels が 1 のときは right を無視する。</summary>
        public void Write(float[] left, float[] right, int count)
        {
            int bytes = count * channels * 2;
            if (buf.Length < bytes) buf = new byte[bytes];
            int k = 0;
            for (int i = 0; i < count; i++)
            {
                k = Put(buf, k, left[i]);
                if (channels == 2) k = Put(buf, k, right[i]);
            }
            stream.Write(buf, 0, bytes);
            dataBytes += bytes;
            if (dataBytes - lastHeaderUpdate >= rate * channels * 2)
            {
                WriteHeader();
                stream.Flush();
                lastHeaderUpdate = dataBytes;
            }
        }

        static int Put(byte[] b, int k, float v)
        {
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            short s = (short)(v * 32767f);
            b[k] = (byte)s;
            b[k + 1] = (byte)(s >> 8);
            return k + 2;
        }

        public void Dispose()
        {
            WriteHeader();
            stream.Dispose();
        }
    }
}
