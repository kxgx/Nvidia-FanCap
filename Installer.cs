using System;
using System.Diagnostics;
using System.IO;
using System.Text;

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

            KillOtherInstances();
            Run("schtasks", "/end /tn \"" + AppInfo.TaskName + "\"");

            // register from a generated task definition: no command-line quoting
            // pitfalls, and the settings that matter are explicit
            // (battery limits off, no execution time limit)
            string xmlPath = Path.Combine(Path.GetTempPath(), AppInfo.TaskName + "-task.xml");
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);   // task XML must be UTF-16
            try
            {
                int rc = Run("schtasks", "/create /tn \"" + AppInfo.TaskName + "\" /xml \"" + xmlPath + "\" /f");
                if (rc != 0) { Console.WriteLine("schtasks /create failed (" + rc + ") - run as administrator"); return rc; }
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }

            Run("schtasks", "/run /tn \"" + AppInfo.TaskName + "\"");
            Console.WriteLine("installed: task \"" + AppInfo.TaskName + "\" registered and started.");
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
        /// The task definition as XML. Quoting lives in the XML instead of a
        /// command line, and the two defaults that silently break a background
        /// daemon are fixed: "stop on battery" and the 72h execution time limit.
        /// </summary>
        private static string BuildTaskXml(string exe)
        {
            return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
"  <RegistrationInfo><Description>Nvidia-FanCap - hidden GPU fan ceiling daemon</Description></RegistrationInfo>\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>\n" +
"  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
"  <Settings>\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
"    <Enabled>true</Enabled>\n" +
"    <Hidden>true</Hidden>\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
"    <Priority>7</Priority>\n" +
"  </Settings>\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--daemon --hidden</Arguments></Exec></Actions>\n" +
"</Task>\n";
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
