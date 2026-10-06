using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace NvidiaFanCap
{
    /// <summary>
    /// Native Win32 settings window - "use the system's own UI": stock common
    /// controls (v6 themed), system fonts, and the Windows light/dark preference.
    /// No UI framework, no runtime: this ships inside the daemon executable.
    /// </summary>
    internal static class Gui
    {
        // control ids
        private const int ID_TB_CAP = 101, ID_TB_PREEMPT = 102, ID_TB_VALVE = 103, ID_CHK_VALVE = 104;
        private const int ID_APPLY = 105, ID_RESET = 106, ID_CLOSE = 107;

        // win32 constants
        private const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_VISIBLE = 0x10000000,
                           WS_CHILD = 0x40000000, WS_TABSTOP = 0x00010000;
        private const uint BS_PUSHBUTTON = 0, BS_AUTOCHECKBOX = 0x00000003;
        private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_TIMER = 0x0113,
                           WM_HSCROLL = 0x0114, WM_SETFONT = 0x0030, WM_CTLCOLORSTATIC = 0x0138,
                           WM_CTLCOLORBTN = 0x0135, WM_CTLCOLORDLG = 0x0136, WM_USER = 0x0400;
        private const uint TBM_GETPOS = WM_USER, TBM_SETPOS = WM_USER + 5,
                           TBM_SETRANGEMIN = WM_USER + 7, TBM_SETRANGEMAX = WM_USER + 8;
        private const int BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1, BST_CHECKED = 1, BST_UNCHECKED = 0;
        private const int SW_SHOW = 5, MB_OK = 0x0000, MB_ICONINFORMATION = 0x0040, MB_ICONERROR = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string lpClassName, string lpWindowName,
            uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hWnd, string text);
        [DllImport("user32.dll")] private static extern uint SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hWnd, IntPtr id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic,
            uint underline, uint strike, uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hWnd, string name, string list);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSW
        {
            public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate wndProc;

        private static string iniPath;
        private static IntPtr hwnd, fontNormal, fontTitle, fontBold, fontValue, bgBrush;
        private static IntPtr hCap, hPreempt, hValve, hChk, hCapVal, hPreemptVal, hValveVal, hStatus1, hStatus2, hPath;
        private static int textMain, textSub, textAccent;
        private static bool dark, loading = true, nvmlReady;
        private static Settings settings;
        private static readonly Dictionary<IntPtr, int> controlColors = new Dictionary<IntPtr, int>();

        internal static int Run(string settingsPath)
        {
            iniPath = settingsPath;
            dark = IsSystemDark();
            try { if (dark) SetPreferredAppMode(2); } catch { }

            textMain = dark ? RGB(240, 240, 240) : RGB(26, 26, 26);
            textSub = dark ? RGB(150, 150, 150) : RGB(115, 115, 115);
            textAccent = dark ? RGB(76, 194, 255) : RGB(0, 92, 185);
            bgBrush = CreateSolidBrush(dark ? RGB(32, 32, 32) : RGB(243, 243, 243));

            double dpi = DpiScale();
            fontNormal = CreateFontW((int)(-12 * dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            fontBold = CreateFontW((int)(-12 * dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            fontTitle = CreateFontW((int)(-21 * dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            fontValue = CreateFontW((int)(-17 * dpi), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

            INITCOMMONCONTROLSEX icc = new INITCOMMONCONTROLSEX();
            icc.dwSize = (uint)Marshal.SizeOf(typeof(INITCOMMONCONTROLSEX));
            icc.dwICC = 0x4; // trackbars
            InitCommonControlsEx(ref icc);

            wndProc = WndProc;
            WNDCLASSW wc = new WNDCLASSW();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(wndProc);
            wc.hInstance = GetModuleHandleW(null);
            wc.hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512);
            wc.hbrBackground = bgBrush;
            wc.lpszClassName = "NvidiaFanCapSettings";
            RegisterClassW(ref wc);

            int w = S(600), h = S(520);
            hwnd = CreateWindowExW(0, "NvidiaFanCapSettings", "Nvidia-FanCap - 显卡风扇上限设置",
                WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
                (GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2, w, h,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return 1;

            if (dark)
            {
                int on = 1;
                try { DwmSetWindowAttribute(hwnd, 20, ref on, 4); } catch { }
            }

            BuildControls(wc.hInstance);
            LoadFromSettings();
            loading = false;

            nvmlReady = Nvml.Init() == Nvml.SUCCESS;
            SetTimer(hwnd, (IntPtr)1, 1000, IntPtr.Zero);
            ShowWindow(hwnd, SW_SHOW);
            UpdateWindow(hwnd);

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            KillTimer(hwnd, (IntPtr)1);
            if (nvmlReady) Nvml.Shutdown();
            DeleteObject(bgBrush);
            return 0;
        }

        private static void BuildControls(IntPtr instance)
        {
            IntPtr p = hwnd;
            int x = S(24), w = S(552);

            Label(p, "Nvidia-FanCap", x, S(18), S(300), S(30), fontTitle, textMain);
            Label(p, "显卡风扇上限设置  ·  64-bit  ·  设置文件：" + ShortPath(iniPath), x, S(50), w, S(18), fontNormal, textSub);

            Label(p, "风扇上限", x, S(88), S(120), S(20), fontBold, textMain);
            hCapVal = Label(p, "40 %", S(156), S(82), S(160), S(28), fontValue, textAccent);
            hCap = Trackbar(p, ID_TB_CAP, x, S(112), w, 20, 100);
            Label(p, "转速超过上限会强制拉回；调低 = 更安静", x, S(146), w, S(18), fontNormal, textSub);

            Label(p, "预接管温度", x, S(180), S(120), S(20), fontBold, textMain);
            hPreemptVal = Label(p, "75 °C", S(156), S(174), S(160), S(28), fontValue, textAccent);
            hPreempt = Trackbar(p, ID_TB_PREEMPT, x, S(204), w, 50, 95);
            Label(p, "到达此温度提前接管风扇，避免 vBIOS 在 80 °C 突然满转", x, S(238), w, S(18), fontNormal, textSub);

            Label(p, "保险阀温度", x, S(272), S(120), S(20), fontBold, textMain);
            hValveVal = Label(p, "90 °C", S(156), S(266), S(160), S(28), fontValue, textAccent);
            hValve = Trackbar(p, ID_TB_VALVE, x, S(296), w, 80, 97);
            hChk = CreateWindowExW(0, "BUTTON", "启用保险阀（超过该温度放开上限让显卡自保，强烈建议保留）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX, x, S(322), w, S(24), p, (IntPtr)ID_CHK_VALVE, instance, IntPtr.Zero);

            hStatus1 = Label(p, "GPU —", x, S(366), w, S(18), fontNormal, textMain);
            hStatus2 = Label(p, "守护进程：检测中…", x, S(386), w, S(18), fontNormal, textMain);
            hPath = Label(p, "", x, S(406), w, S(16), fontNormal, textSub);

            Button(p, "应用", ID_APPLY, x, S(436), S(100));
            Button(p, "恢复默认", ID_RESET, S(136), S(436), S(100));
            Button(p, "关闭", ID_CLOSE, S(248), S(436), S(100));
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_CTLCOLORSTATIC:
                case WM_CTLCOLORBTN:
                {
                    IntPtr dc = wParam;
                    SetBkMode(dc, 1); // TRANSPARENT
                    int color;
                    if (!controlColors.TryGetValue(lParam, out color)) color = textMain;
                    SetTextColor(dc, color);
                    return bgBrush;
                }
                case WM_CTLCOLORDLG:
                    return bgBrush;

                case WM_HSCROLL:
                    UpdateValueLabels();
                    return IntPtr.Zero;

                case WM_COMMAND:
                {
                    int id = (int)(wParam.ToInt64() & 0xFFFF);
                    try
                    {
                        if (id == ID_APPLY) Apply();
                        else if (id == ID_RESET) Reset();
                        else if (id == ID_CLOSE) DestroyWindow(hWnd);
                    }
                    catch (Exception ex)
                    {
                        MessageBoxW(hWnd, "错误:\n" + ex.Message, "Nvidia-FanCap", MB_OK | MB_ICONERROR);
                    }
                    return IntPtr.Zero;
                }

                case WM_TIMER:
                    UpdateStatus();
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static void LoadFromSettings()
        {
            loading = true;
            settings = Settings.Load(iniPath);
            SendMessageW(hCap, TBM_SETPOS, (IntPtr)1, (IntPtr)settings.Cap);
            SendMessageW(hPreempt, TBM_SETPOS, (IntPtr)1, (IntPtr)settings.Preempt);
            SendMessageW(hValve, TBM_SETPOS, (IntPtr)1, (IntPtr)settings.Valve);
            SendMessageW(hChk, BM_SETCHECK, (IntPtr)(settings.ValveEnabled ? BST_CHECKED : BST_UNCHECKED), IntPtr.Zero);
            UpdateValueLabels();
        }

        private static void UpdateValueLabels()
        {
            if (hCapVal == IntPtr.Zero) return;
            SetWindowTextW(hCapVal, Pos(hCap) + " %");
            SetWindowTextW(hPreemptVal, Pos(hPreempt) + " °C");
            SetWindowTextW(hValveVal, Pos(hValve) + " °C");
        }

        private static void Apply()
        {
            try
            {
                Settings s = Settings.Load(iniPath);
                s.Cap = Pos(hCap);
                s.Preempt = Pos(hPreempt);
                s.Valve = Pos(hValve);
                s.ValveEnabled = SendMessageW(hChk, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32() == BST_CHECKED;
                string used = Settings.SaveSmart(s, iniPath);
                iniPath = used;
                settings = s;
                SetWindowTextW(hPath, "设置文件：" + used);
                MessageBoxW(hwnd, "已保存到:\n" + used + "\n\n守护进程约 5 秒内生效，无需重启。", "Nvidia-FanCap", MB_OK | MB_ICONINFORMATION);
            }
            catch (Exception ex)
            {
                MessageBoxW(hwnd, "保存失败:\n" + ex.Message, "Nvidia-FanCap", MB_OK | MB_ICONERROR);
            }
        }

        private static void Reset()
        {
            try
            {
                loading = true;
                SendMessageW(hCap, TBM_SETPOS, (IntPtr)1, (IntPtr)40);
                SendMessageW(hPreempt, TBM_SETPOS, (IntPtr)1, (IntPtr)75);
                SendMessageW(hValve, TBM_SETPOS, (IntPtr)1, (IntPtr)90);
                SendMessageW(hChk, BM_SETCHECK, (IntPtr)BST_CHECKED, IntPtr.Zero);
                UpdateValueLabels();
                loading = false;
            }
            catch { }
        }

        private static void UpdateStatus()
        {
            try
            {
                if (nvmlReady)
                {
                    IntPtr device;
                    if (Nvml.GetHandleByIndex(0, out device) == Nvml.SUCCESS)
                    {
                        uint numFans;
                        Nvml.GetNumFans(device, out numFans);
                        StringBuilder sb = new StringBuilder("GPU " + Nvml.ReadTemperature(device) + " °C     风扇 ");
                        for (int f = 0; f < (int)numFans; f++) sb.Append(Nvml.ReadFan(device, (uint)f)).Append("%  ");
                        SetWindowTextW(hStatus1, sb.ToString());
                    }
                }

                bool running;
                Mutex probe = null;
                try { running = Mutex.TryOpenExisting(AppInfo.MutexName, out probe); } catch { running = false; }
                if (probe != null) probe.Dispose();

                SetWindowTextW(hStatus2, running
                    ? "守护进程：运行中（上限 " + (settings != null ? settings.Cap : 40) + "%，改动约 5 秒生效）"
                    : "守护进程：未运行 - 运行 install-task.bat 或安装 MSI 后才会锁死上限");
            }
            catch { }
        }

        // helpers -----------------------------------------------------------

        private static bool IsSystemDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
                }
            }
            catch { return false; }
        }

        private static double DpiScale()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int dpi = GetDeviceCaps(dc, 88);
            ReleaseDC(IntPtr.Zero, dc);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        private static int S(int v) { return (int)(v * DpiScale() + 0.5); }
        private static int RGB(byte r, byte g, byte b) { return r | (g << 8) | (b << 16); }
        private static int Pos(IntPtr trackbar) { return (int)SendMessageW(trackbar, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero).ToInt64(); }
        private static string ShortPath(string path)
        {
            try { return path.Length > 60 ? "..." + path.Substring(path.Length - 57) : path; } catch { return path; }
        }

        private static IntPtr Label(IntPtr parent, string text, int x, int y, int w, int h, IntPtr font, int color)
        {
            IntPtr handle = CreateWindowExW(0, "STATIC", text, WS_CHILD | WS_VISIBLE, x, y, w, h,
                parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            SendMessageW(handle, WM_SETFONT, font, (IntPtr)1);
            controlColors[handle] = color;
            return handle;
        }

        private static IntPtr Trackbar(IntPtr parent, int id, int x, int y, int w, int min, int max)
        {
            IntPtr handle = CreateWindowExW(0, "msctls_trackbar32", "", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                x, y, w, S(32), parent, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
            SendMessageW(handle, TBM_SETRANGEMIN, IntPtr.Zero, (IntPtr)min);
            SendMessageW(handle, TBM_SETRANGEMAX, IntPtr.Zero, (IntPtr)max);
            if (dark) { try { SetWindowTheme(handle, "DarkMode_Explorer", null); } catch { } }
            return handle;
        }

        private static void Button(IntPtr parent, string text, int id, int x, int y, int w)
        {
            IntPtr handle = CreateWindowExW(0, "BUTTON", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                x, y, w, S(30), parent, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
            SendMessageW(handle, WM_SETFONT, fontNormal, (IntPtr)1);
            if (dark) { try { SetWindowTheme(handle, "DarkMode_Explorer", null); } catch { } }
        }
    }
}
