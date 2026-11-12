using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace TerminalGroupie
{
    /// <summary>
    /// End-to-end self test, run with the "--selftest" command-line argument.
    /// Verifies console re-parenting, minimize-to-tray on every minimize path,
    /// that the shell keeps running while hidden, whole-tree killing, and a
    /// clean app exit. Writes a plain-text report and then exits the app.
    /// </summary>
    internal static class SelfTest
    {
        public static async Task RunAsync(SessionManager mgr, string reportPath, Action exitApp)
        {
            var lines = new List<string>();
            bool allPass = true;

            void Check(bool ok, string what)
            {
                lines.Add((ok ? "PASS  " : "FAIL  ") + what);
                if (!ok) allPass = false;
            }

            // Arm a watchdog that force-exits the process if the exit path wedges,
            // so an unattended test can never hang forever.
            void DoExit()
            {
                App.ArmExitWatchdog("self-test exit path", 15000);
                exitApp();
            }

            void Finish()
            {
                lines.Add("----");
                lines.Add("RESULT: " + (allPass ? "PASS" : "FAIL"));
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllLines(reportPath, lines);
                }
                catch
                {
                    // Last resort: if the report can't be written there is nothing else we can do.
                }
            }

            lines.Add("TerminalGroupie self test, " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z");
            lines.Add("----");

            try
            {
                // 1. The initial cmd terminal must get a wrapper with a re-parented console.
                var s = mgr.Sessions.Count > 0 ? mgr.Sessions[0] : null;
                bool attached = false;
                if (s != null)
                {
                    for (int i = 0; i < 80; i++)
                    {
                        await Task.Delay(250);
                        if (s.Wrapper != null && s.ConsoleHwnd != IntPtr.Zero && s.IsAlive)
                        {
                            attached = true;
                            break;
                        }
                    }
                }
                Check(attached, "cmd terminal attached (wrapper + console handle)"
                    + (s != null && s.AttachNote != null ? "  [diag: " + s.AttachNote + "]" : ""));
                if (s == null || s.Wrapper == null)
                {
                    Finish();
                    await Task.Delay(200);
                    DoExit();
                    return;
                }

                IntPtr wrapperHwnd = new WindowInteropHelper(s.Wrapper!).Handle;
                IntPtr parent = Native.GetParentOf(s.ConsoleHwnd);
                Check(parent == wrapperHwnd,
                    "console re-parented into wrapper (GetParent=0x" + parent.ToString("X")
                    + ", wrapper=0x" + wrapperHwnd.ToString("X") + ")");
                Check(Native.IsChildWindow(s.ConsoleHwnd), "console carries the WS_CHILD style");

                // 2. Minimize through the wrapper's tray button.
                s.Wrapper!.HideToTray();
                await Task.Delay(400);
                Check(s.IsHidden, "HideToTray() marks the session hidden");
                Check(!Native.IsVisible(wrapperHwnd), "wrapper window is not visible after hide");
                Check(s.IsAlive, "shell process still alive while hidden");

                // 3. Restore from the tray.
                s.Wrapper.ShowFromTray();
                await Task.Delay(500);
                Check(!s.IsHidden, "ShowFromTray() restores the session");
                Check(Native.IsVisible(wrapperHwnd), "wrapper window visible again");

                // The console should sit inside the wrapper's rectangle (FitConsole ran).
                Native.Rect crc = Native.GetRectOf(s.ConsoleHwnd);
                Native.Rect wrc = Native.GetRectOf(wrapperHwnd);
                bool inside = crc.Left >= wrc.Left - 1 && crc.Top >= wrc.Top - 1
                    && crc.Right <= wrc.Right + 2 && crc.Bottom <= wrc.Bottom + 2
                    && crc.Width > 10 && crc.Height > 10;
                Check(inside, "console positioned/sized inside the wrapper ("
                    + crc.Width + "x" + crc.Height + " at " + crc.Left + "," + crc.Top
                    + " vs wrapper " + wrc.Width + "x" + wrc.Height + " at " + wrc.Left + "," + wrc.Top + ")");

                // 4. Taskbar minimize path (WM_SYSCOMMAND SC_MINIMIZE).
                Native.PostMinimize(wrapperHwnd);
                await Task.Delay(600);
                Check(s.IsHidden, "taskbar minimize routed to tray (WM_SYSCOMMAND path)");
                Check(s.IsAlive, "shell still alive after the WM_SYSCOMMAND path");

                // 5. Win+D style path (ShowWindow SW_MINIMIZE -> state change).
                s.Wrapper.ShowFromTray();
                await Task.Delay(400);
                Native.Show(wrapperHwnd, Native.SW_MINIMIZE);
                await Task.Delay(600);
                Check(s.IsHidden, "Win+D-style minimize routed to tray (state-change path)");
                Check(s.IsAlive, "shell still alive after the state-change path");

                // 6. Tree kill on a synthetic parent -> child pair (proves taskkill /T).
                //    The parent writes the child's PID to a file (this machine's
                //    "tasklist PARENT" filter is broken) and stays alive via
                //    WaitForExit() so the child genuinely sits in its tree at kill time.
                string pidFile = Path.Combine(Path.GetTempPath(), "tg_childpid_" + Environment.ProcessId + ".txt");
                try { File.Delete(pidFile); } catch { /* ignore */ }

                var parentProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"$c = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 300' -PassThru; "
                        + "if ($c) { $c.Id | Set-Content -LiteralPath '" + pidFile + "' }; $c.WaitForExit()\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (parentProc != null)
                {
                    int aPid = parentProc.Id;
                    int bPid = 0;
                    for (int i = 0; i < 40 && bPid == 0; i++)
                    {
                        await Task.Delay(250);
                        try
                        {
                            if (File.Exists(pidFile)
                                && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)
                                && pid > 0 && pid != aPid)
                            {
                                bPid = pid;
                            }
                        }
                        catch
                        {
                            // File may be mid-write; try again next tick.
                        }
                    }
                    Check(bPid > 0, "test tree spawned a child process (child pid " + bPid + ")");

                    Utilities.KillProcessTree(aPid);
                    bool aDead = false, bDead = false;
                    for (int i = 0; i < 40; i++)
                    {
                        await Task.Delay(250);
                        aDead = ProcessDead(aPid);
                        bDead = bPid > 0 ? ProcessDead(bPid) : true;
                        if (aDead && bDead) break;
                    }
                    Check(aDead, "tree-kill removed the parent shell");
                    Check(bPid > 0 && bDead, "tree-kill removed the child process");
                    try { File.Delete(pidFile); } catch { /* ignore */ }
                }
                else
                {
                    Check(false, "could not start the synthetic test tree");
                }

                // 7. Kill the real cmd terminal.
                s.Wrapper.ShowFromTray();
                await Task.Delay(400);
                int sessionPid = s.Process.Id;
                mgr.KillTerminal(s);
                bool sessionDead = await WaitForExitAsync(s.Process, 5000);
                Check(sessionDead, "KillTerminal killed the cmd shell (pid " + sessionPid + ")");
                Check(!mgr.Sessions.Any(x => x.Id == s.Id), "killed session removed from the list");

                // 8. A second terminal (PowerShell) attaches and can be killed too.
                TermSession? s2 = null;
                try
                {
                    s2 = mgr.NewTerminal(ShellKind.PowerShell);
                }
                catch (Exception ex)
                {
                    Check(false, "could not start a PowerShell terminal: " + ex.Message);
                }
                if (s2 != null)
                {
                    bool s2Attached = false;
                    for (int i = 0; i < 80; i++)
                    {
                        await Task.Delay(250);
                        if (s2.Wrapper != null && s2.ConsoleHwnd != IntPtr.Zero && s2.IsAlive)
                        {
                            s2Attached = true;
                            break;
                        }
                    }
                    Check(s2Attached, "PowerShell terminal attached (wrapper + console handle)"
                        + (s2.AttachNote != null ? "  [diag: " + s2.AttachNote + "]" : ""));
                    if (s2Attached)
                    {
                        IntPtr p2 = Native.GetParentOf(s2.ConsoleHwnd);
                        IntPtr w2 = new WindowInteropHelper(s2.Wrapper!).Handle;
                        Check(p2 == w2, "PowerShell console re-parented into its wrapper");
                        mgr.KillTerminal(s2);
                        Check(await WaitForExitAsync(s2.Process, 5000), "PowerShell terminal killed cleanly");
                    }
                }

                // 9. KillAll leaves nothing behind.
                mgr.KillAll();
                await Task.Delay(400);
                Check(mgr.Sessions.Count == 0, "KillAll leaves no sessions");
            }
            catch (Exception ex)
            {
                Check(false, "self test crashed: " + ex);
            }

            Finish();
            await Task.Delay(300);
            DoExit();
        }

        private static async Task<bool> WaitForExitAsync(Process p, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ProcessDead(p.Id))
                {
                    return true;
                }
                await Task.Delay(100);
            }
            return ProcessDead(p.Id);
        }

        private static bool ProcessDead(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.HasExited;
            }
            catch (ArgumentException)
            {
                return true; // no such process
            }
        }
    }
}
