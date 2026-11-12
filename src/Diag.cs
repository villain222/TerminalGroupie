using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace TerminalGroupie
{
    /// <summary>
    /// Diagnostic harness, run with the "--diag" command-line argument.
    ///
    /// It reproduces the two reported bugs and captures hard evidence:
    ///   * Bug 1 (maximize): after maximizing, does the console fill the wrapper,
    ///     and does the maximized window overhang the work area (clipping the
    ///     console's left column)?
    ///   * Bug 2 (input): after maximizing / closing the first terminal, can the
    ///     console still receive typed input?
    ///
    /// "Did the input land" is proven objectively: each test types an
    ///   echo &lt;token&gt; &gt; &lt;file&gt;
    /// command into the console and then reads the file back. If the token is in
    /// the file, the keystrokes reached the shell. Foreground / keyboard-focus /
    /// owning-thread state is snapshotted before and after every step so the
    /// cross-thread focus problem (console lives on the conhost thread, our chrome
    /// lives on the WPF UI thread) is visible in the report.
    ///
    /// Writes a plain-text report and then exits the app.
    /// </summary>
    internal static class Diag
    {
        private static string TempFile(string token)
        {
            return Path.Combine(Path.GetTempPath(), "tg_diag_" + token + ".txt");
        }

        public static async Task RunAsync(SessionManager mgr, string reportPath, Action exitApp)
        {
            var lines = new List<string>();
            bool allPass = true;

            void Check(bool ok, string what)
            {
                lines.Add((ok ? "PASS  " : "FAIL  ") + what);
                if (!ok) allPass = false;
            }

            void Note(string what)
            {
                lines.Add("      " + what);
            }

            void Section(string what)
            {
                lines.Add("----");
                lines.Add(what);
            }

            void DoExit()
            {
                App.ArmExitWatchdog("diag exit path", 15000);
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
                    // Last resort.
                }
            }

            lines.Add("TerminalGroupie DIAG, " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z");
            lines.Add("----");

            try
            {
                // ---------- 0. Wait for the initial terminal to attach ----------
                TermSession? s1 = mgr.Sessions.Count > 0 ? mgr.Sessions[0] : null;
                bool attached = false;
                if (s1 != null)
                {
                    for (int i = 0; i < 80; i++)
                    {
                        await Task.Delay(250);
                        if (s1.Wrapper != null && s1.ConsoleHwnd != IntPtr.Zero && s1.IsAlive)
                        {
                            attached = true;
                            break;
                        }
                    }
                }
                Check(attached, "initial terminal attached (wrapper + console handle)"
                    + (s1 != null && s1.AttachNote != null ? "  [diag: " + s1.AttachNote + "]" : ""));

                if (!attached || s1 == null || s1.Wrapper == null)
                {
                    Finish();
                    await Task.Delay(200);
                    DoExit();
                    return;
                }

                IntPtr w1 = new WindowInteropHelper(s1.Wrapper).Handle;
                IntPtr c1 = s1.ConsoleHwnd;

                Section("0. BASELINE (before any interaction)");
                GeometryNote(w1, c1, lines);
                Note(FocusSnapshot(c1));

                // ---------- 1. Input test A: baseline typing must work ----------
                Check(await TypeAndCheck(lines, "A (baseline)", s1, "TGA_A", TempFile("A")),
                    "input lands in the console before any interaction");

                // ---------- 2. Maximize (double-click the caption) ----------
                Section("2. MAXIMIZE (double-click caption)");
                bool maximized = await TryMaximize(s1, w1);
                Note("maximize requested=" + true + "  WindowState=" + s1.Wrapper.WindowState
                     + "  (fallback used=" + (!maximized) + ")");
                GeometryNote(w1, c1, lines);
                Note(FocusSnapshot(c1));

                // Evidence for Bug 1: did the console grow to fill the wrapper?
                Native.Rect w = Native.GetRectOf(w1);
                Native.Rect c = Native.GetRectOf(c1);
                // The console legitimately fills the wrapper MINUS the custom title bar,
                // so measure the title-bar offset from the geometry rather than assuming 0.
                int titleBar = Math.Max(0, c.Top - w.Top);
                bool consoleFills = c.Width >= w.Width - 8 && c.Height >= (w.Height - titleBar - 8);
                Check(consoleFills, "console refit to fill maximized wrapper (console "
                    + c.Width + "x" + c.Height + " vs wrapper " + w.Width + "x" + w.Height
                    + " titleBar~" + titleBar + ")");

                // Evidence for Bug 1: overhang of the maximized window past the work area.
                if (Native.TryGetWorkArea(w1, out Native.Rect work))
                {
                    int overL = work.Left - w.Left;
                    int overT = work.Top - w.Top;
                    int overR = w.Right - work.Right;
                    int overB = w.Bottom - work.Bottom;
                    Note("work area " + work.Left + "," + work.Top + " " + work.Width + "x" + work.Height
                         + "  wrapper " + w.Left + "," + w.Top + " " + w.Width + "x" + w.Height);
                    Note("overhang left=" + overL + " top=" + overT + " right=" + overR + " bottom=" + overB
                         + (overL > 0 ? "  <-- left column clipped" : ""));
                    Check(overL <= 0, "maximized wrapper does not overhang the work area on the left (left=" + overL + "px)");
                }

                // ---------- 3. Input test B: after maximize ----------
                Check(await TypeAndCheck(lines, "B (after maximize)", s1, "TGA_B", TempFile("B")),
                    "input lands in the console AFTER maximize");

                // ---------- 4. Restore (double-click the caption again) ----------
                Section("4. RESTORE (double-click caption)");
                await TryRestore(s1, w1);
                Note("WindowState=" + s1.Wrapper.WindowState);
                GeometryNote(w1, c1, lines);
                Note(FocusSnapshot(c1));

                // ---------- 5. Input test C: after restore ----------
                Check(await TypeAndCheck(lines, "C (after restore)", s1, "TGA_C", TempFile("C")),
                    "input lands in the console AFTER restore");

                // ---------- 6. Kill terminal 1 (the user's 'close' action) ----------
                Section("6. KILL TERMINAL 1 (close)");
                int s1pid = s1.Process.Id;
                mgr.KillTerminal(s1);
                bool s1dead = false;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(250);
                    if (ProcessDead(s1pid)) { s1dead = true; break; }
                }
                Check(s1dead, "terminal 1 killed (pid " + s1pid + ")");
                Note("console 1 alive=" + Native.WindowExists(c1));

                // ---------- 7. Terminal 2: a fresh terminal must still be typeable ----------
                Section("7. TERMINAL 2 (fresh, after terminal 1 was closed)");
                TermSession? s2 = null;
                try
                {
                    s2 = mgr.NewTerminal(ShellKind.Cmd);
                }
                catch (Exception ex)
                {
                    Check(false, "could not start terminal 2: " + ex.Message);
                }

                if (s2 != null)
                {
                    bool a2 = false;
                    for (int i = 0; i < 80; i++)
                    {
                        await Task.Delay(250);
                        if (s2.Wrapper != null && s2.ConsoleHwnd != IntPtr.Zero && s2.IsAlive)
                        {
                            a2 = true;
                            break;
                        }
                    }
                    Check(a2, "terminal 2 attached (wrapper + console handle)"
                        + (s2.AttachNote != null ? "  [diag: " + s2.AttachNote + "]" : ""));

                    if (a2)
                    {
                        IntPtr w2 = new WindowInteropHelper(s2.Wrapper).Handle;
                        IntPtr c2 = s2.ConsoleHwnd;
                        GeometryNote(w2, c2, lines);
                        Note(FocusSnapshot(c2));
                        Check(await TypeAndCheck(lines, "D (terminal 2)", s2, "TGA_D", TempFile("D")),
                            "input lands in terminal 2 AFTER terminal 1 was closed");
                    }
                }

                // ---------- 8. Clean up ----------
                mgr.KillAll();
                await Task.Delay(400);
                Check(mgr.Sessions.Count == 0, "KillAll leaves no sessions");

                foreach (var t in new[] { "A", "B", "C", "D" })
                {
                    try { File.Delete(TempFile(t)); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                Check(false, "diag crashed: " + ex);
            }

            Finish();
            await Task.Delay(300);
            DoExit();
        }

        // ---------- helpers ----------

        /// <summary>
        /// Brings the wrapper to the front, clicks the middle of the console (the
        /// gesture a user makes to focus it), records the focus state, types an
        /// echo-to-file command, and verifies the file. Returns true if the token
        /// landed in the file (i.e. the input reached the shell).
        /// </summary>
        private static async Task<bool> TypeAndCheck(List<string> lines, string label, TermSession s, string token, string filePath)
        {
            try { File.Delete(filePath); } catch { /* ignore */ }

            IntPtr w = new WindowInteropHelper(s.Wrapper).Handle;
            IntPtr c = s.ConsoleHwnd;

            Native.BringToFront(w);
            await Task.Delay(250);

            Native.Rect crc = Native.GetRectOf(c);
            int cx = (crc.Left + crc.Right) / 2;
            int cy = (crc.Top + crc.Bottom) / 2;
            Native.ClickAt(cx, cy);
            await Task.Delay(150);
            Native.ClickAt(cx, cy);
            await Task.Delay(300);

            lines.Add("      " + label + " focus: " + FocusSnapshot(c));
            lines.Add("      " + label + " probe: " + Native.FocusProbe(c));

            Native.TypeUnicode("echo " + token + " > " + filePath);
            await Task.Delay(1200);

            bool landed = false;
            string content = "";
            try
            {
                if (File.Exists(filePath))
                {
                    content = File.ReadAllText(filePath);
                    landed = content.Contains(token);
                }
            }
            catch
            {
                // ignore
            }

            lines.Add("      " + label + " echo file " + (File.Exists(filePath) ? "exists" : "MISSING")
                 + (landed ? "" : (content.Length > 0 ? " (content: '" + Trim(content) + "')" : "")));
            return landed;
        }

        /// <summary>Double-clicks the caption to maximize; falls back to SC_MAXIMIZE.</summary>
        private static async Task<bool> TryMaximize(TermSession s, IntPtr w)
        {
            Native.BringToFront(w);
            await Task.Delay(250);

            Native.Rect rc = Native.GetRectOf(w);
            int x = (rc.Left + rc.Right) / 2;
            int y = rc.Top + 15;
            Native.ClickAt(x, y);
            await Task.Delay(60);
            Native.ClickAt(x, y);
            await Task.Delay(900);

            if (s.Wrapper?.WindowState == WindowState.Maximized)
            {
                return true;
            }

            Native.PostMaximize(w);
            await Task.Delay(900);
            return s.Wrapper?.WindowState == WindowState.Maximized;
        }

        /// <summary>Double-clicks the caption to restore; falls back to SW_RESTORE.</summary>
        private static async Task<bool> TryRestore(TermSession s, IntPtr w)
        {
            Native.BringToFront(w);
            await Task.Delay(250);

            Native.Rect rc = Native.GetRectOf(w);
            int x = (rc.Left + rc.Right) / 2;
            int y = rc.Top + 15;
            Native.ClickAt(x, y);
            await Task.Delay(60);
            Native.ClickAt(x, y);
            await Task.Delay(900);

            if (s.Wrapper?.WindowState == WindowState.Normal)
            {
                return true;
            }

            Native.Show(w, Native.SW_RESTORE);
            await Task.Delay(900);
            return s.Wrapper?.WindowState == WindowState.Normal;
        }

        private static void GeometryNote(IntPtr w, IntPtr c, List<string> lines)
        {
            if (!Native.WindowExists(w) || !Native.WindowExists(c))
            {
                lines.Add("      geometry: (window or console handle gone)");
                return;
            }
            Native.Rect wr = Native.GetRectOf(w);
            Native.Rect cr = Native.GetRectOf(c);
            lines.Add("      wrapper  " + wr.Left + "," + wr.Top + "  " + wr.Width + "x" + wr.Height
                + "  style=0x" + Native.GetStyle(w).ToString("X8")
                + "  ex=0x" + Native.GetExStyle(w).ToString("X8")
                + "  pid=" + Native.PidOf(w) + "  tid=" + Native.ThreadOf(w));
            lines.Add("      console  " + cr.Left + "," + cr.Top + "  " + cr.Width + "x" + cr.Height
                + "  child=" + Native.IsChildWindow(c)
                + "  parent=0x" + Native.GetParentOf(c).ToString("X")
                + "  pid=" + Native.PidOf(c) + "  tid=" + Native.ThreadOf(c));
        }

        private static string FocusSnapshot(IntPtr console)
        {
            IntPtr fg = Native.GetForeground();
            bool got = Native.TryGetFocusOfForegroundThread(out IntPtr focus);
            uint fgThread = fg != IntPtr.Zero ? Native.ThreadOf(fg) : 0;
            uint cThread = console != IntPtr.Zero ? Native.ThreadOf(console) : 0;

            string s = "foreground=0x" + fg.ToString("X")
                + "  focus=" + (got ? "0x" + focus.ToString("X") : "none")
                + "  fgTid=" + fgThread
                + "  consoleTid=" + cThread;

            if (console != IntPtr.Zero && got && focus == console)
            {
                s += "  [FOCUS=CONSOLE]";
            }
            else
            {
                s += "  [focus!=console]";
            }
            s += (fgThread != 0 && fgThread == cThread) ? "  [SAME-THREAD]" : "  [CROSS-THREAD]";
            return s;
        }

        private static string Trim(string s)
        {
            s = s.Trim();
            return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
        }

        private static async Task<bool> WaitForExitAsync(int pid, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ProcessDead(pid)) return true;
                await Task.Delay(100);
            }
            return ProcessDead(pid);
        }

        private static bool ProcessDead(int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return p.HasExited;
            }
            catch (System.ArgumentException)
            {
                return true;
            }
        }
    }
}
