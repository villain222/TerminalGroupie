using System;
using System.Management;

namespace TerminalGroupie
{
    /// <summary>
    /// Process ancestry queries via WMI (Win32.Process). This is used instead of the
    /// native NtQueryInformationProcess / Toolhelp32 paths, which fail on this machine
    /// (STATUS_UNSUCCESSFUL / ERROR_MORE_DATA) but WMI works reliably.
    /// </summary>
    internal static class ProcessTree
    {
        /// <summary>
        /// Parent PID of <paramref name="pid"/>, or 0 when it cannot be determined
        /// (process gone, WMI unavailable, etc.).
        /// </summary>
        public static int GetParentPid(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + pid);
                foreach (ManagementObject o in searcher.Get())
                {
                    return Convert.ToInt32(o["ParentProcessId"]);
                }
            }
            catch
            {
                // Diagnostics/attach only; never let a lookup failure take the app down.
            }
            return 0;
        }
    }
}
