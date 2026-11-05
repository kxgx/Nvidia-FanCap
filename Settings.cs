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
        public string Mode = "cap";          // cap = ceiling only, fixed = constant speed, curve = temperature curves
        public int Interval = 250;           // fastest poll period in ms while policing
        public int Preempt = 75;             // take control at this temperature (0 = reactive only)
        public int Release = 45;             // hand control back below this temperature
        public int Valve = 90;               // safety valve temperature
        public int ValveReenable = 84;       // re-arm the ceiling below this temperature
        public bool ValveEnabled = true;
        public int PowerLimitW = 0;          // 0 = leave the power limit alone (curve mode: hard upper bound)
        public int Fan = -1;                 // -1 = automatic (fans that actually move)

        // curve mode ---------------------------------------------------------
        // Defaults are compiled in, so an ini written by an older version (or no
        // ini at all) still runs curve mode with these values.
        // tuned on a blower 4070 Ti SUPER: flat at 0 until 50C keeps the 0-RPM idle,
        // and the curve stays under the ~84C point where the driver forces 100%
        public static readonly double[] DefaultCurveTemp = { 35, 50, 55, 62, 70, 76, 80, 83, 86, 90 };
        public static readonly double[] DefaultCurveFan = { 0, 0, 24, 30, 37, 43, 49, 58, 72, 100 };
        public static readonly double[] DefaultPowerCurveTemp = { 60, 70, 76, 80, 83, 86, 90 };
        public static readonly double[] DefaultPowerCurveWatt = { 310, 255, 205, 170, 140, 120, 100 };

        public double[] CurveTemp = (double[])DefaultCurveTemp.Clone();          // temperature knots (C)
        public double[] CurveFan = (double[])DefaultCurveFan.Clone();            // fan percent at each knot
        public double[] PowerCurveTemp = (double[])DefaultPowerCurveTemp.Clone();// power-limit curve knots (C)
        public double[] PowerCurveWatt = (double[])DefaultPowerCurveWatt.Clone();// watts at each knot
        public double TempEma = 0.25;        // temperature EMA: lower = smoother/slower
        public int TargetTemp = 78;          // auto mode: temperature the fitted curve aims at
        public double FanRateUp = 2.0;       // max fan increase per tick (percent)
        public double FanRateDown = 1.0;     // max fan decrease per tick (percent)
        // auto mode calibration (card dependent; defaults are a measured blower)
        public double FanPerDegree = 4.5;    // fan percent needed per degree C
        public double DegPerWatt = 0.035;    // steady temperature rise per watt

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
            s.NormalizeCurves();
            return s;
        }

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "cap": Cap = Num(value, Cap); break;
                case "mode": Mode = value.ToLowerInvariant(); break;
                case "target_temp": TargetTemp = Num(value, TargetTemp); break;
                case "interval": Interval = Num(value, Interval); break;
                case "preempt": Preempt = Num(value, Preempt); break;
                case "release": Release = Num(value, Release); break;
                case "valve": Valve = Num(value, Valve); break;
                case "valve_reenable": ValveReenable = Num(value, ValveReenable); break;
                case "valve_enabled": ValveEnabled = value != "0"; break;
                case "power_limit": PowerLimitW = Num(value, PowerLimitW); break;
                case "fan": Fan = Num(value, Fan); break;
                case "curve_temp": CurveTemp = Curve.ParseList(value) ?? CurveTemp; break;
                case "curve_fan": CurveFan = Curve.ParseList(value) ?? CurveFan; break;
                case "power_curve_temp": PowerCurveTemp = Curve.ParseList(value) ?? PowerCurveTemp; break;
                case "power_curve_watt": PowerCurveWatt = Curve.ParseList(value) ?? PowerCurveWatt; break;
                case "temp_ema": TempEma = NumDouble(value, TempEma); break;
                case "fan_rate_up": FanRateUp = NumDouble(value, FanRateUp); break;
                case "fan_rate_down": FanRateDown = NumDouble(value, FanRateDown); break;
                case "fan_per_degree": FanPerDegree = NumDouble(value, FanPerDegree); break;
                case "deg_per_watt": DegPerWatt = NumDouble(value, DegPerWatt); break;
            }
        }

        /// <summary>
        /// Keep the curve pairs usable: a temperature list and its value list must
        /// have the same length, otherwise both fall back to the compiled defaults.
        /// Also keeps the smoothing/rate values inside sane bounds. Called after the
        /// file is read and right before it is written, so a half-edited ini can
        /// never reach the control loop as a contradiction.
        /// </summary>
        public void NormalizeCurves()
        {
            if (!ValidPair(CurveTemp, CurveFan))
            {
                CurveTemp = (double[])DefaultCurveTemp.Clone();
                CurveFan = (double[])DefaultCurveFan.Clone();
            }
            if (!ValidPair(PowerCurveTemp, PowerCurveWatt))
            {
                PowerCurveTemp = (double[])DefaultPowerCurveTemp.Clone();
                PowerCurveWatt = (double[])DefaultPowerCurveWatt.Clone();
            }
            TempEma = Clamp(TempEma, 0.05, 1.0);
            TargetTemp = (int)Clamp(TargetTemp, 60, 84);
            FanRateUp = Clamp(FanRateUp, 0.1, 50.0);
            FanRateDown = Clamp(FanRateDown, 0.1, 50.0);
            FanPerDegree = Clamp(FanPerDegree, 1.0, 15.0);
            DegPerWatt = Clamp(DegPerWatt, 0.005, 0.150);
        }

        private static bool ValidPair(double[] x, double[] y)
        {
            return x != null && y != null && x.Length >= 2 && x.Length == y.Length;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public void Save(string path)
        {
            NormalizeCurves();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Nvidia-FanCap settings - edit by hand or use the GUI (double click the exe).");
            sb.AppendLine("# The daemon re-reads this file within ~5 seconds of a change.");
            sb.AppendLine("#");
            sb.AppendLine("# cap            fan ceiling in percent (anything above is forced back down)");
            sb.AppendLine("# mode           cap = ceiling only, fixed = constant speed, curve = temperature curves (smooth)");
            sb.AppendLine("# interval       fastest poll period in ms while policing the fan");
            sb.AppendLine("# preempt        take control at this temperature (0 = reactive only)");
            sb.AppendLine("# release        hand control back to the driver below this temperature");
            sb.AppendLine("# valve          safety valve temperature (release the ceiling to protect the card)");
            sb.AppendLine("# valve_reenable re-arm the ceiling below this temperature");
            sb.AppendLine("# valve_enabled  1 = safety valve on (recommended), 0 = off");
            sb.AppendLine("# power_limit    GPU power limit in watts applied at start (0 = unchanged;");
            sb.AppendLine("#                curve mode treats a non-zero value as a hard upper bound)");
            sb.AppendLine("# fan            which fan to control, -1 = automatic");
            sb.AppendLine("#");
            sb.AppendLine("# curve mode: temperature -> fan percent and temperature -> power limit.");
            sb.AppendLine("# curve_temp     fan curve temperatures in C, strictly increasing");
            sb.AppendLine("# curve_fan      fan percent at each curve_temp point (same number of points)");
            sb.AppendLine("# temp_ema       temperature smoothing 0.05..1 (lower = smoother, slower)");
            sb.AppendLine("# fan_rate_up    max fan change per tick while ramping up (percent)");
            sb.AppendLine("# fan_rate_down  max fan change per tick while ramping down (percent)");
            sb.AppendLine("# power_curve_temp  power-limit curve temperatures in C, strictly increasing");
            sb.AppendLine("# power_curve_watt  watts at each power_curve_temp point (same number of points)");
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
            sb.AppendLine("curve_temp=" + Curve.Format(CurveTemp));
            sb.AppendLine("curve_fan=" + Curve.Format(CurveFan));
            sb.AppendLine("temp_ema=" + NumOut(TempEma));
            sb.AppendLine("target_temp=" + TargetTemp);
            sb.AppendLine("fan_per_degree=" + NumOut(FanPerDegree));
            sb.AppendLine("deg_per_watt=" + NumOut(DegPerWatt));
            sb.AppendLine("fan_rate_up=" + NumOut(FanRateUp));
            sb.AppendLine("fan_rate_down=" + NumOut(FanRateDown));
            sb.AppendLine("power_curve_temp=" + Curve.Format(PowerCurveTemp));
            sb.AppendLine("power_curve_watt=" + Curve.Format(PowerCurveWatt));
            File.WriteAllText(path, sb.ToString());
        }

        private static int Num(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static double NumDouble(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static string NumOut(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
