using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TerminalGroupie
{
    /// <summary>Which shell a terminal session runs in.</summary>
    public enum ShellKind
    {
        Cmd,
        PowerShell
    }

    /// <summary>
    /// A single managed terminal: a live shell process plus the handle of its
    /// console window and (normally) the custom-chrome wrapper window that hosts it.
    /// </summary>
    public sealed class TermSession
    {
        public int Id { get; }
        public string Title { get; set; }
        public ShellKind Kind { get; }
        public Process Process { get; }

        /// <summary>Handle of the real console window (re-parented into <see cref="Wrapper"/> when one exists).</summary>
        public IntPtr ConsoleHwnd { get; internal set; }

        /// <summary>Custom-chrome wrapper window hosting the console; null = plain top-level console.</summary>
        public TerminalWindow? Wrapper { get; internal set; }

        /// <summary>True while the window is hidden to the tray (the shell still runs).</summary>
        public bool IsHidden { get; internal set; }

        /// <summary>Last diagnostic note from the wrapper-attach flow (null = attach never ran yet).</summary>
        public string? AttachNote { get; internal set; }

        /// <summary>
        /// Human-readable attach outcome for the UI: "wrapped" when the custom
        /// chrome window is in use, the failure reason otherwise.
        /// </summary>
        public string StatusText
        {
            get
            {
                if (Wrapper != null) return "wrapped";
                if (AttachNote != null) return AttachNote;
                return "attaching…";
            }
        }

        public TermSession(int id, string title, ShellKind kind, Process process)
        {
            Id = id;
            Title = title;
            Kind = kind;
            Process = process;
        }

        public bool IsAlive
        {
            get
            {
                try { return !Process.HasExited; }
                catch { return false; }
            }
        }

        public string StateText => IsAlive ? (IsHidden ? "in tray" : "visible") : "exited";

        /// <summary>Bring the terminal back on screen (and focus it).</summary>
        public void Show()
        {
            if (Wrapper != null)
            {
                Wrapper.ShowFromTray();
                return;
            }
            if (ConsoleHwnd != IntPtr.Zero)
            {
                Native.BringToFront(ConsoleHwnd);
            }
            IsHidden = false;
        }

        /// <summary>Send the terminal to the tray (the process keeps running in the background).</summary>
        public void Hide()
        {
            if (Wrapper != null)
            {
                Wrapper.HideToTray();
                return;
            }
            if (ConsoleHwnd != IntPtr.Zero)
            {
                Native.Show(ConsoleHwnd, Native.SW_HIDE);
            }
            IsHidden = true;
        }

        /// <summary>Kill the shell and every command running under it.</summary>
        public void Kill()
        {
            Utilities.KillProcessTree(Process.Id);
        }
    }

    /// <summary>
    /// Owns the set of live <see cref="TermSession"/>s and the logic to create, show,
    /// hide and kill them. Raises <see cref="SessionsChanged"/> — always marshalled to the
    /// UI thread — whenever the collection or the visible state of a session changes.
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        private readonly List<TermSession> _sessions = new List<TermSession>();
        private readonly object _gate = new object();

        // Console windows already assigned to a session (guards against two
        // sessions grabbing the same window when created back-to-back).
        private readonly HashSet<IntPtr> _claimedConsoles = new HashSet<IntPtr>();
        private readonly object _claimGate = new object();

        private int _nextId = 1;
        private readonly Dispatcher _dispatcher;

        public event Action? SessionsChanged;

        public SessionManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public IReadOnlyList<TermSession> Sessions
        {
            get { lock (_gate) { return _sessions.ToList().AsReadOnly(); } }
        }

        /// <summary>Launch a new terminal (cmd or PowerShell) and wrap it in a custom-chrome window.</summary>
        public TermSession NewTerminal(ShellKind kind)
        {
            // Snapshot the conhost processes that exist BEFORE we launch, so the
            // attach flow can identify OUR new conhost by what appears afterwards.
            // A pre-existing conhost (the user's own terminal) is in this set and is
            // therefore never adopted.
            var beforeConhosts = Native.ConhostPids();

            var process = StartShell(kind);

            int id;
            lock (_gate) { id = _nextId++; }
            var session = new TermSession(id, kind + " #" + id, kind, process);

            lock (_gate) { _sessions.Add(session); }
            RaiseChanged();

            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => OnSessionExited(session);

            // Find OUR new conhost (off the UI thread) and re-parent its console
            // window into a wrapper window.
            _ = AttachWrapperAsync(session, beforeConhosts);
            return session;
        }

        public void ShowTerminal(TermSession session)
        {
            session.Show();
            RaiseChanged();
        }

        public void HideTerminal(TermSession session)
        {
            session.Hide();
            RaiseChanged();
        }

        /// <summary>The wrapper window changed a session's hidden state; refresh the UI.</summary>
        public void NotifyStateChanged(TermSession session)
        {
            RaiseChanged();
        }

        public void KillTerminal(TermSession session)
        {
            try { session.Kill(); } catch { /* ignore */ }
            session.Wrapper?.RequestClose();
            if (RemoveSession(session))
            {
                RaiseChanged();
            }
        }

        public void KillAll()
        {
            TermSession[] snapshot;
            lock (_gate) { snapshot = _sessions.ToArray(); }
            foreach (var s in snapshot)
            {
                try { s.Kill(); } catch { /* ignore */ }
                s.Wrapper?.RequestClose();
            }
        }

        /// <summary>Full shutdown: kill every terminal and close its window.</summary>
        public void Shutdown()
        {
            KillAll();
        }

        public void Dispose()
        {
            KillAll();
        }

        // ---------- internals ----------

        private static Process StartShell(ShellKind kind)
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            psi.FileName = (kind == ShellKind.PowerShell) ? "powershell.exe" : "cmd.exe";

            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("Failed to start the terminal process.");
            return p;
        }

        private async Task AttachWrapperAsync(TermSession session, HashSet<uint> beforeConhosts)
        {
            IntPtr console = IntPtr.Zero;
            int hostPid = 0;
            string hostName = "";
            uint setParentError = 0;
            string outcome = "";
            int shellPid = session.Process.Id;

            // ---- phase 1 (off the UI thread): find THIS shell's console window ----
            // Primary (verified empirically): a console process OWNS its console
            // window — the visible ConsoleWindowClass top-level window belongs to the
            // shell's own pid (conhost only renders it). Matching the window to the
            // shell's pid is 1:1 and provably ours; a foreign window (the user's own
            // terminal) can never be returned because it is owned by a different pid.
            //
            // Fallback: some configurations have a NEW conhost.exe own the window
            // instead; a conhost that was already running before our launch is in
            // beforeConhosts and is never adopted.
            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < 120; i++) // up to ~6 seconds
                    {
                        Thread.Sleep(50);
                        if (!session.IsAlive)
                        {
                            return; // shell is gone; nothing to attach
                        }

                        // Primary: the console window owned by our shell.
                        IntPtr w = Native.FindConsoleWindowByPid((uint)shellPid, "ConsoleWindowClass");
                        if (w == IntPtr.Zero)
                        {
                            // Fallback: a new conhost that owns the window.
                            var newConhosts = NewConhosts(beforeConhosts);
                            if (newConhosts.Count == 0)
                            {
                                continue; // console window not up yet
                            }

                            uint host = newConhosts.Count == 1
                                ? newConhosts[0]
                                : PickOurConhost(newConhosts, shellPid);
                            hostName = "conhost";
                            hostPid = (int)host;
                            w = Native.FindVisibleWindowByPid(host);
                            if (w == IntPtr.Zero)
                            {
                                continue; // conhost up but its window not visible yet
                            }
                        }
                        else
                        {
                            hostName = "shell";
                            hostPid = shellPid;
                        }

                        lock (_claimGate)
                        {
                            if (_claimedConsoles.Add(w))
                            {
                                console = w;
                            }
                        }
                        return;
                    }
                });
            }
            catch (Exception ex)
            {
                session.AttachNote = "console discovery crashed: " + ex.Message;
                outcome = "discovery crashed: " + ex.Message;
                LogAttach(session, outcome, hostName, hostPid, IntPtr.Zero, 0);
                return;
            }

            if (console == IntPtr.Zero)
            {
                if (!session.IsAlive)
                {
                    session.AttachNote = "shell exited before attach";
                    outcome = "shell exited before attach";
                }
                else if (WindowsTerminalRunning())
                {
                    // The shell is hosted inside a shared Windows Terminal window,
                    // which may host many tabs/sessions. Re-parenting or hiding it
                    // would break the user's other sessions, so we leave it exactly
                    // as it is.
                    session.AttachNote = "hosted by Windows Terminal (shared host) — left as plain window";
                    outcome = "shared host: WindowsTerminal";
                }
                else
                {
                    session.AttachNote = "no console window appeared within 6s";
                    outcome = "no console window within 6s";
                }
                LogAttach(session, outcome, hostName, hostPid, IntPtr.Zero, 0);
                RaiseChanged();
                return;
            }

            session.AttachNote = "host " + hostName + " pid=" + hostPid + "; console 0x" + console.ToString("X") + "; reparenting";
            await _dispatcher.InvokeAsync(() =>
            {
                if (!session.IsAlive)
                {
                    session.AttachNote += "; shell already exited";
                    outcome = "shell exited before reparent";
                    return;
                }

                session.ConsoleHwnd = console;

                try
                {
                    // Hide the console before re-parenting so it doesn't flash at its
                    // original screen location.
                    Native.Show(console, Native.SW_HIDE);

                    var wrapper = new TerminalWindow(session, this);
                    session.Wrapper = wrapper;
                    IntPtr wrapperHwnd = new WindowInteropHelper(wrapper).EnsureHandle();

                    if (Native.SetParentTo(console, wrapperHwnd, out setParentError))
                    {
                        wrapper.Show();
                        Native.Show(console, Native.SW_SHOW);
                        wrapper.Activate();
                        wrapper.Dispatcher.BeginInvoke(new Action(wrapper.RefitConsole));
                        Native.SetFocus(console);
                        session.AttachNote += "; re-parent OK";
                        outcome = "wrapped";
                    }
                    else
                    {
                        // Re-parenting failed: fall back to a plain top-level console
                        // window so the session still works. The window is provably
                        // ours (owned by this shell's own pid), so classic show/hide
                        // on it is safe.
                        session.Wrapper = null;
                        wrapper.RequestClose();
                        Native.Show(console, Native.SW_SHOW);
                        session.AttachNote += "; SetParentTo FAILED (win32 err " + setParentError + ") -> plain window";
                        outcome = "SetParentTo failed (win32 err " + setParentError + ") -> plain window";
                    }
                }
                catch (Exception ex)
                {
                    session.AttachNote += "; attach exception: " + ex;
                    outcome = "attach exception: " + ex.Message;
                    var w = session.Wrapper;
                    session.Wrapper = null;
                    try
                    {
                        w?.RequestClose();
                        Native.Show(console, Native.SW_SHOW);
                    }
                    catch
                    {
                        // best effort
                    }
                }
            });

            if (outcome.Length == 0)
            {
                outcome = "wrapped";
            }
            LogAttach(session, outcome, hostName, hostPid, console, setParentError);
            RaiseChanged();
        }

        /// <summary>
        /// conhost PIDs that are running NOW but were not in <paramref name="before"/>.
        /// These are the conhosts spawned since our launch — candidates for being
        /// this terminal's host.
        /// </summary>
        private static List<uint> NewConhosts(HashSet<uint> before)
        {
            var result = new List<uint>();
            foreach (var pid in Native.ConhostPids())
            {
                if (!before.Contains(pid))
                {
                    result.Add(pid);
                }
            }
            return result;
        }

        /// <summary>
        /// From several new conhosts, pick the one that renders <paramref name="shellPid"/>:
        /// the one whose parent is our shell (WMI parent-PID). Falls back to the first
        /// candidate if the parent cannot be determined.
        /// </summary>
        private static uint PickOurConhost(List<uint> candidates, int shellPid)
        {
            foreach (var c in candidates)
            {
                if (ProcessTree.GetParentPid((int)c) == shellPid)
                {
                    return c;
                }
            }
            return candidates[0];
        }

        /// <summary>True if Windows Terminal (the shared Win11 terminal host) is running.</summary>
        private static bool WindowsTerminalRunning()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("WindowsTerminal"))
                {
                    using (p)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // best effort
            }
            return false;
        }

        /// <summary>
        /// Append one attach attempt (with the full process-tree and window evidence)
        /// to %TEMP%\TerminalGroupie_attach.log so failures on any machine are
        /// diagnosable without a round trip.
        /// </summary>
        private static void LogAttach(TermSession session, string outcome, string hostName, int hostPid, IntPtr console, uint setParentError)
        {
            var sb = new StringBuilder();
            sb.Append("== attach ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("  ").Append(session.Title)
              .Append("  (shell pid=").Append(session.Process.Id).Append(") ==").Append('\n');
            sb.Append(AttachLog.AncestorChain(session.Process.Id)).Append('\n');
            sb.Append("  host: ").Append(hostName.Length > 0 ? hostName + " pid=" + hostPid : "none found").Append('\n');
            sb.Append("  console window: ").Append(console == IntPtr.Zero ? "none" : "0x" + console.ToString("X")).Append('\n');
            sb.Append("  SetParentTo win32 error: ").Append(setParentError).Append('\n');
            sb.Append("  outcome: ").Append(outcome).Append('\n');
            sb.Append("  top-level windows:\n").Append(AttachLog.WindowDump());
            AttachLog.Append(sb.ToString());
        }

        private void OnSessionExited(TermSession session)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                session.Wrapper?.RequestClose();
                if (RemoveSession(session))
                {
                    RaiseChanged();
                }
            }));
        }

        private bool RemoveSession(TermSession session)
        {
            lock (_gate) { return _sessions.Remove(session); }
        }

        private void RaiseChanged()
        {
            if (_dispatcher.CheckAccess())
            {
                SessionsChanged?.Invoke();
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(() => SessionsChanged?.Invoke()));
            }
        }
    }
}
