using System;
using System.Runtime.InteropServices;

namespace NvidiaFanCap
{
    /// <summary>
    /// Minimal NVML (NVIDIA Management Library) bindings - only what FanCap needs.
    /// nvml.dll ships with every NVIDIA driver; no SDK required.
    /// </summary>
    internal static class Nvml
    {
        private const string Dll = "nvml.dll";

        internal const int SUCCESS = 0;
        internal const int NOT_SUPPORTED = 3;
        internal const int INSUFFICIENT_PERMISSIONS = 4;

        internal const uint POLICY_AUTO = 0;    // NVML_FAN_POLICY_TEMPERATURE_CONTINOUS_SW
        internal const uint POLICY_MANUAL = 1;  // NVML_FAN_POLICY_MANUAL

        [DllImport(Dll, EntryPoint = "nvmlInit_v2")]
        internal static extern int Init();
        [DllImport(Dll, EntryPoint = "nvmlShutdown")]
        internal static extern int Shutdown();
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        internal static extern int GetHandleByIndex(uint index, out IntPtr device);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByPciBusId_v2", CharSet = CharSet.Ansi)]
        internal static extern int GetHandleByPciBusId(string busId, out IntPtr device);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
        internal static extern int GetName(IntPtr device, byte[] name, uint length);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")]
        internal static extern int GetTemperature(IntPtr device, int sensorType, out uint tempC);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetNumFans")]
        internal static extern int GetNumFans(IntPtr device, out uint count);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed_v2")]
        internal static extern int GetFanSpeed(IntPtr device, uint fan, out uint speed);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanControlPolicy_v2")]
        internal static extern int GetFanControlPolicy(IntPtr device, uint fan, out uint policy);
        [DllImport(Dll, EntryPoint = "nvmlDeviceSetFanControlPolicy")]
        internal static extern int SetFanControlPolicy(IntPtr device, uint fan, uint policy);
        [DllImport(Dll, EntryPoint = "nvmlDeviceSetFanSpeed_v2")]
        internal static extern int SetFanSpeed(IntPtr device, uint fan, uint speed);
        [DllImport(Dll, EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2")]
        internal static extern int SetDefaultFanSpeed(IntPtr device, uint fan);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        internal static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")]
        internal static extern int GetPowerDefaultLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceSetPowerManagementLimit")]
        internal static extern int SetPowerLimit(IntPtr device, uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlErrorString", CharSet = CharSet.Ansi)]
        internal static extern IntPtr ErrorString(int error);

        internal static string Describe(int rc)
        {
            string text;
            try
            {
                IntPtr p = ErrorString(rc);
                text = p == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(p);
            }
            catch { text = "unknown"; }
            return "rc=" + rc + " (" + text + ")";
        }

        internal static uint ReadFan(IntPtr device, uint fan)
        {
            uint value;
            return GetFanSpeed(device, fan, out value) == SUCCESS ? value : 0;
        }

        internal static uint ReadTemperature(IntPtr device)
        {
            uint value;
            return GetTemperature(device, 0, out value) == SUCCESS ? value : 0;
        }
    }
}
