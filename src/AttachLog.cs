using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TerminalGroupie
{
    /// <summary>
    /// Append-only diagnostic log of terminal attach attempts, written on every
    /// "New terminal" so that when attaching fails on a given machine we have the
    /// full picture of what the flow actually saw: the shell's process ancestor
    /// chain, the console host it resolved to, the top-level window inventory
    /// (handle, class, owning pid/name, visibility), and the outcome.
    /// Location: %TEMP%\TerminalGroupie_attach.log
    /// </summary>
    internal static class AttachLog
    {
        private static readonly object Gate = new object();

        public static string LogFilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "TerminalGroupie_attach.log"); }
        }

        public static void Append(string block)
        {
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(LogFilePath, block + Environment.NewLine);
                }
                catch
                {
                    // Diagnostics only; never let logging take the app down.
                }
            }
        }

        /// <summary>The process ancestor chain of <paramref name="pid"/>, up to 3 levels.</summary>
        public static string AncestorChain(int pid)
        {
            var sb = new StringBuilder();
            int current = pid;
            for (int level = 1; level <= 3; level++)
            {
                try
                {
                    using var p = Process.GetProcessById(current);
                    sb.Append("  level").Append(level)
                      .Append(" pid=").Append(current)
                      .Append(" name=").Append(p.ProcessName).Append(".exe");
                    int parent = ProcessTree.GetParentPid(current);
                    if (parent == 0)
                    {
                        break;
                    }
                    current = parent;
                }
                catch
                {
                    sb.Append("  level").Append(level).Append(" pid=").Append(current).Append(" (no longer readable)");
                    break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Every top-level window: handle, class, owning pid (and name), visibility.</summary>
        public static string WindowDump()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var w in Native.TopLevelWindows())
                {
                    sb.Append("  0x").Append(w.Handle.ToString("X"))
                      .Append(" class=").Append(w.ClassName)
                      .Append(" pid=").Append(w.Pid);
                    try
                    {
                        using var p = Process.GetProcessById((int)w.Pid);
                        sb.Append(" name=").Append(p.ProcessName).Append(".exe");
                    }
                    catch
                    {
                        // elevated or gone — pid is still in the line
                    }
                    sb.Append(" visible=").Append(w.Visible).Append('\n');
                }
            }
            catch
            {
                sb.Append("  (window enumeration failed)\n");
            }
            return sb.ToString();
        }
    }
}
