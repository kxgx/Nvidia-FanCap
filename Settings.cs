using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NvidiaFanCap
{
    /// <summary>Shared application constants (compiled into both projects).</summary>
    public static class AppInfo
    {
        public const string Name = "Nvidia-FanCap";
        public const string ExeName = "Nvidia-FanCap-x64.exe";
        public const string TaskName = "NvidiaFanCap";
        public const string MutexName = "Nvidia-FanCap-SingleInstance";
        public const string SettingsFileName = "Nvidia-FanCap.ini";
    }

    /// <summary>
    /// User settings, stored as a plain key=value file next to the daemon executable.
    /// The GUI writes it, the daemon hot-reloads it within ~5 seconds.
    /// </summary>
    public sealed class Settings
    {
        /// <summary>
        /// Per-user override folder: used when the settings file next to the
        /// executable is read-only (e.g. an MSI install under Program Files).
        /// </summary>
        public static string OverridePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nvidia-FanCap");
            return Path.Combine(dir, AppInfo.SettingsFileName);
        }

        /// <summary>
        /// Where to read settings from. The per-user override wins when it exists,
        /// otherwise the file next to the executable (portable layout). The GUI and
        /// the daemon use the same rule, so they always agree on the file.
        /// </summary>
        public static string ResolvePath()
        {
            string over = OverridePath();
            if (File.Exists(over)) return over;
            return Path.Combine(AppContext.BaseDirectory, AppInfo.SettingsFileName);
        }

        /// <summary>
        /// Save, falling back to the per-user location when the preferred path is
        /// not writable (Program Files for a normal user). Returns the path used.
        /// Writability is probed first so no exception is needed for control flow.
        /// </summary>
        public static string SaveSmart(Settings s, string preferred)
        {
            string target = IsWritable(preferred) ? preferred : OverridePath();
            if (!string.Equals(target, preferred, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(target)); } catch { }
            }
            try { s.Save(target); return target; }
            catch
            {
                string over = OverridePath();
                try { Directory.CreateDirectory(Path.GetDirectoryName(over)); } catch { }
                s.Save(over);
                return over;
            }
        }

        private static bool IsWritable(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
                return true;
            }
            catch { return false; }
        }

        public int Cap = 40;
        public string Mode = "cap";          // cap = ceiling only, fixed = constant speed
        public int Interval = 250;           // fastest poll period in ms while policing
        public int Preempt = 75;             // take control at this temperature (0 = reactive only)
        public int Release = 45;             // hand control back below this temperature
        public int Valve = 90;               // safety valve temperature
        public int ValveReenable = 84;       // re-arm the ceiling below this temperature
        public bool ValveEnabled = true;
        public int PowerLimitW = 0;          // 0 = leave the power limit alone
        public int Fan = -1;                 // -1 = automatic (fans that actually move)

        public static Settings Load(string path)
        {
            Settings s = new Settings();
            if (!File.Exists(path)) return s;
            try
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    s.Set(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
            return s;
        }

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "cap": Cap = Num(value, Cap); break;
                case "mode": Mode = value.ToLowerInvariant(); break;
                case "interval": Interval = Num(value, Interval); break;
                case "preempt": Preempt = Num(value, Preempt); break;
                case "release": Release = Num(value, Release); break;
                case "valve": Valve = Num(value, Valve); break;
                case "valve_reenable": ValveReenable = Num(value, ValveReenable); break;
                case "valve_enabled": ValveEnabled = value != "0"; break;
                case "power_limit": PowerLimitW = Num(value, PowerLimitW); break;
                case "fan": Fan = Num(value, Fan); break;
            }
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Nvidia-FanCap settings - edit by hand or use the GUI (double click the exe).");
            sb.AppendLine("# The daemon re-reads this file within ~5 seconds of a change.");
            sb.AppendLine("#");
            sb.AppendLine("# cap            fan ceiling in percent (anything above is forced back down)");
            sb.AppendLine("# mode           cap = ceiling only, fixed = constant speed at cap");
            sb.AppendLine("# interval       fastest poll period in ms while policing the fan");
            sb.AppendLine("# preempt        take control at this temperature (0 = reactive only)");
            sb.AppendLine("# release        hand control back to the driver below this temperature");
            sb.AppendLine("# valve          safety valve temperature (release the ceiling to protect the card)");
            sb.AppendLine("# valve_reenable re-arm the ceiling below this temperature");
            sb.AppendLine("# valve_enabled  1 = safety valve on (recommended), 0 = off");
            sb.AppendLine("# power_limit    GPU power limit in watts applied at start (0 = unchanged)");
            sb.AppendLine("# fan            which fan to control, -1 = automatic");
            sb.AppendLine("cap=" + Cap);
            sb.AppendLine("mode=" + Mode);
            sb.AppendLine("interval=" + Interval);
            sb.AppendLine("preempt=" + Preempt);
            sb.AppendLine("release=" + Release);
            sb.AppendLine("valve=" + Valve);
            sb.AppendLine("valve_reenable=" + ValveReenable);
            sb.AppendLine("valve_enabled=" + (ValveEnabled ? 1 : 0));
            sb.AppendLine("power_limit=" + PowerLimitW);
            sb.AppendLine("fan=" + Fan);
            File.WriteAllText(path, sb.ToString());
        }

        private static int Num(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }
}
