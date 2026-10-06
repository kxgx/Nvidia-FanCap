using System;
using System.Diagnostics;
using System.IO;

namespace NvidiaFanCap
{
    /// <summary>
    /// Installs/removes the hidden logon task by asking the Task Scheduler.
    /// Paths are derived from the running executable, so this works wherever the
    /// program is installed (Program Files, portable folder, ...).
    /// </summary>
    internal static class Installer
    {
        internal static int Install()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, AppInfo.ExeName);
            if (!File.Exists(exe)) { Console.WriteLine("missing: " + exe); return 1; }

            string task = AppInfo.TaskName;
            string action = "\\\"" + exe + "\\\" --daemon --hidden";

            KillOtherInstances();
            Run("schtasks", "/end /tn \"" + task + "\"");
            int rc = Run("schtasks", "/create /tn \"" + task + "\" /tr \"" + action + "\" /sc onlogon /rl highest /f");
            if (rc != 0) { Console.WriteLine("schtasks /create failed (" + rc + ") - run as administrator"); return rc; }
            Run("schtasks", "/run /tn \"" + task + "\"");
            Console.WriteLine("installed: task \"" + task + "\" registered and started.");
            Console.WriteLine("  exe: " + exe);
            return 0;
        }

        internal static int Uninstall()
        {
            string task = AppInfo.TaskName;
            Run("schtasks", "/end /tn \"" + task + "\"");
            Run("schtasks", "/delete /tn \"" + task + "\" /f");
            KillOtherInstances();
            Console.WriteLine("removed: task \"" + task + "\" deleted, fan control is back under driver/vBIOS control.");
            return 0;
        }

        /// <summary>
        /// Stops daemon instances started earlier (possibly from another folder),
        /// leaving this process alone - it may be the installer custom action.
        /// </summary>
        private static void KillOtherInstances()
        {
            int self = Environment.ProcessId;
            Run("taskkill", "/f /fi \"IMAGENAME eq " + AppInfo.ExeName + "\" /fi \"PID ne " + self + "\"");
        }

        private static int Run(string fileName, string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { Console.WriteLine(fileName + ": " + ex.Message); return -1; }
        }
    }
}
