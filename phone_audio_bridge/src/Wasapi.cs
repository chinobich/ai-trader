using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace PhoneBridge
{
    // Windows 標準の音声 API (WASAPI / Core Audio) を直接呼び出すための定義。
    // 外部ライブラリを使わないので、Windows に最初から入っている C# コンパイラだけでビルドできる。

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr propVariant);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr propVariant);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out int bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int numFrames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFrames);
        [PreserveSig] int GetNextPacketSize(out int numFrames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int numFrames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int numFrames, int flags);
    }

    // IAudioSessionControl の後ろに IAudioSessionControl2 のメソッドが続く並び（COM の vtable 順）。
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out int processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    internal static class CoreAudio
    {
        public const int ClsctxAll = 0x17;
        public const int DeviceStateActive = 0x1;
        public const int ShareModeShared = 0;
        public const int StreamFlagsLoopback = 0x00020000;
        public const int StreamFlagsEventCallback = 0x00040000;
        public const int BufferFlagsDataDiscontinuity = 0x1;
        public const int BufferFlagsSilent = 0x2;

        public static Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static Guid IidAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static Guid IidAudioSessionControl = new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
        public static PropertyKey PkeyDeviceFriendlyName = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(IntPtr propVariant);

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new AudioException(hr, what);
        }

        public static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
    }

    /// <summary>音声機器まわりのエラー。利用者向けの対処方法をメッセージに含める。</summary>
    internal sealed class AudioException : Exception
    {
        public readonly int Code;

        public AudioException(string message) : base(message) { }

        public AudioException(int hr, string what) : base(Describe(hr, what)) { Code = hr; }

        static string Describe(int hr, string what)
        {
            string hint;
            switch ((uint)hr)
            {
                case 0x88890004: hint = "機器が切断されました（USBの抜け・スリープなど）"; break;
                case 0x8889000A: hint = "他のソフトがこの機器を独占しています（以前のサービスのソフトが動いていないか確認してください）"; break;
                case 0x8889000E: hint = "他のソフトがこの機器を独占しています"; break;
                case 0x88890008: hint = "この機器の音声形式に対応していません"; break;
                case 0x80070005: hint = "アクセスが拒否されました（Windowsの設定 → プライバシー → マイク で「デスクトップアプリがマイクにアクセスできるようにする」をオンにしてください）"; break;
                case 0x80070490: hint = "機器が見つかりません"; break;
                case 0x88890001: hint = "初期化されていません"; break;
                default: hint = null; break;
            }
            string text = what + " に失敗しました (0x" + hr.ToString("X8") + ")";
            return hint == null ? text : text + "：" + hint;
        }
    }

    internal enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    /// <summary>機器の音声形式（WAVEFORMATEX / WAVEFORMATEXTENSIBLE）。</summary>
    internal sealed class WaveFormatInfo
    {
        static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");

        public int Channels;
        public int SampleRate;
        public int BitsPerSample;
        public int BlockAlign;
        public SampleKind Kind;

        public static WaveFormatInfo FromPointer(IntPtr p)
        {
            var f = new WaveFormatInfo();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            f.BlockAlign = Marshal.ReadInt16(p, 12);
            f.BitsPerSample = Marshal.ReadInt16(p, 14);
            bool isFloat;
            if (tag == 0xFFFE)
            {
                var guidBytes = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), guidBytes, 0, 16);
                var sub = new Guid(guidBytes);
                if (sub == SubtypeFloat) isFloat = true;
                else if (sub == SubtypePcm) isFloat = false;
                else throw new AudioException("対応していない音声形式です（" + sub + "）");
            }
            else if (tag == 3) isFloat = true;
            else if (tag == 1) isFloat = false;
            else throw new AudioException("対応していない音声形式です（形式番号 " + tag + "）");

            if (isFloat && f.BitsPerSample == 32) f.Kind = SampleKind.Float32;
            else if (!isFloat && f.BitsPerSample == 16) f.Kind = SampleKind.Pcm16;
            else if (!isFloat && f.BitsPerSample == 24) f.Kind = SampleKind.Pcm24;
            else if (!isFloat && f.BitsPerSample == 32) f.Kind = SampleKind.Pcm32;
            else throw new AudioException("対応していない音声形式です（" + f.BitsPerSample + "bit）");
            if (f.Channels < 1 || f.SampleRate < 1000) throw new AudioException("音声形式の情報が不正です");
            return f;
        }

        public override string ToString()
        {
            return SampleRate + "Hz " + Channels + "ch " + (Kind == SampleKind.Float32 ? "float32" : BitsPerSample + "bit");
        }
    }

    internal sealed class AudioDeviceInfo
    {
        public readonly string Id;
        public readonly string Name;
        public readonly bool IsCapture;

        public AudioDeviceInfo(string id, string name, bool isCapture) { Id = id; Name = name; IsCapture = isCapture; }

        public override string ToString() { return Name; }
    }

    internal static class AudioDevices
    {
        public static List<AudioDeviceInfo> List(bool capture)
        {
            var result = new List<AudioDeviceInfo>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                CoreAudio.Check(enumerator.EnumAudioEndpoints(capture ? EDataFlow.Capture : EDataFlow.Render, CoreAudio.DeviceStateActive, out collection), "機器一覧の取得");
                int count;
                CoreAudio.Check(collection.GetCount(out count), "機器数の取得");
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) < 0) continue;
                    try
                    {
                        string id;
                        if (device.GetId(out id) < 0) continue;
                        result.Add(new AudioDeviceInfo(id, GetFriendlyName(device), capture));
                    }
                    finally { CoreAudio.Release(device); }
                }
            }
            finally
            {
                CoreAudio.Release(collection);
                CoreAudio.Release(enumerator);
            }
            return result;
        }

        /// <summary>Windows の既定の機器（見つからなければ null）。</summary>
        public static string GetDefaultId(bool capture, ERole role)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(capture ? EDataFlow.Capture : EDataFlow.Render, role, out device) < 0) return null;
                string id;
                return device.GetId(out id) < 0 ? null : id;
            }
            finally
            {
                CoreAudio.Release(device);
                CoreAudio.Release(enumerator);
            }
        }

        /// <summary>診断用：機器が Windows 上で使っている音声形式を文字列で返す。</summary>
        public static string DescribeMixFormat(string id)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                CoreAudio.Check(enumerator.GetDevice(id, out device), "機器を開く");
                Guid iid = CoreAudio.IidAudioClient;
                object obj;
                CoreAudio.Check(device.Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero, out obj), "機器の有効化");
                client = (IAudioClient)obj;
                IntPtr mix;
                CoreAudio.Check(client.GetMixFormat(out mix), "音声形式の取得");
                try { return WaveFormatInfo.FromPointer(mix).ToString(); }
                finally { Marshal.FreeCoTaskMem(mix); }
            }
            catch (Exception ex) { return "（取得できません: " + ex.Message + "）"; }
            finally
            {
                CoreAudio.Release(client);
                CoreAudio.Release(device);
                CoreAudio.Release(enumerator);
            }
        }

        /// <summary>
        /// 保存しておいた ID か名前で機器を探す。USB の挿し口を変えると ID や
        /// 「マイク (2- USB Audio Device)」のような番号が変わるので、名前は番号を除いて比べる。
        /// </summary>
        public static AudioDeviceInfo Find(List<AudioDeviceInfo> list, string id, string name)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (var d in list)
                    if (d.Id == id) return d;
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var d in list)
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
            string key = NormalizeName(name);
            foreach (var d in list)
                if (NormalizeName(d.Name) == key) return d;
            return null;
        }

        static readonly Regex InstanceNumber = new Regex(@"\(\d+- ", RegexOptions.Compiled);

        public static string NormalizeName(string name)
        {
            return InstanceNumber.Replace(name ?? "", "(").Trim().ToUpperInvariant();
        }

        static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore store;
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) < 0) return "(名前不明)";
            IntPtr pv = Marshal.AllocCoTaskMem(64);
            try
            {
                for (int i = 0; i < 64; i += 8) Marshal.WriteInt64(pv, i, 0);
                PropertyKey key = CoreAudio.PkeyDeviceFriendlyName;
                if (store.GetValue(ref key, pv) < 0) return "(名前不明)";
                const short VtLpwstr = 31;
                if (Marshal.ReadInt16(pv) != VtLpwstr) return "(名前不明)";
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) ?? "(名前不明)";
            }
            finally
            {
                CoreAudio.PropVariantClear(pv);
                Marshal.FreeCoTaskMem(pv);
                CoreAudio.Release(store);
            }
        }
    }

    /// <summary>
    /// 1 台の機器に対する共有モードの録音または再生ストリーム。
    /// 機器ごとの音声形式（サンプルレート・チャンネル数・ビット数）の違いはここで吸収し、
    /// 外側とはモノラル（または 2 系統）の float でやり取りする。
    /// </summary>
    internal sealed class WasapiStream : IDisposable
    {
        public readonly bool IsCapture;
        public WaveFormatInfo Format { get; private set; }
        public int BufferFrames { get; private set; }
        public int PeriodFrames { get; private set; }

        IMMDeviceEnumerator enumerator;
        IMMDevice device;
        IAudioClient client;
        IAudioCaptureClient captureClient;
        IAudioRenderClient renderClient;
        readonly AutoResetEvent bufferEvent = new AutoResetEvent(false);
        bool useEvent;
        bool started;
        float[] floatBuf = new float[0];
        short[] shortBuf = new short[0];
        int[] intBuf = new int[0];
        byte[] byteBuf = new byte[0];

        /// <summary>取り込み中に音の途切れ（取りこぼし）を検出した回数。</summary>
        public int Discontinuities;

        WasapiStream(bool capture) { IsCapture = capture; }

        /// <param name="loopback">再生機器の音を録音する（テスト用）。</param>
        /// <param name="useEvent">false なら呼び出し側が一定間隔で読み出す（ループバック用）。</param>
        public static WasapiStream Open(string deviceId, bool capture, bool loopback, int bufferMs, bool useEvent)
        {
            var s = new WasapiStream(capture || loopback);
            try
            {
                s.useEvent = useEvent;
                s.enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                CoreAudio.Check(s.enumerator.GetDevice(deviceId, out s.device), "機器を開く");
                Guid iid = CoreAudio.IidAudioClient;
                object obj;
                CoreAudio.Check(s.device.Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero, out obj), "機器の有効化");
                s.client = (IAudioClient)obj;

                IntPtr mix;
                CoreAudio.Check(s.client.GetMixFormat(out mix), "音声形式の取得");
                try
                {
                    s.Format = WaveFormatInfo.FromPointer(mix);
                    int flags = 0;
                    if (useEvent) flags |= CoreAudio.StreamFlagsEventCallback;
                    if (loopback) flags |= CoreAudio.StreamFlagsLoopback;
                    CoreAudio.Check(s.client.Initialize(CoreAudio.ShareModeShared, flags, bufferMs * 10000L, 0, mix, IntPtr.Zero), "音声ストリームの初期化");
                }
                finally { Marshal.FreeCoTaskMem(mix); }

                if (useEvent)
                    CoreAudio.Check(s.client.SetEventHandle(s.bufferEvent.SafeWaitHandle.DangerousGetHandle()), "イベントの設定");
                int frames;
                CoreAudio.Check(s.client.GetBufferSize(out frames), "バッファサイズの取得");
                s.BufferFrames = frames;
                long defaultPeriod, minPeriod;
                if (s.client.GetDevicePeriod(out defaultPeriod, out minPeriod) >= 0 && defaultPeriod > 0)
                    s.PeriodFrames = (int)(defaultPeriod * s.Format.SampleRate / 10000000L);
                else
                    s.PeriodFrames = s.Format.SampleRate / 100;

                Guid serviceId = s.IsCapture ? CoreAudio.IidAudioCaptureClient : CoreAudio.IidAudioRenderClient;
                object service;
                CoreAudio.Check(s.client.GetService(ref serviceId, out service), "音声サービスの取得");
                if (s.IsCapture) s.captureClient = (IAudioCaptureClient)service;
                else
                {
                    s.renderClient = (IAudioRenderClient)service;
                    s.ConfigureSession();
                }
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 音量ミキサーでの表示名を付け、Windows の「通信時に他の音を小さくする」機能の
        /// 対象外にする（仮想ケーブルへ送る音が勝手に小さくなるのを防ぐ）。失敗しても動作には影響しない。
        /// </summary>
        void ConfigureSession()
        {
            object obj = null;
            try
            {
                Guid iid = CoreAudio.IidAudioSessionControl;
                if (client.GetService(ref iid, out obj) < 0 || obj == null) return;
                var session = obj as IAudioSessionControl2;
                if (session == null) return;
                session.SetDisplayName("電話音声ブリッジ", IntPtr.Zero);
                session.SetDuckingPreference(true);
            }
            catch (Exception) { }
            finally { CoreAudio.Release(obj); }
        }

        public void Start()
        {
            CoreAudio.Check(client.Start(), "音声ストリームの開始");
            started = true;
        }

        /// <summary>次のデータ（録音）または空き（再生）を待つ。タイムアウトしたら false。</summary>
        public bool Wait(int timeoutMs)
        {
            if (useEvent) return bufferEvent.WaitOne(timeoutMs);
            Thread.Sleep(Math.Min(timeoutMs, 10));
            return true;
        }

        /// <summary>録音済みのデータをすべて読み、モノラルにして dst に入れる。戻り値はフレーム数。</summary>
        public int ReadAvailable(ref float[] dst)
        {
            int total = 0;
            while (true)
            {
                int packet;
                CoreAudio.Check(captureClient.GetNextPacketSize(out packet), "録音データの確認");
                if (packet == 0) break;
                IntPtr data;
                int frames, flags;
                long devicePosition, qpcPosition;
                CoreAudio.Check(captureClient.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition), "録音データの取得");
                try
                {
                    if (dst.Length < total + frames) Array.Resize(ref dst, Math.Max(dst.Length * 2, total + frames));
                    if ((flags & CoreAudio.BufferFlagsDataDiscontinuity) != 0) Discontinuities++;
                    if ((flags & CoreAudio.BufferFlagsSilent) != 0) Array.Clear(dst, total, frames);
                    else ToMono(data, frames, dst, total);
                }
                finally { captureClient.ReleaseBuffer(frames); }
                total += frames;
            }
            return total;
        }

        public int GetPadding()
        {
            int padding;
            CoreAudio.Check(client.GetCurrentPadding(out padding), "再生バッファの確認");
            return padding;
        }

        /// <summary>
        /// streams[0]（と streams[1]）の frames 個を再生バッファに書く。
        /// 1 系統ならすべてのチャンネルに同じ音を、2 系統なら左右に分けて出す。
        /// </summary>
        public void Write(float[][] streams, int streamCount, int frames)
        {
            IntPtr data;
            CoreAudio.Check(renderClient.GetBuffer(frames, out data), "再生バッファの取得");
            FromStreams(streams, streamCount, frames, data);
            CoreAudio.Check(renderClient.ReleaseBuffer(frames, 0), "再生バッファの書き込み");
        }

        public void WriteSilence(int frames)
        {
            if (frames <= 0) return;
            IntPtr data;
            CoreAudio.Check(renderClient.GetBuffer(frames, out data), "再生バッファの取得");
            CoreAudio.Check(renderClient.ReleaseBuffer(frames, CoreAudio.BufferFlagsSilent), "再生バッファの書き込み");
        }

        void ToMono(IntPtr src, int frames, float[] dst, int offset)
        {
            int ch = Format.Channels;
            int n = frames * ch;
            float scale = 1f / ch;
            switch (Format.Kind)
            {
                case SampleKind.Float32:
                    if (floatBuf.Length < n) floatBuf = new float[n];
                    Marshal.Copy(src, floatBuf, 0, n);
                    for (int f = 0, k = 0; f < frames; f++)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++) sum += floatBuf[k++];
                        dst[offset + f] = sum * scale;
                    }
                    break;
                case SampleKind.Pcm16:
                    if (shortBuf.Length < n) shortBuf = new short[n];
                    Marshal.Copy(src, shortBuf, 0, n);
                    for (int f = 0, k = 0; f < frames; f++)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++) sum += shortBuf[k++];
                        dst[offset + f] = sum * scale / 32768f;
                    }
                    break;
                case SampleKind.Pcm32:
                    if (intBuf.Length < n) intBuf = new int[n];
                    Marshal.Copy(src, intBuf, 0, n);
                    for (int f = 0, k = 0; f < frames; f++)
                    {
                        double sum = 0;
                        for (int c = 0; c < ch; c++) sum += intBuf[k++];
                        dst[offset + f] = (float)(sum * scale / 2147483648.0);
                    }
                    break;
                default:
                    int bytes = n * 3;
                    if (byteBuf.Length < bytes) byteBuf = new byte[bytes];
                    Marshal.Copy(src, byteBuf, 0, bytes);
                    for (int f = 0, k = 0; f < frames; f++)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++, k += 3)
                            sum += (byteBuf[k] | (byteBuf[k + 1] << 8) | ((sbyte)byteBuf[k + 2] << 16)) / 8388608f;
                        dst[offset + f] = sum * scale;
                    }
                    break;
            }
        }

        void FromStreams(float[][] streams, int streamCount, int frames, IntPtr dst)
        {
            int ch = Format.Channels;
            int n = frames * ch;
            if (floatBuf.Length < n) floatBuf = new float[n];
            float[] s0 = streams[0];
            float[] s1 = streamCount > 1 ? streams[1] : null;
            for (int f = 0, k = 0; f < frames; f++)
            {
                for (int c = 0; c < ch; c++)
                {
                    float v;
                    if (s1 == null) v = s0[f];
                    else if (ch == 1) v = 0.5f * (s0[f] + s1[f]);
                    else if (c == 0) v = s0[f];
                    else if (c == 1) v = s1[f];
                    else v = 0f;
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    floatBuf[k++] = v;
                }
            }
            switch (Format.Kind)
            {
                case SampleKind.Float32:
                    Marshal.Copy(floatBuf, 0, dst, n);
                    break;
                case SampleKind.Pcm16:
                    if (shortBuf.Length < n) shortBuf = new short[n];
                    for (int i = 0; i < n; i++) shortBuf[i] = (short)(floatBuf[i] * 32767f);
                    Marshal.Copy(shortBuf, 0, dst, n);
                    break;
                case SampleKind.Pcm32:
                    if (intBuf.Length < n) intBuf = new int[n];
                    for (int i = 0; i < n; i++) intBuf[i] = (int)(floatBuf[i] * 2147483647.0);
                    Marshal.Copy(intBuf, 0, dst, n);
                    break;
                default:
                    int bytes = n * 3;
                    if (byteBuf.Length < bytes) byteBuf = new byte[bytes];
                    for (int i = 0, k = 0; i < n; i++, k += 3)
                    {
                        int v = (int)(floatBuf[i] * 8388607f);
                        byteBuf[k] = (byte)v;
                        byteBuf[k + 1] = (byte)(v >> 8);
                        byteBuf[k + 2] = (byte)(v >> 16);
                    }
                    Marshal.Copy(byteBuf, 0, dst, bytes);
                    break;
            }
        }

        public void Dispose()
        {
            if (client != null && started)
            {
                try { client.Stop(); } catch (Exception) { }
                started = false;
            }
            CoreAudio.Release(captureClient); captureClient = null;
            CoreAudio.Release(renderClient); renderClient = null;
            CoreAudio.Release(client); client = null;
            CoreAudio.Release(device); device = null;
            CoreAudio.Release(enumerator); enumerator = null;
            bufferEvent.Close();
        }
    }

    /// <summary>音声処理スレッドを Windows のマルチメディア用優先度 (MMCSS) で動かす。</summary>
    internal static class Mmcss
    {
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [DllImport("avrt.dll")]
        static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        public static IntPtr Enter()
        {
            try
            {
                int index = 0;
                return AvSetMmThreadCharacteristics("Pro Audio", ref index);
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        public static void Leave(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { AvRevertMmThreadCharacteristics(handle); } catch (Exception) { }
        }
    }
}
