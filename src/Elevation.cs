using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace TerminalGroupie
{
    /// <summary>
    /// Elevation helpers. A standard (medium-integrity) instance manages standard
    /// terminals; an elevated instance — launched via UAC — can additionally manage
    /// ADMIN terminals (attach, re-parent, kill), because Windows only allows
    /// re-parenting / killing at the same integrity level and below. The two kinds
    /// of instance are told apart in the tray: green icon = standard, red icon = elevated.
    /// </summary>
    public static class Elevation
    {
        /// <summary>True when this process is running elevated.</summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Window/tray suffix that marks the two instance kinds.</summary>
        public static string Suffix => IsElevated ? " (elevated)" : "";

        /// <summary>
        /// True when another TerminalGroupie process is running elevated. A standard
        /// (medium) process is denied a handle to an elevated one, so an OpenProcess
        /// that fails with ACCESS_DENIED against a sibling instance is the probe.
        /// </summary>
        public static bool ElevatedInstanceRunning()
        {
            try
            {
                int self = Environment.ProcessId;
                foreach (var p in Process.GetProcessesByName("TerminalGroupie"))
                {
                    using (p)
                    {
                        if (p.Id == self) continue;
                        try
                        {
                            IntPtr h = Native.OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */, p.Id);
                            Native.CloseHandle(h);
                            // opened fine -> same (standard) integrity
                        }
                        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ERROR_ACCESS_DENIED */)
                        {
                            return true; // present but untouchable -> elevated
                        }
                    }
                }
            }
            catch
            {
                // Best-effort probe; a crash means "no" (the menu will just try to launch).
            }
            return false;
        }

        /// <summary>
        /// Launches a new elevated instance of this app (raises the UAC prompt).
        /// Returns false when the user declined or the launch failed.
        /// </summary>
        public static bool OpenElevatedInstance()
        {
            try
            {
                string? exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                {
                    return false;
                }
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var p = Process.Start(psi);
                return p != null;
            }
            catch (Win32Exception)
            {
                return false; // UAC declined
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
