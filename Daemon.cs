using System;
using System.Collections.Generic;
using System.Globalization;
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
    ///
    /// Mode "curve" replaces the fixed ceiling with a shape-preserving fan curve
    /// plus a power-limit curve: fan speed follows the (smoothed) temperature with
    /// hysteresis and per-tick rate limiting, the power limit follows its own curve
    /// (down immediately, up +10 W per tick) and both are released by the same
    /// safety valve as the other modes.
    /// </summary>
    internal static class Daemon
    {
        // curve mode tuning --------------------------------------------------
        private const double FAN_HYSTERESIS = 0.75;   // ignore target changes smaller than this (percent)
        private const double FAN_RELEASE_AT = 5.0;    // target at/below this: back to auto, 0-RPM idle
        private const double FAN_REENGAGE_AT = 8.0;   // target above this: take manual control again
        private const int POWER_STEP_UP = 6;          // watts per tick when raising the power limit
        private const int POWER_DEADBAND = 2;         // watts: ignore rounding jitter in the curve target
        private const int POWER_MIN = 50;             // never command an absurdly low limit
        private const int POWER_MAX_FALLBACK = 330;   // only used when the card reports no default limit

        // auto mode tuning - both sensitivities were measured on the blower
        // 4070 Ti SUPER (fan 4.5 %/C, board power 0.035 C/W)
        private const double FAN_PER_DEGREE = 4.5;
        private const double DEG_PER_WATT = 0.035;
        private const double ANCHOR_DAMPING = 0.30;

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

            // single instance: wait a little for a dying instance to release the
            // mutex (upgrade installs stop the old daemon and start the new one
            // within the same second)
            Mutex single = null;
            bool isFirst = false;
            for (int i = 0; i < 20 && single == null; i++)
            {
                single = new Mutex(true, Program.MutexName, out isFirst);
                if (!isFirst) { single.Dispose(); single = null; Thread.Sleep(500); }
            }
            if (single == null) { Log("another Nvidia-FanCap daemon is already running"); return 3; }

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
                cfg.NormalizeCurves();
                int appliedPowerLimit = 0;
                int defaultPowerW = ReadDefaultPowerLimit(device);

                bool autoMode = string.Equals(cfg.Mode, "auto", StringComparison.OrdinalIgnoreCase);
                bool curveMode = IsCurve(cfg) || autoMode;
                ThermalModel autoModel = autoMode ? (ThermalModel.TryLoad(ModelStatePath()) ?? new ThermalModel()) : null;
                Curve fanCurve = null;
                Curve powerCurve = null;
                Curve defaultFanCurve = Curve.Build(Settings.DefaultCurveTemp, Settings.DefaultCurveFan);
                // an anchor learned in a previous run wins; otherwise seed from the
                // configured power limit so a high limit starts loud enough to stay
                // out of the driver override zone (the learner will bring it down)
                double autoAnchor = autoModel != null && autoModel.Anchor > 0 ? autoModel.Anchor : SeedAnchor(cfg, defaultPowerW);
                if (autoMode) fanCurve = BuildAnchorCurve(cfg.TargetTemp, autoAnchor, FAN_PER_DEGREE);
                else if (curveMode) { fanCurve = BuildFanCurve(cfg); powerCurve = BuildPowerCurve(cfg); }
                if (curveMode && fanCurve == null)
                {
                    Log("curve: invalid curve settings, falling back to cap mode");
                    curveMode = false;
                }
                if (curveMode && !autoMode && powerCurve == null)
                {
                    Log("curve: invalid curve settings, falling back to cap mode");
                    curveMode = false;
                }

                // curve mode state (persists across ticks)
                double emaTemp = -1;            // smoothed temperature, <0 = not initialised
                double curveFanOut = -1;        // fan percent currently commanded by us
                double curveTargetFan = -1;     // last curve target (verbose logging only)
                int curvePower = -1;            // power limit (W) we last applied, <0 = not owned yet
                bool curvePowerDisabled = false;// card refused SetPowerLimit: keep the fan curve running
                int lastCurveFan = -1;          // -1 = automatic
                int lastCurvePower = -1;
                DateTime nextCurveLog = DateTime.MinValue;
                DateTime nextCurveDrift = DateTime.UtcNow.AddSeconds(5);

                // auto mode state: steady-state window + learned thermal model
                var steadyWin = new Queue<double[]>();
                DateTime nextAutoTick = DateTime.UtcNow;
                DateTime lastModelSample = DateTime.MinValue;
                DateTime nextModelUpdate = DateTime.UtcNow.AddSeconds(5);
                DateTime nextModelSave = DateTime.UtcNow.AddSeconds(300);

                if ((!curveMode || autoMode) && cfg.PowerLimitW > 0) ApplyPowerLimit(device, cfg.PowerLimitW, ref appliedPowerLimit);

                Log(string.Format("mode={0} cap={1}% preempt>={2}C release<={3}C valve>={4}C(arm<={5}C){6}",
                    cfg.Mode, cfg.Cap, cfg.Preempt, cfg.Release, cfg.Valve, cfg.ValveReenable,
                    cfg.ValveEnabled ? "" : " VALVE=OFF"));
                if (curveMode) LogCurveSetup(cfg, defaultPowerW);
                if (autoMode) Log("auto: fitting the fan curve from the power limit (" +
                    (cfg.PowerLimitW > 0 ? cfg.PowerLimitW + "W" : "card default") + ") toward " +
                    cfg.TargetTemp + "C  " + autoModel.Format());

                Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) { e.Cancel = true; running = false; };

                var held = new SortedSet<int>();
                bool valveOpen = false;
                int writeErrors = 0;
                DateTime nextBurstLog = DateTime.MinValue;
                DateTime nextDriftCheck = DateTime.UtcNow.AddSeconds(5);
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
                            bool wasCurve = curveMode;
                            cfg = Merge(Settings.Load(current), cli, cliMode, cliValveEnabled);
                            cfg.NormalizeCurves();
                            bool newAuto = string.Equals(cfg.Mode, "auto", StringComparison.OrdinalIgnoreCase);
                            if (newAuto && autoModel == null) autoModel = ThermalModel.TryLoad(ModelStatePath()) ?? new ThermalModel();
                            if (!newAuto) autoModel = null;
                            autoMode = newAuto;
                            curveMode = IsCurve(cfg) || autoMode;
                            if (curveMode)
                            {
                                if (autoMode) fanCurve = BuildAnchorCurve(cfg.TargetTemp, autoAnchor, FAN_PER_DEGREE);
                                else { fanCurve = BuildFanCurve(cfg); powerCurve = BuildPowerCurve(cfg); }
                                if (fanCurve == null || (!autoMode && powerCurve == null))
                                {
                                    Log("curve: invalid curve settings, falling back to cap mode");
                                    curveMode = false;
                                }
                            }
                            Log("settings reloaded: mode=" + cfg.Mode + " cap=" + cfg.Cap + "% preempt=" + cfg.Preempt +
                                "C valve=" + cfg.Valve + "C(" + (cfg.ValveEnabled ? "on" : "off") + ") power=" +
                                (cfg.PowerLimitW > 0 ? cfg.PowerLimitW + "W" : "off") + " [" + current + "]");
                            if (curveMode) LogCurveSetup(cfg, defaultPowerW);

                            if (curveMode)
                            {
                                if (!wasCurve)
                                {
                                    // curve mode now owns fan + power limit: reset the trackers and
                                    // hand any static hold back so the curve can start clean
                                    appliedPowerLimit = 0;
                                    emaTemp = -1;
                                    curveFanOut = -1;
                                    curvePower = -1;
                                    lastCurveFan = lastCurvePower = -1;
                                    if (held.Count > 0) { Release(device, held); held.Clear(); }
                                }
                                nextCurveDrift = DateTime.UtcNow.AddSeconds(5);
                                curvePowerDisabled = false;   // settings changed: retry the power curve
                                if (autoMode)
                                {
                                    // auto mode keeps the power limit as a static input
                                    if (cfg.PowerLimitW != appliedPowerLimit) ApplyPowerLimit(device, cfg.PowerLimitW, ref appliedPowerLimit);
                                    else ReassertPowerLimit(device, cfg.PowerLimitW, ref appliedPowerLimit);
                                }
                            }
                            else
                            {
                                emaTemp = -1;
                                curveFanOut = -1;
                                if (wasCurve)
                                {
                                    // leaving curve mode: give the power limit back to the card,
                                    // then let the normal cap/fixed logic re-apply a static one
                                    if (curvePower > 0) RestorePowerDefault(device);
                                    curvePower = -1;
                                    appliedPowerLimit = 0;
                                    if (held.Count > 0) { Release(device, held); held.Clear(); }
                                }
                                if (cfg.PowerLimitW != appliedPowerLimit) ApplyPowerLimit(device, cfg.PowerLimitW, ref appliedPowerLimit);
                                else ReassertPowerLimit(device, cfg.PowerLimitW, ref appliedPowerLimit);
                            }
                        }
                        else if ((!curveMode || autoMode) && DateTime.UtcNow >= nextDriftCheck)
                        {
                            // another app (or a driver reload) can silently change the
                            // power limit; keep ours in place (auto mode uses a static
                            // power limit as the input for the fitted curve)
                            nextDriftCheck = DateTime.UtcNow.AddSeconds(5);
                            ReassertPowerLimit(device, cfg.PowerLimitW, ref appliedPowerLimit);
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

                    if (curveMode)
                    {
                        // exponential smoothing: the fan curve must not chase every 1C step
                        if (emaTemp < 0) emaTemp = temp;
                        else emaTemp += cfg.TempEma * (temp - emaTemp);
                        if (emaTemp < 0) emaTemp = 0;

                        // another app can move the power limit behind our back (NVIDIA App
                        // auto-tuning, a driver reload): adopt the real value so the curve
                        // control corrects it instead of assuming its last write stuck
                        if (!curvePowerDisabled && curvePower > 0 && DateTime.UtcNow >= nextCurveDrift)
                        {
                            nextCurveDrift = DateTime.UtcNow.AddSeconds(5);
                            uint actualMw;
                            if (Nvml.GetPowerLimit(device, out actualMw) == Nvml.SUCCESS)
                            {
                                int actual = (int)(actualMw / 1000);
                                if (Math.Abs(actual - curvePower) >= 2)
                                {
                                    Log("curve: power limit drifted to " + actual + "W, retaking control");
                                    curvePower = actual;
                                }
                            }
                        }
                    }

                    // auto mode: learn the thermal response from steady states and
                    // re-derive the fan curve from the power limit the user chose.
                    // "Steady" is judged on a 24 s window (the fan is allowed to
                    // micro-adjust, which it always does) - a point is usable only
                    // when fan, temperature and power all stayed inside a narrow band.
                    if (autoMode && autoModel != null)
                    {
                        double powerW = Nvml.ReadPowerWatts(device);
                        if (DateTime.UtcNow >= nextAutoTick)
                        {
                            nextAutoTick = DateTime.UtcNow.AddSeconds(1);
                            steadyWin.Enqueue(new double[] { NowSecs(), powerW, maxSpeed, temp });
                            double cutoff = NowSecs() - 40.0;   // thermal time constant is ~30 s
                            while (steadyWin.Count > 0 && steadyWin.Peek()[0] < cutoff) steadyWin.Dequeue();

                            if (steadyWin.Count >= 20 && DateTime.UtcNow - lastModelSample >= TimeSpan.FromSeconds(25))
                            {
                                double pmin = double.MaxValue, pmax = double.MinValue;
                                double fmin = double.MaxValue, fmax = double.MinValue;
                                double tmin = double.MaxValue, tmax = double.MinValue;
                                double psum = 0, fsum = 0, tsum = 0;
                                int n = steadyWin.Count;
                                foreach (double[] s in steadyWin)
                                {
                                    if (s[1] < pmin) pmin = s[1]; if (s[1] > pmax) pmax = s[1];
                                    if (s[2] < fmin) fmin = s[2]; if (s[2] > fmax) fmax = s[2];
                                    if (s[3] < tmin) tmin = s[3]; if (s[3] > tmax) tmax = s[3];
                                    psum += s[1]; fsum += s[2]; tsum += s[3];
                                }
                                if (pmax - pmin <= 20 && fmax - fmin <= 5 && tmax - tmin <= 2.0 &&
                                    curveFanOut >= 0 && Math.Abs(maxSpeed - curveFanOut) <= 8)
                                {
                                    // the last two clauses reject windows during a driver
                                    // override: there the fan is not ours, so the point
                                    // would teach the model nonsense
                                    autoModel.Add(psum / n, fsum / n, tsum / n);
                                    lastModelSample = DateTime.UtcNow;
                                    Log("auto: learned " + autoModel.Format() + " -> anchor " +
                                        Fmt1(autoModel.AnchorFan(cfg.TargetTemp, AnchorPower(cfg, defaultPowerW, psum / n))) +
                                        "% at " + cfg.TargetTemp + "C (slope " + Fmt1(autoModel.Slope) + "%/C)");
                                }
                            }
                        }
                        if (DateTime.UtcNow >= nextModelUpdate)
                        {
                            nextModelUpdate = DateTime.UtcNow.AddSeconds(15);
                            // Damped anchor update from the last reliable steady point.
                            // Only two card constants are needed, and both were measured:
                            // 4.5 % fan per degree C, 0.035 C per watt. Damping keeps a
                            // transient observation from slamming the curve around.
                            double p0, f0, t0;
                            if (autoModel.TryGetLast(out p0, out f0, out t0) && curveFanOut >= 0 && Math.Abs(maxSpeed - curveFanOut) <= 8)
                            {
                                double anchorPower = AnchorPower(cfg, defaultPowerW, powerW);
                                double desired = f0 + (t0 - cfg.TargetTemp) * FAN_PER_DEGREE
                                                 - (anchorPower - p0) * DEG_PER_WATT * FAN_PER_DEGREE;
                                if (autoAnchor < 0) autoAnchor = desired;
                                else autoAnchor += ANCHOR_DAMPING * (desired - autoAnchor);
                                if (autoAnchor < 0) autoAnchor = 0; else if (autoAnchor > 100) autoAnchor = 100;
                                autoModel.Anchor = autoAnchor;
                                fanCurve = BuildAnchorCurve(cfg.TargetTemp, autoAnchor, FAN_PER_DEGREE);
                                Log("auto: point T=" + Fmt1(t0) + "C F=" + Fmt1(f0) + "% P=" + Fmt1(p0) + "W -> want " +
                                    Fmt1(desired) + "% at " + cfg.TargetTemp + "C, anchor " + Fmt1(autoAnchor) +
                                    "% (limit " + Fmt1(AnchorPower(cfg, defaultPowerW, powerW)) + "W)");
                            }
                            else if (autoAnchor < 0)
                            {
                                fanCurve = defaultFanCurve;   // no reliable point yet
                            }
                        }
                        if (DateTime.UtcNow >= nextModelSave)
                        {
                            nextModelSave = DateTime.UtcNow.AddSeconds(300);
                            autoModel.Save(ModelStatePath());
                        }
                    }

                    // safety valve: never stand between the card and its survival
                    if (cfg.ValveEnabled && temp >= cfg.Valve)
                    {
                        if (!valveOpen)
                        {
                            Log("VALVE OPEN at " + temp + "C - ceiling disarmed, card takes over");
                            if (held.Count > 0) Release(device, held);
                            held.Clear();
                            curveFanOut = -1;
                            if (curveMode && curvePower > 0)
                            {
                                RestorePowerDefault(device);   // valve: give the power limit back too
                                curvePower = -1;
                            }
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
                        if (curveMode)
                        {
                            // fan target is monotone: never overshoots between knots
                            double targetFan = fanCurve.Evaluate(emaTemp);
                            if (targetFan < 0) targetFan = 0; else if (targetFan > 100) targetFan = 100;
                            curveTargetFan = targetFan;

                            // 0-RPM idle: below the bottom of the curve hand the fan back
                            if (held.Count > 0 && targetFan <= FAN_RELEASE_AT)
                            {
                                Release(device, held);
                                held.Clear();
                                curveFanOut = -1;
                                Log("curve: 0-RPM idle - released fan to auto at " + temp + "C (target " +
                                    Fmt1(targetFan) + "%)");
                            }
                            else if (held.Count == 0 && numFans > 0 && targetFan > FAN_REENGAGE_AT)
                            {
                                if (cfg.Fan >= 0) held.Add(cfg.Fan);
                                else for (int f = 0; f < (int)numFans; f++) held.Add(f);
                                if (Engage(device, held))
                                {
                                    curveFanOut = maxSpeed;   // start where the driver left the fan
                                    Log("curve: took over fan(s) " + Fmt(held) + " at " + temp + "C (target " +
                                        Fmt1(targetFan) + "%)");
                                }
                                else { held.Clear(); curveFanOut = -1; writeErrors++; }
                            }

                            int outPct = -1;
                            if (held.Count > 0)
                            {
                                if (curveFanOut < 0) curveFanOut = maxSpeed;
                                // hysteresis + per-tick rate limit toward the curve target
                                if (Math.Abs(targetFan - curveFanOut) >= FAN_HYSTERESIS)
                                {
                                    if (targetFan > curveFanOut) curveFanOut = Math.Min(targetFan, curveFanOut + cfg.FanRateUp);
                                    else curveFanOut = Math.Max(targetFan, curveFanOut - cfg.FanRateDown);
                                }
                                outPct = (int)Math.Round(curveFanOut, MidpointRounding.AwayFromZero);
                                if (outPct < 0) outPct = 0; else if (outPct > 100) outPct = 100;

                                // the driver revokes manual fan policy when its thermal
                                // protection kicks in: reclaim the policy every tick
                                EnsureManual(device, held);
                                if (!WriteCap(device, held, outPct)) writeErrors++;

                                if (maxSpeed > outPct + 20 && DateTime.UtcNow >= nextBurstLog)
                                {
                                    nextBurstLog = DateTime.UtcNow.AddSeconds(20);
                                    Log("curve: driver override: fan " + maxSpeed + "% while target " + outPct +
                                        "% at " + temp + "C (thermal protection is fighting the curve)");
                                }
                            }

                            // power limit from the power curve: down immediately,
                            // up at most POWER_STEP_UP watts per tick. A card that
                            // rejects power-limit writes keeps the fan curve only.
                            if (curvePower < 0 && !curvePowerDisabled && !autoMode)
                            {
                                uint currentMw;
                                if (Nvml.GetPowerLimit(device, out currentMw) != Nvml.SUCCESS)
                                {
                                    Log("curve: cannot read the power limit - continuing with the fan curve only");
                                    curvePowerDisabled = true;
                                }
                                else curvePower = (int)(currentMw / 1000);
                            }
                            if (!autoMode && !curvePowerDisabled)
                            {
                                double targetW = powerCurve.Evaluate(emaTemp);
                                double maxW = defaultPowerW > 0 ? defaultPowerW : POWER_MAX_FALLBACK;
                                if (cfg.PowerLimitW > 0 && cfg.PowerLimitW < maxW) maxW = cfg.PowerLimitW;  // hard bound
                                if (targetW > maxW) targetW = maxW;
                                if (targetW < POWER_MIN) targetW = POWER_MIN;
                                int wantW = (int)Math.Round(targetW, MidpointRounding.AwayFromZero);

                                if (curvePower < 0) curvePower = wantW;
                                else if (wantW < curvePower - POWER_DEADBAND)
                                {
                                    if (!SetPowerLimitWatts(device, wantW, ref curvePower))
                                    {
                                        Log("curve: power limit control unavailable - continuing with the fan curve only");
                                        RestorePowerDefault(device);   // try to undo whatever we did apply
                                        curvePower = -1;
                                        curvePowerDisabled = true;
                                    }
                                }
                                else if (wantW > curvePower + POWER_DEADBAND)
                                {
                                    int nextW = Math.Min(wantW, curvePower + POWER_STEP_UP);
                                    if (!SetPowerLimitWatts(device, nextW, ref curvePower))
                                    {
                                        Log("curve: power limit control unavailable - continuing with the fan curve only");
                                        RestorePowerDefault(device);   // try to undo whatever we did apply
                                        curvePower = -1;
                                        curvePowerDisabled = true;
                                    }
                                }
                            }

                            // one compact state line, at most every few seconds
                            int shownFan = held.Count > 0 ? outPct : -1;
                            if (shownFan != lastCurveFan || curvePower != lastCurvePower)
                            {
                                if (DateTime.UtcNow >= nextCurveLog)
                                {
                                    nextCurveLog = DateTime.UtcNow.AddSeconds(3);
                                    string fanText = shownFan < 0
                                        ? "fan auto"
                                        : "fan " + (lastCurveFan >= 0 ? lastCurveFan.ToString() : "auto") + "->" + shownFan + "%";
                                    string powerText = "power " + (lastCurvePower >= 0 ? lastCurvePower.ToString() : "?") +
                                        "->" + (curvePower >= 0 ? curvePower.ToString() : "?") + "W";
                                    Log("curve: temp=" + temp + "C(ema " + Fmt1(emaTemp) + ") " + fanText + " " + powerText);
                                }
                                lastCurveFan = shownFan;
                                lastCurvePower = curvePower;
                            }
                        }
                        else if (cfg.Mode == "fixed")
                        {
                            if (held.Count == 0)
                            {
                                if (cfg.Fan >= 0) held.Add(cfg.Fan);
                                else for (int f = 0; f < (int)numFans; f++) held.Add(f);
                                if (!Engage(device, held)) { held.Clear(); writeErrors++; }
                            }
                            if (held.Count > 0) EnsureManual(device, held);
                            if (held.Count > 0 && !WriteCap(device, held, cfg.Cap)) writeErrors++;
                        }
                        else // cap mode
                        {
                            if (held.Count == 0 && (maxSpeed > cfg.Cap || (cfg.Preempt > 0 && temp >= cfg.Preempt)))
                            {
                                if (cfg.Fan >= 0) held.Add(cfg.Fan);
                                else
                                {
                                    foreach (int f in spinning) held.Add(f);
                                    if (held.Count == 0) held.Add(0);
                                }
                                bool overCap = maxSpeed > cfg.Cap;
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
                                else
                                {
                                    // The driver revokes manual fan policy when its thermal
                                    // protection kicks in (seen above ~84C on a 4070 Ti
                                    // Super) and forces the fan up. Reclaim the policy on
                                    // every tick instead of only writing the speed.
                                    EnsureManual(device, held);
                                    if (!WriteCap(device, held, cfg.Cap)) writeErrors++;
                                    if (maxSpeed > cfg.Cap + 20 && DateTime.UtcNow >= nextBurstLog)
                                    {
                                        nextBurstLog = DateTime.UtcNow.AddSeconds(20);
                                        Log("driver override: fan " + maxSpeed + "% while holding " + cfg.Cap +
                                            "% at " + temp + "C (thermal protection is fighting the ceiling)");
                                    }
                                }
                            }
                        }
                    }

                    if (verbose)
                    {
                        if (curveMode)
                            Log("tick: temp=" + temp + "C ema=" + Fmt1(emaTemp) + "C fan=" + maxSpeed + "% target=" +
                                Fmt1(curveTargetFan) + "% power=" + (curvePower > 0 ? curvePower + "W" : "auto") +
                                (valveOpen ? " [valve open]" : (held.Count > 0 ? " [holding]" : " [auto]")));
                        else
                            Log("tick: temp=" + temp + "C fan=" + maxSpeed + "%" +
                                (held.Count > 0 ? " [held " + cfg.Cap + "%]" : " [auto]"));
                    }

                    if (writeErrors == 1) Log("fan write failed - the daemon needs administrator rights");
                    if (writeErrors > 20) { Log("too many write errors, giving up"); break; }

                    // adaptive cadence: fast only while actually policing the fan
                    bool busy;
                    if (curveMode)
                        busy = held.Count > 0 || valveOpen || (cfg.ValveEnabled && temp + 10 >= cfg.Valve) ||
                               (emaTemp >= 0 && Math.Abs(emaTemp - temp) > 2.0);
                    else
                        busy = held.Count > 0 || valveOpen || maxSpeed + 5 > cfg.Cap ||
                               (cfg.Preempt > 0 && temp + 10 >= cfg.Preempt);
                    Thread.Sleep(busy ? Math.Max(100, cfg.Interval) : 1000);
                }

                if (held.Count > 0)
                {
                    if (noRestore) Log("exit: leaving manual control in place (--no-restore)" +
                                       (curveMode ? "" : ", fan held at " + cfg.Cap + "%"));
                    else
                    {
                        Release(device, held);
                        Log("restored automatic fan control on exit");
                    }
                }
                if (curveMode && !noRestore && curvePower > 0 && curvePower != defaultPowerW)
                {
                    RestorePowerDefault(device);
                    Log("restored the default power limit on exit");
                }
                if (autoMode && autoModel != null) autoModel.Save(ModelStatePath());
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

        private static bool IsCurve(Settings s)
        {
            return string.Equals(s.Mode, "curve", StringComparison.OrdinalIgnoreCase);
        }

        private static Curve BuildFanCurve(Settings s)
        {
            return Curve.BuildOrDefault(s.CurveTemp, s.CurveFan,
                Settings.DefaultCurveTemp, Settings.DefaultCurveFan);
        }

        private static Curve BuildPowerCurve(Settings s)
        {
            return Curve.BuildOrDefault(s.PowerCurveTemp, s.PowerCurveWatt,
                Settings.DefaultPowerCurveTemp, Settings.DefaultPowerCurveWatt);
        }

        private static void LogCurveSetup(Settings cfg, int defaultPowerW)
        {
            Log("curve: fan  " + Curve.Format(cfg.CurveTemp) + " C -> " + Curve.Format(cfg.CurveFan) + "%");
            Log("curve: power " + Curve.Format(cfg.PowerCurveTemp) + " C -> " + Curve.Format(cfg.PowerCurveWatt) +
                "W (card default " + (defaultPowerW > 0 ? defaultPowerW + "W" : "unknown") + ")");
            Log("curve: temp_ema=" + Fmt2(cfg.TempEma) + " fan_rate_up=" + Fmt2(cfg.FanRateUp) +
                "%/tick fan_rate_down=" + Fmt2(cfg.FanRateDown) + "%/tick release<=" + Fmt1(FAN_RELEASE_AT) +
                "% reengage>" + Fmt1(FAN_REENGAGE_AT) + "%");
        }

        private static string Fmt(SortedSet<int> fans)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int f in fans) { if (sb.Length > 0) sb.Append(','); sb.Append(f); }
            return sb.ToString();
        }

        private static string Fmt1(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Fmt2(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
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

        private static void EnsureManual(IntPtr device, SortedSet<int> fans)
        {
            foreach (int f in fans) Nvml.SetFanControlPolicy(device, (uint)f, Nvml.POLICY_MANUAL);
        }

        /// <summary>
        /// Power ceiling is the lever that decides whether a low fan cap can be
        /// sustained: on a blower 4070 Ti Super the driver forces the fan to 100%
        /// way above ~84C, and only a lower power limit keeps the card below that.
        /// </summary>
        private static void ApplyPowerLimit(IntPtr device, int watts, ref int applied)
        {
            if (watts <= 0) { applied = 0; return; }
            uint now, defaultValue;
            Nvml.GetPowerLimit(device, out now);
            Nvml.GetPowerDefaultLimit(device, out defaultValue);
            int rc = Nvml.SetPowerLimit(device, (uint)(watts * 1000));
            Log("power limit: " + (now / 1000) + "W (default " + (defaultValue / 1000) + "W) -> " +
                watts + "W  " + Nvml.Describe(rc));
            applied = watts;
        }

        /// <summary>
        /// Put the configured power limit back when something else changed it
        /// (NVIDIA App's auto-tuning raises it to 110 %, a driver reload resets it).
        /// </summary>
        private static void ReassertPowerLimit(IntPtr device, int watts, ref int applied)
        {
            if (watts <= 0) return;
            uint current;
            if (Nvml.GetPowerLimit(device, out current) != Nvml.SUCCESS) return;
            if ((int)(current / 1000) == watts) return;
            Log("power limit drifted to " + (current / 1000) + "W, restoring " + watts + "W");
            ApplyPowerLimit(device, watts, ref applied);
        }

        private static int ReadDefaultPowerLimit(IntPtr device)
        {
            uint defaultValue;
            return Nvml.GetPowerDefaultLimit(device, out defaultValue) == Nvml.SUCCESS
                ? (int)(defaultValue / 1000) : 0;
        }

        /// <summary>Quiet version of ApplyPowerLimit for the per-tick curve ramp.</summary>
        private static bool SetPowerLimitWatts(IntPtr device, int watts, ref int applied)
        {
            if (watts <= 0) { applied = 0; return true; }
            int rc = Nvml.SetPowerLimit(device, (uint)(watts * 1000));
            if (rc != Nvml.SUCCESS)
            {
                Log("SetPowerLimit " + watts + "W: " + Nvml.Describe(rc));
                applied = 0;
                return false;
            }
            applied = watts;
            return true;
        }

        /// <summary>
        /// Hand the power limit back to the card's default (safety valve / exit).
        /// </summary>
        private static void RestorePowerDefault(IntPtr device)
        {
            uint defaultValue;
            if (Nvml.GetPowerDefaultLimit(device, out defaultValue) != Nvml.SUCCESS)
            {
                Log("GetPowerDefaultLimit failed - leaving the power limit alone");
                return;
            }
            uint current;
            Nvml.GetPowerLimit(device, out current);
            int rc = Nvml.SetPowerLimit(device, defaultValue);
            Log("power limit: " + (current / 1000) + "W -> default " + (defaultValue / 1000) + "W  " + Nvml.Describe(rc));
        }

        /// <summary>
        /// Power the fitted fan curve is anchored at: the configured limit when the
        /// user set one, otherwise the measured steady draw (so a card that never
        /// reaches its limit still gets a correct anchor).
        /// </summary>
        private static double AnchorPower(Settings cfg, int defaultPowerW, double measured)
        {
            double fallback = defaultPowerW > 0 ? defaultPowerW : 330;
            if (measured > 50) return Math.Min(measured, cfg.PowerLimitW > 0 ? cfg.PowerLimitW : fallback);
            if (cfg.PowerLimitW > 0) return cfg.PowerLimitW;
            return fallback;
        }

        /// <summary>
        /// Starting anchor for auto mode. Derived from the configured power limit
        /// with the measured sensitivities so a high limit starts loud enough to
        /// stay below the driver override zone; the learner then brings it down.
        /// </summary>
        private static double SeedAnchor(Settings cfg, int defaultPowerW)
        {
            double limit = cfg.PowerLimitW > 0 ? cfg.PowerLimitW : (defaultPowerW > 0 ? defaultPowerW : 310);
            double anchor = 46 + (limit - 150) * DEG_PER_WATT * FAN_PER_DEGREE;
            if (anchor < 30) anchor = 30; else if (anchor > 85) anchor = 85;
            return anchor;
        }

        /// <summary>
        /// Three-knot fan curve through (targetTemp, anchorFan) with the measured
        /// slope: flat zero far below the knee (0-RPM idle), the anchor at the
        /// target temperature, saturated above the driver override zone.
        /// </summary>
        private static Curve BuildAnchorCurve(double targetTemp, double anchorFan, double fanPerDegree)
        {
            double[] xs = new double[] { targetTemp - 40, targetTemp, targetTemp + 20 };
            double[] ys = new double[] { anchorFan - 40 * fanPerDegree, anchorFan, anchorFan + 20 * fanPerDegree };
            for (int i = 0; i < ys.Length; i++)
            {
                if (ys[i] < 0) ys[i] = 0; else if (ys[i] > 100) ys[i] = 100;
            }
            if (ys[0] > ys[1]) ys[0] = ys[1];
            if (ys[2] < ys[1]) ys[2] = ys[1];
            return Curve.Build(xs, ys);
        }

        private static double NowSecs()
        {
            return DateTime.UtcNow.Ticks / 1e7;   // monotonic enough for a 24 s window
        }

        /// <summary>Learned thermal model lives next to the per-user settings.</summary>
        private static string ModelStatePath()
        {
            string dir = Path.GetDirectoryName(Settings.OverridePath());
            return Path.Combine(dir ?? Path.GetTempPath(), "model.txt");
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
