using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TerminalGroupie
{
    /// <summary>
    /// Application entry point. Owns the shared <see cref="SessionManager"/> and the
    /// system-tray icon so that both outlive the main window (closing the main window
    /// only hides it; the terminals keep running in the tray).
    /// </summary>
    public partial class App : Application
    {
        private SessionManager? _sessionManager;
        private TrayIcon? _tray;
        private MainWindow? _main;

        /// <summary>True once Exit() has started; lets windows allow themselves to close.</summary>
        public static bool ShuttingDown { get; private set; }

        /// <summary>
        /// While set (self-test mode), diagnostics are appended to this file and
        /// UI-thread exceptions are logged instead of shown in a modal dialog —
        /// a modal MessageBox would hang the unattended test forever.
        /// </summary>
        public static string? TracePath { get; private set; }

        private static readonly object TraceGate = new object();

        /// <summary>Appends a timestamped line to the trace file (no-op when not in self-test mode).</summary>
        public static void Trace(string message)
        {
            string? path;
            lock (TraceGate) { path = TracePath; }
            if (path == null) return;
            try
            {
                File.AppendAllText(path, "[exit-trace] " + DateTime.Now.ToString("HH:mm:ss.fff")
                    + "  " + message + Environment.NewLine);
            }
            catch
            {
                // Diagnostics only; never let logging take the app down.
            }
        }

        /// <summary>Kill every terminal, close every window, remove the tray icon.</summary>
        public static void ExitApp()
        {
            ((App)Current).ExitNow();
        }

        /// <summary>
        /// Spawns a background watchdog that force-exits the process if the named step
        /// (e.g. the self-test exit path) has not completed within the timeout.
        /// Being a background thread, it dies with a clean exit — arming it is always safe.
        /// </summary>
        public static void ArmExitWatchdog(string tag, int timeoutMs)
        {
            var t = new Thread(() =>
            {
                Thread.Sleep(timeoutMs);
                Trace("WATCHDOG: " + tag + " did not complete within " + timeoutMs + " ms — force-exiting");
                Environment.FailFast("TerminalGroupie watchdog: " + tag + " wedged; force-exit");
            });
            t.IsBackground = true;
            t.Name = "exit-watchdog";
            t.Start();
        }

        public App()
        {
            // Keep a stray exception from silently killing the app. In self-test
            // mode a modal dialog would hang the unattended run, so log instead.
            DispatcherUnhandledException += (s, e) =>
            {
                if (TracePath != null)
                {
                    Trace("UI thread exception (suppressed): " + e.Exception);
                }
                else
                {
                    MessageBox.Show(e.Exception.Message, "TerminalGroupie",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                e.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Unattended elevation probe (no UI, no terminals):
            //   --elevation-check            report this instance's elevation state and exit
            //   --elevation-check-out <path> where the one-line report is written
            // Report line: "elevated=<True|False> elevatedSiblingRunning=<True|False> pid=<n>"
            if (e.Args.Any(a => string.Equals(a, "--elevation-check", StringComparison.OrdinalIgnoreCase)))
            {
                string outPath = Path.Combine(Path.GetTempPath(), "TerminalGroupie_elevation.txt");
                for (int i = 0; i + 1 < e.Args.Length; i++)
                {
                    if (string.Equals(e.Args[i], "--elevation-check-out", StringComparison.OrdinalIgnoreCase))
                    {
                        outPath = e.Args[i + 1];
                    }
                }
                try
                {
                    string line = "elevated=" + Elevation.IsElevated
                        + " elevatedSiblingRunning=" + Elevation.ElevatedInstanceRunning()
                        + " pid=" + Environment.ProcessId;
                    File.WriteAllText(outPath, line + Environment.NewLine);
                }
                catch
                {
                    // Diagnostics only; still exit deterministically.
                }
                ExitNow();
                return;
            }

            _sessionManager = new SessionManager(Dispatcher);
            _main = new MainWindow(_sessionManager);
            _tray = new TrayIcon(_sessionManager, ShowMainWindow);

            _main.Show();

            // Start with one cmd terminal so the app is immediately usable.
            try
            {
                _sessionManager.NewTerminal(ShellKind.Cmd);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start a terminal: " + ex.Message,
                    "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Test hooks (used by the smoke test; harmless in normal use):
            //   --elevation-check            report this instance's elevation state to a file, then exit
            //   --elevation-check-out <path> where the elevation report is written
            //   --selftest               run the full end-to-end self test, write a report, and exit
            //   --selftest-report <path> where the self-test report is written
            //   --selftest-exit          just run the full clean-exit path after a few seconds
            //   --diag                   run the maximize/input diagnostic harness, write a report, and exit
            //   --diag-report <path>     where the diag report is written
            string reportPath = Path.Combine(Path.GetTempPath(), "TerminalGroupie_selftest.txt");
            string diagPath = Path.Combine(Path.GetTempPath(), "TerminalGroupie_diag.txt");
            for (int i = 0; i + 1 < e.Args.Length; i++)
            {
                if (string.Equals(e.Args[i], "--selftest-report", StringComparison.OrdinalIgnoreCase))
                {
                    reportPath = e.Args[i + 1];
                }
                else if (string.Equals(e.Args[i], "--diag-report", StringComparison.OrdinalIgnoreCase))
                {
                    diagPath = e.Args[i + 1];
                }
            }

            if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                // Diagnostics for the unattended run: trace the exit path to a file and
                // never show modal dialogs (they would hang the test forever).
                TracePath = reportPath + ".trace";
                try
                {
                    File.WriteAllText(TracePath,
                        "[exit-trace] self-test started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine);
                }
                catch
                {
                    // Diagnostics only.
                }
                _ = SelfTest.RunAsync(_sessionManager, reportPath, ExitApp);
            }
            else if (e.Args.Any(a => string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase)))
            {
                // Diagnostic harness for the maximize/input bugs. Same unattended
                // guarantees as the self-test: trace the exit path to a file and
                // never show modal dialogs (they would hang the run forever).
                TracePath = diagPath + ".trace";
                try
                {
                    File.WriteAllText(TracePath,
                        "[exit-trace] diag started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine);
                }
                catch
                {
                    // Diagnostics only.
                }
                _ = Diag.RunAsync(_sessionManager, diagPath, ExitApp);
            }
            else if (e.Args.Any(a => string.Equals(a, "--selftest-exit", StringComparison.OrdinalIgnoreCase)))
            {
                _ = ExitAfterDelayAsync(5000);
            }
        }

        private async Task ExitAfterDelayAsync(int delayMs)
        {
            await Task.Delay(delayMs);
            ExitApp();
        }

        private void ExitNow()
        {
            if (ShuttingDown) return;
            ShuttingDown = true;

            Trace("ExitNow: begin (sessions=" + SafeSessionCount() + ")");

            // Kill the shells first so their wrapper windows can close unconditionally.
            try { _sessionManager?.Shutdown(); }
            catch (Exception ex) { Trace("ExitNow: SessionManager.Shutdown threw: " + ex); }
            Trace("ExitNow: session shutdown done; closing main window");

            try { _main?.Close(); } catch (Exception ex) { Trace("ExitNow: main.Close threw: " + ex); }
            _main = null;
            Trace("ExitNow: main window closed; disposing tray icon");

            try { _tray?.Dispose(); } catch (Exception ex) { Trace("ExitNow: tray.Dispose threw: " + ex); }
            _tray = null;
            _sessionManager = null;
            Trace("ExitNow: calling Application.Shutdown()");

            try { Shutdown(); } catch (Exception ex) { Trace("ExitNow: Application.Shutdown threw: " + ex); }
            Trace("ExitNow: Application.Shutdown() returned; ending the process deterministically");

            // All windows are closed and the tray icon removed at this point, so a
            // hard exit is safe and guarantees no leftover non-background thread
            // (e.g. a process-monitor thread) can keep the process alive.
            Environment.Exit(0);
        }

        private int SafeSessionCount()
        {
            try { return _sessionManager?.Sessions.Count ?? -1; }
            catch { return -1; }
        }

        private void ShowMainWindow()
        {
            if (_main == null)
            {
                _main = new MainWindow(_sessionManager!);
            }
            _main.Show();
            _main.Activate();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Safety net: make sure nothing outlives the process.
            _tray?.Dispose();
            _tray = null;
            _sessionManager?.Dispose();
            _sessionManager = null;
            base.OnExit(e);
        }
    }
}
