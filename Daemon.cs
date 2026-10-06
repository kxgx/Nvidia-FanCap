using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NvidiaFanCap
{
    /// <summary>
    /// The enforcement loop. Owns the fan only while it has to: it grabs manual
    /// control the moment the driver/VBIOS pushes the fan above the ceiling (or
    /// when the preempt temperature is reached) and hands control back once the
    /// GPU is cool again, so idle 0-RPM behaviour keeps working.
    /// </summary>
    internal static class Daemon
    {
        private static TextWriter logFile;
        private static volatile bool running = true;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            Console.WriteLine(line);
            if (logFile != null)
            {
                try { logFile.WriteLine(line); logFile.Flush(); } catch { }
            }
        }

        internal static int Run(string iniPath, string[] args)
        {
            bool verbose = false, noRestore = false, hidden = false;
            int durationSec = 0;
            string logPath = ArgValue(args, "--log");
            Dictionary<string, int> cli = new Dictionary<string, int>();
            string cliMode = null;
            bool? cliValveEnabled = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--cap": cli["cap"] = int.Parse(args[++i]); break;
                    case "--mode": cliMode = args[++i].ToLowerInvariant(); break;
                    case "--interval": cli["interval"] = int.Parse(args[++i]); break;
                    case "--preempt-temp": cli["preempt"] = int.Parse(args[++i]); break;
                    case "--release-temp": cli["release"] = int.Parse(args[++i]); break;
                    case "--valve-temp": cli["valve"] = int.Parse(args[++i]); break;
                    case "--valve-reenable": cli["valve_reenable"] = int.Parse(args[++i]); break;
                    case "--no-valve": cliValveEnabled = false; break;
                    case "--power-limit": cli["power_limit"] = int.Parse(args[++i]); break;
                    case "--fan": cli["fan"] = int.Parse(args[++i]); break;
                    case "--duration": durationSec = int.Parse(args[++i]); break;
                    case "--no-restore": noRestore = true; break;
                    case "--verbose": verbose = true; break;
                    case "--hidden": hidden = true; break;
                    case "--set": i++; break;
                    case "--log": i++; break;
                }
            }

            if (hidden && !verbose && logPath == null)
            {
                IntPtr console = GetConsoleWindow();
                if (console != IntPtr.Zero) ShowWindow(console, 0); // SW_HIDE
            }

            if (logPath != null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                logFile = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            }

            Mutex single = new Mutex(true, Program.MutexName, out bool isFirst);
            if (!isFirst) { Log("another Nvidia-FanCap daemon is already running"); return 3; }

            int rc = Nvml.Init();
            if (rc != Nvml.SUCCESS) { Log("nvmlInit_v2 failed: " + Nvml.Describe(rc)); return 1; }

            try
            {
                IntPtr device;
                string busId = ArgValue(args, "--busid");
                rc = busId != null
                    ? Nvml.GetHandleByPciBusId(busId, out device)
                    : Nvml.GetHandleByIndex(0, out device);
                if (rc != Nvml.SUCCESS) { Log("device open failed: " + Nvml.Describe(rc)); return 1; }

                byte[] name = new byte[96];
                Nvml.GetName(device, name, (uint)name.Length);
                uint numFans;
                Nvml.GetNumFans(device, out numFans);
                Log("device: " + Encoding.ASCII.GetString(name).TrimEnd('\0') + "  fans=" + numFans +
                    "  arch=" + (IntPtr.Size == 8 ? "x64" : "x86"));

                // harmless permission probe: write back the policy already in effect
                uint policy;
                Nvml.GetFanControlPolicy(device, 0, out policy);
                if (Nvml.SetFanControlPolicy(device, 0, policy) == Nvml.INSUFFICIENT_PERMISSIONS)
                {
                    Log("需要管理员权限：守护进程必须以管理员身份运行（运行 install-task.bat 开机自启）");
                    return 5;
                }

                DateTime iniStamp = File.Exists(iniPath) ? File.GetLastWriteTimeUtc(iniPath) : DateTime.MinValue;
                Settings cfg = Merge(Settings.Load(iniPath), cli, cliMode, cliValveEnabled);

                if (cfg.PowerLimitW > 0)
                {
                    uint current, defaultValue;
                    Nvml.GetPowerLimit(device, out current);
                    Nvml.GetPowerDefaultLimit(device, out defaultValue);
                    int prc = Nvml.SetPowerLimit(device, (uint)(cfg.PowerLimitW * 1000));
                    Log("power limit: " + (current / 1000) + "W (default " + (defaultValue / 1000) + "W) -> " +
                        cfg.PowerLimitW + "W  " + Nvml.Describe(prc));
                }

                Log(string.Format("mode={0} cap={1}% preempt>={2}C release<={3}C valve>={4}C(arm<={5}C){6}",
                    cfg.Mode, cfg.Cap, cfg.Preempt, cfg.Release, cfg.Valve, cfg.ValveReenable,
                    cfg.ValveEnabled ? "" : " VALVE=OFF"));

                Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) { e.Cancel = true; running = false; };

                var held = new SortedSet<int>();
                bool valveOpen = false;
                int writeErrors = 0;
                DateTime started = DateTime.Now;
                DateTime nextConfigCheck = DateTime.UtcNow.AddSeconds(5);

                while (running)
                {
                    if (durationSec > 0 && (DateTime.Now - started).TotalSeconds >= durationSec) break;

                    // hot reload: one timestamp check every few seconds, no file reads
                    if (DateTime.UtcNow >= nextConfigCheck)
                    {
                        nextConfigCheck = DateTime.UtcNow.AddSeconds(5);
                        string current = Settings.ResolvePath();   // per-user override may have appeared
                        DateTime stamp = File.Exists(current) ? File.GetLastWriteTimeUtc(current) : DateTime.MinValue;
                        if (current != iniPath || stamp != iniStamp)
                        {
                            iniPath = current;
                            iniStamp = stamp;
                            cfg = Merge(Settings.Load(current), cli, cliMode, cliValveEnabled);
                            Log("settings reloaded: cap=" + cfg.Cap + "% preempt=" + cfg.Preempt + "C valve=" +
                                cfg.Valve + "C(" + (cfg.ValveEnabled ? "on" : "off") + ") [" + current + "]");
                        }
                    }

                    uint temp = Nvml.ReadTemperature(device);
                    uint maxSpeed = 0;
                    var spinning = new SortedSet<int>();
                    for (int f = 0; f < (int)numFans; f++)
                    {
                        uint speed = Nvml.ReadFan(device, (uint)f);
                        if (speed > 0) spinning.Add(f);
                        if (speed > maxSpeed) maxSpeed = speed;
                    }

                    // safety valve: never stand between the card and its survival
                    if (cfg.ValveEnabled && temp >= cfg.Valve)
                    {
                        if (!valveOpen)
                        {
                            Log("VALVE OPEN at " + temp + "C - ceiling disarmed, card takes over");
                            if (held.Count > 0) Release(device, held);
                            held.Clear();
                            valveOpen = true;
                        }
                    }
                    else if (valveOpen && temp <= cfg.ValveReenable)
                    {
                        Log("valve closed at " + temp + "C - ceiling re-armed");
                        valveOpen = false;
                    }

                    if (!valveOpen)
                    {
                        bool overCap = maxSpeed > cfg.Cap;
                        bool preempt = cfg.Preempt > 0 && temp >= cfg.Preempt;

                        if (cfg.Mode == "fixed")
                        {
                            if (held.Count == 0)
                            {
                                if (cfg.Fan >= 0) held.Add(cfg.Fan);
                                else for (int f = 0; f < (int)numFans; f++) held.Add(f);
                                if (!Engage(device, held)) { held.Clear(); writeErrors++; }
                            }
                            if (held.Count > 0 && !WriteCap(device, held, cfg.Cap)) writeErrors++;
                        }
                        else // cap mode
                        {
                            if (held.Count == 0 && (overCap || preempt))
                            {
                                if (cfg.Fan >= 0) held.Add(cfg.Fan);
                                else
                                {
                                    foreach (int f in spinning) held.Add(f);
                                    if (held.Count == 0) held.Add(0);
                                }
                                string why = overCap
                                    ? ("fan " + maxSpeed + "% > cap " + cfg.Cap + "%")
                                    : ("temp " + temp + "C >= preempt " + cfg.Preempt + "C");
                                if (Engage(device, held))
                                {
                                    WriteCap(device, held, cfg.Cap);
                                    Log("CEILING ENGAGED (" + why + ") -> holding " + cfg.Cap + "% on fan(s) " + Fmt(held));
                                }
                                else { held.Clear(); writeErrors++; }
                            }
                            else if (held.Count > 0)
                            {
                                foreach (int f in spinning) held.Add(f); // adopt any fan that starts moving
                                if (temp <= cfg.Release)
                                {
                                    Release(device, held);
                                    Log("released to auto at " + temp + "C (fan " + maxSpeed + "%)");
                                    held.Clear();
                                }
                                else if (!WriteCap(device, held, cfg.Cap)) writeErrors++;
                            }
                        }
                    }

                    if (verbose)
                        Log("tick: temp=" + temp + "C fan=" + maxSpeed + "%" +
                            (held.Count > 0 ? " [held " + cfg.Cap + "%]" : " [auto]"));

                    if (writeErrors == 1) Log("fan write failed - the daemon needs administrator rights");
                    if (writeErrors > 20) { Log("too many write errors, giving up"); break; }

                    // adaptive cadence: fast only while actually policing the fan
                    bool busy = held.Count > 0 || valveOpen || maxSpeed + 5 > cfg.Cap ||
                                (cfg.Preempt > 0 && temp + 10 >= cfg.Preempt);
                    Thread.Sleep(busy ? Math.Max(100, cfg.Interval) : 1000);
                }

                if (held.Count > 0)
                {
                    if (noRestore) Log("exit: leaving manual control in place (--no-restore), fan held at " + cfg.Cap + "%");
                    else
                    {
                        Release(device, held);
                        Log("restored automatic fan control on exit");
                    }
                }
                return 0;
            }
            finally
            {
                Nvml.Shutdown();
                if (logFile != null) logFile.Dispose();
                single.ReleaseMutex();
                single.Dispose();
            }
        }

        private static Settings Merge(Settings fileSettings, Dictionary<string, int> cli,
            string cliMode, bool? cliValveEnabled)
        {
            Settings s = fileSettings;
            foreach (KeyValuePair<string, int> kv in cli) s.Set(kv.Key, kv.Value.ToString());
            if (cliMode != null) s.Mode = cliMode;
            if (cliValveEnabled.HasValue) s.ValveEnabled = cliValveEnabled.Value;
            return s;
        }

        private static string Fmt(SortedSet<int> fans)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int f in fans) { if (sb.Length > 0) sb.Append(','); sb.Append(f); }
            return sb.ToString();
        }

        private static bool Engage(IntPtr device, SortedSet<int> fans)
        {
            foreach (int f in fans)
            {
                int rc = Nvml.SetFanControlPolicy(device, (uint)f, Nvml.POLICY_MANUAL);
                if (rc != Nvml.SUCCESS) { Log("SetFanControlPolicy(manual) fan" + f + ": " + Nvml.Describe(rc)); return false; }
            }
            return true;
        }

        private static bool WriteCap(IntPtr device, SortedSet<int> fans, int cap)
        {
            bool ok = true;
            foreach (int f in fans)
            {
                int rc = Nvml.SetFanSpeed(device, (uint)f, (uint)cap);
                if (rc != Nvml.SUCCESS) { ok = false; Log("SetFanSpeed fan" + f + ": " + Nvml.Describe(rc)); }
            }
            return ok;
        }

        private static void Release(IntPtr device, SortedSet<int> fans)
        {
            foreach (int f in fans)
            {
                Nvml.SetDefaultFanSpeed(device, (uint)f);
                Nvml.SetFanControlPolicy(device, (uint)f, Nvml.POLICY_AUTO);
            }
        }

        private static string ArgValue(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
