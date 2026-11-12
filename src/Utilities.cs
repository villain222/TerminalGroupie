using System;
using System.Diagnostics;

namespace TerminalGroupie
{
    public static class Utilities
    {
        /// <summary>
        /// Force-kills a process and its entire descendant tree (i.e. the shell plus every
        /// command running under it). Uses <c>taskkill /T</c> so the tree is torn down
        /// atomically on the OS side, which is more reliable than walking the tree in managed code.
        /// </summary>
        public static void KillProcessTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/F /T /PID " + pid,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
            }
            catch
            {
                // Best-effort: the process may already be gone, or taskkill may be unavailable.
            }
        }
    }
}
