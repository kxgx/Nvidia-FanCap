using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NvidiaFanCap
{
    internal static class Program
    {
        internal const string AppName = AppInfo.Name;
        internal const string MutexName = AppInfo.MutexName;
        internal const string SettingsFileName = AppInfo.SettingsFileName;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
        private const int ATTACH_PARENT_PROCESS = -1;

        private static int Main(string[] args)
        {
            // Windows-subsystem binary: only get a console when a terminal launched us
            if (args.Length > 0) { try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { } }

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                // never die with an unhandled exception: report and exit cleanly
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "Nvidia-FanCap-error.log"), DateTime.Now + Environment.NewLine + ex); } catch { }
                try { MessageBoxW(IntPtr.Zero, "Nvidia-FanCap 错误:\n" + ex.Message, "Nvidia-FanCap", 0x0000 | 0x0010); } catch { }
                try { Console.Error.WriteLine("error: " + ex.Message); } catch { }
                return 1;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static int Run(string[] args)
        {
            string iniPath = Settings.ResolvePath();

            if (args.Length == 0 || Has(args, "--gui")) return Gui.Run(iniPath);
            if (Has(args, "--install")) return Installer.Install();
            if (Has(args, "--uninstall")) return Installer.Uninstall();
            if (Has(args, "--status")) return Status(iniPath);
            if (Has(args, "--set")) return SetValues(iniPath, args);
            return Daemon.Run(iniPath, args);
        }

        private static bool Has(string[] args, string key)
        {
            foreach (string a in args)
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int Status(string iniPath)
        {
            Settings s = Settings.Load(iniPath);
            Console.WriteLine(AppName + " " + (IntPtr.Size == 8 ? "x64" : "x86") + "  settings: " + iniPath);
            Console.WriteLine("  cap=" + s.Cap + "%  mode=" + s.Mode + "  preempt=" + s.Preempt +
                "C  release=" + s.Release + "C  valve=" + s.Valve + "C(" + (s.ValveEnabled ? "on" : "off") + ")" +
                "  power_limit=" + (s.PowerLimitW > 0 ? s.PowerLimitW + "W" : "off"));

            if (Nvml.Init() == Nvml.SUCCESS)
            {
                IntPtr device;
                if (Nvml.GetHandleByIndex(0, out device) == Nvml.SUCCESS)
                {
                    uint numFans;
                    Nvml.GetNumFans(device, out numFans);
                    StringBuilder sb = new StringBuilder("  gpu: temp=" + Nvml.ReadTemperature(device) + "C");
                    for (int f = 0; f < (int)numFans; f++)
                        sb.Append("  fan[").Append(f).Append("]=").Append(Nvml.ReadFan(device, (uint)f)).Append("%");
                    Console.WriteLine(sb.ToString());
                }
                Nvml.Shutdown();
            }
            return 0;
        }

        private static int SetValues(string iniPath, string[] args)
        {
            Settings s = Settings.Load(iniPath);
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--set", StringComparison.OrdinalIgnoreCase)) continue;
                string pair = args[++i];
                int eq = pair.IndexOf('=');
                if (eq <= 0) { Console.Error.WriteLine("bad --set: " + pair); return 2; }
                s.Set(pair.Substring(0, eq).Trim().ToLowerInvariant(), pair.Substring(eq + 1).Trim());
            }
            if (s.Cap < 1 || s.Cap > 100) { Console.Error.WriteLine("cap must be 1..100"); return 2; }
            string used = Settings.SaveSmart(s, iniPath);
            Console.WriteLine("saved " + used);
            return Status(used);
        }
    }
}
