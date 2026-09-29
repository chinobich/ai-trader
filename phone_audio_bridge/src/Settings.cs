using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace PhoneBridge
{
    /// <summary>
    /// 設定（PhoneBridge.ini）。公開フィールドがそのまま「名前=値」の行として保存される。
    /// </summary>
    internal sealed class Settings
    {
        // 機器の割り当て（ID が変わっても名前で見つけ直す）
        public string PhoneInId = "";
        public string PhoneInName = "";
        public string PhoneOutId = "";
        public string PhoneOutName = "";
        public string HeadsetMicId = "";
        public string HeadsetMicName = "";
        public string HeadsetSpkId = "";
        public string HeadsetSpkName = "";
        public string AiOutId = "";
        public string AiOutName = "";

        // 音量 (dB)
        public int CallerToHeadsetDb = 0;
        public int OperatorToPhoneDb = 0;
        public int AiCallerDb = 0;
        public int AiOperatorDb = 0;

        public bool AiAgc = true;
        public bool AiStereo = false;
        public bool DuckAi = false;
        public bool DuckHeadset = false;
        public bool AutoStart = false;
        public string RecordDir = "";

        // 詳しい人向けの調整値
        public int LinkTargetMs = 30;
        public int RenderTargetMs = 20;
        public int DuckThresholdDb = -34;
        public int DuckDepthDb = -15;

        /// <summary>設定ファイルがまだ無かった（初回起動）。保存はされない。</summary>
        public bool IsNew { get; private set; }

        /// <summary>設定・ログ・録音を置くフォルダ。exe の場所に書けなければ %LOCALAPPDATA%\PhoneBridge。</summary>
        public static string DataDir()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (IsWritable(exeDir)) return exeDir;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBridge");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".phonebridge_write_test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        static FieldInfo[] Fields()
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
        }

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!File.Exists(path))
            {
                s.IsNew = true;
                return s;
            }
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                FieldInfo field = typeof(Settings).GetField(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field == null) continue;
                try
                {
                    if (field.FieldType == typeof(string)) field.SetValue(s, value);
                    else if (field.FieldType == typeof(int)) field.SetValue(s, int.Parse(value, CultureInfo.InvariantCulture));
                    else if (field.FieldType == typeof(bool)) field.SetValue(s, value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
                }
                catch (FormatException) { }
                catch (OverflowException) { }
            }
            s.Clamp();
            return s;
        }

        void Clamp()
        {
            CallerToHeadsetDb = Math.Max(-20, Math.Min(20, CallerToHeadsetDb));
            OperatorToPhoneDb = Math.Max(-20, Math.Min(20, OperatorToPhoneDb));
            AiCallerDb = Math.Max(-20, Math.Min(20, AiCallerDb));
            AiOperatorDb = Math.Max(-20, Math.Min(20, AiOperatorDb));
            LinkTargetMs = Math.Max(10, Math.Min(500, LinkTargetMs));
            RenderTargetMs = Math.Max(10, Math.Min(200, RenderTargetMs));
            DuckThresholdDb = Math.Max(-70, Math.Min(0, DuckThresholdDb));
            DuckDepthDb = Math.Max(-60, Math.Min(0, DuckDepthDb));
        }

        public string ToIniText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 電話音声ブリッジ (PhoneBridge) の設定。画面で変更すると自動で保存されます。");
            foreach (FieldInfo field in Fields())
            {
                object value = field.GetValue(this);
                string text;
                if (value is bool) text = (bool)value ? "1" : "0";
                else if (value is int) text = ((int)value).ToString(CultureInfo.InvariantCulture);
                else text = (value as string ?? "").Replace("\r", "").Replace("\n", "");
                sb.Append(field.Name).Append('=').AppendLine(text);
            }
            return sb.ToString();
        }

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToIniText(), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            IsNew = false;
        }
    }
}
