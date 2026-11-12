using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            var before = Native.TopLevelWindows();
            var process = StartShell(kind);

            int id;
            lock (_gate) { id = _nextId++; }
            var session = new TermSession(id, kind + " #" + id, kind, process);

            lock (_gate) { _sessions.Add(session); }
            RaiseChanged();

            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => OnSessionExited(session);

            // Find the new console window (off the UI thread) and re-parent it
            // into a wrapper window.
            _ = AttachWrapperAsync(session, before);
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

        private async Task AttachWrapperAsync(TermSession session, HashSet<IntPtr> before)
        {
            IntPtr console = IntPtr.Zero;
            uint shellPid = (uint)session.Process.Id;
            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < 120; i++)
                    {
                        Thread.Sleep(50);
                        foreach (var h in Native.NewConsoleWindows(before, shellPid))
                        {
                            lock (_claimGate)
                            {
                                if (_claimedConsoles.Add(h))
                                {
                                    console = h;
                                    return;
                                }
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                session.AttachNote = "console discovery crashed: " + ex;
                return;
            }

            if (console == IntPtr.Zero)
            {
                // No console window found: it stays a plain top-level window and is
                // managed with the classic show / hide / kill behavior.
                session.AttachNote = "no new console window appeared within 6s";
                return;
            }

            session.AttachNote = "console 0x" + console.ToString("X") + " found; reparenting";
            await _dispatcher.InvokeAsync(() =>
            {
                if (!session.IsAlive)
                {
                    session.AttachNote += "; shell already exited";
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

                    if (Native.SetParentTo(console, wrapperHwnd))
                    {
                        wrapper.Show();
                        Native.Show(console, Native.SW_SHOW);
                        wrapper.Activate();
                        wrapper.Dispatcher.BeginInvoke(new Action(wrapper.RefitConsole));
                        Native.SetFocus(console);
                        session.AttachNote += "; re-parent OK";
                    }
                    else
                    {
                        // Re-parenting failed (older/odd conhost builds): fall back to a
                        // plain top-level console window so the session still works.
                        session.Wrapper = null;
                        wrapper.RequestClose();
                        Native.Show(console, Native.SW_SHOW);
                        session.AttachNote += "; SetParentTo FAILED -> fallback (shell alive="
                            + session.IsAlive + ")";
                    }
                }
                catch (Exception ex)
                {
                    session.AttachNote += "; attach exception: " + ex;
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

            RaiseChanged();
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
