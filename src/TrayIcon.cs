using System;
using System.Windows;
using System.Windows.Forms;

namespace TerminalGroupie
{
    /// <summary>
    /// System-tray icon (WinForms NotifyIcon) that lists every running terminal.
    /// Each terminal offers "Open in window" and "Kill terminal"; plus New (cmd/PowerShell)
    /// and Exit. Double-clicking the icon restores the main window.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _notify;
        private readonly ContextMenuStrip _menu;
        private readonly SessionManager _mgr;
        private readonly Action _showMain;
        private readonly bool _elevated;
        private readonly bool _canSetDefaultTerminal;
        private readonly bool _canRestoreOriginal;
        private bool _disposed;

        public TrayIcon(SessionManager mgr, Action showMain)
        {
            _mgr = mgr;
            _showMain = showMain;
            _elevated = Elevation.IsElevated;

            // We can set the default terminal if either: no policy lock exists (HKCU works), or we're elevated.
            bool policyLocked = DefaultTerminal.DefaultTerminalLockedByPolicy();
            _canSetDefaultTerminal = !policyLocked || _elevated;
            var currentPair = DefaultTerminal.GetCurrent();
            _canRestoreOriginal = currentPair != null && string.IsNullOrEmpty(currentPair.Item1) == false;

            _menu = new ContextMenuStrip();
            _notify = new NotifyIcon
            {
                // Green icon = standard instance, red icon = elevated instance.
                Icon = TrayIcons.For(_elevated),
                Text = "TerminalGroupie" + Elevation.Suffix,
                Visible = true,
                ContextMenuStrip = _menu
            };
            _notify.DoubleClick += (s, e) => _showMain();

            _mgr.SessionsChanged += RebuildMenu;
            RebuildMenu();
        }

        private void RebuildMenu()
        {
            _menu.Items.Clear();

            var sessions = _mgr.Sessions;
            if (sessions.Count == 0)
            {
                _menu.Items.Add(new ToolStripMenuItem("(no running terminals)") { Enabled = false });
            }

            foreach (var s in sessions)
            {
                var state = s.IsAlive ? (s.IsHidden ? "hidden" : "visible") : "exited";
                var item = new ToolStripMenuItem(s.Title + "  [" + state + "]");

                var open = new ToolStripMenuItem("Open in window");
                open.Click += (a, b) => _mgr.ShowTerminal(s);

                var kill = new ToolStripMenuItem("Kill terminal");
                kill.Click += (a, b) => _mgr.KillTerminal(s);

                item.DropDownItems.Add(open);
                item.DropDownItems.Add(kill);
                _menu.Items.Add(item);
            }

            _menu.Items.Add(new ToolStripSeparator());

            var newCmd = new ToolStripMenuItem("New terminal (cmd)");
            newCmd.Click += (a, b) => { _showMain(); _mgr.NewTerminal(ShellKind.Cmd); };

            var newPs = new ToolStripMenuItem("New terminal (PowerShell)");
            newPs.Click += (a, b) => { _showMain(); _mgr.NewTerminal(ShellKind.PowerShell); };

            _menu.Items.Add(newCmd);
            _menu.Items.Add(newPs);

            // Default-terminal delegation menu (visible when there's a saved pair).
            AddDefaultTerminalItems();

            if (_elevated)
            {
                // This instance runs elevated: it is the one that can manage
                // admin terminals (attach, re-parent, kill) end-to-end.
                _menu.Items.Add(new ToolStripMenuItem("This is the ELEVATED instance") { Enabled = false });
            }
            else
            {
                // Standard instance: offer to spawn the elevated sibling. Windows
                // won't let a medium-integrity process manage elevated terminals,
                // so the elevated instance is a separate, red-icon process.
                bool alreadyUp = Elevation.ElevatedInstanceRunning();
                var elev = new ToolStripMenuItem(alreadyUp
                    ? "Elevated instance already running"
                    : "Open elevated instance (UAC)…");
                elev.Enabled = !alreadyUp;
                elev.Click += (a, b) =>
                {
                    if (!Elevation.OpenElevatedInstance())
                    {
                        System.Windows.MessageBox.Show(
                            "Could not start an elevated instance (UAC declined or launch failed).",
                            "TerminalGroupie", System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                };
                _menu.Items.Add(elev);

                // Also offer default-terminal controls from the standard tray.
                AddDefaultTerminalItems();
            }

            _menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem(_elevated ? "Exit elevated instance" : "Exit");
            exit.Click += (a, b) => App.ExitApp();
            _menu.Items.Add(exit);
        }

        /// <summary>Add default-terminal items to the tray menu (caller ensures separator). Visible only when a pair exists.</summary>
        private void AddDefaultTerminalItems()
        {
            var currentPair = DefaultTerminal.GetCurrent();
            if (currentPair == null) return;

            bool isWCH = DefaultTerminal.IsWindowsConsoleHost();

            // "Change to WCH" — only show when delegate is NOT already WCH and we can write HKCU.
            if (!isWCH && _canSetDefaultTerminal)
            {
                var setWch = new ToolStripMenuItem("Change default terminal to Windows Console Host…");
                setWch.Click += (a, b) => OnSetDefaultTerminalToWCH();
                _menu.Items.Add(setWch);
            }

            // "Restore original" — only when we're currently on WCH and originals were saved.
            if (_canRestoreOriginal && isWCH)
            {
                var restore = new ToolStripMenuItem("Restore previous default terminal");
                restore.Click += (a, b) => OnRestoreOriginalDefaultTerminal();
                _menu.Items.Add(restore);
            }

            // Show current state.
            string currentState;
            if (isWCH) currentState = "Windows Console Host";
            else currentState = currentPair.Item2;
            var statusItem = new ToolStripMenuItem("Default terminal: " + currentState) { Enabled = false };
            _menu.Items.Add(statusItem);

            _menu.Items.Add(new ToolStripSeparator());
        }

        private void OnSetDefaultTerminalToWCH()
        {
            // If user hasn't consented yet, show the consent dialog first.
            if (!DefaultTerminal.HasUserConsented())
            {
                bool isWT = DefaultTerminal.IsWindowsTerminal();
                var dlg = new ConsentDialog();
                // No reliable owner from ContextMenuStrip; the dialog will center on screen if owner is null.
                dlg.ShowDialog();

                if (!dlg.Allowed) return;
            }

            bool success = DefaultTerminal.SaveAndSetToWindowsConsoleHost();
            RebuildMenu();

            if (success)
            {
                System.Windows.MessageBox.Show(
                    "Default terminal changed to Windows Console Host.\nNew terminals will appear in TerminalGroupie.",
                    "TerminalGroupie", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }

        private void OnRestoreOriginalDefaultTerminal()
        {
            var result = System.Windows.MessageBox.Show(
                "Restore your previous default terminal?\n\n" +
                ("Current: Windows Console Host").Replace("\r", "").TrimEnd(),
                "TerminalGroupie", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            bool success = DefaultTerminal.RestoreOriginal();
            RebuildMenu();

            if (success)
            {
                System.Windows.MessageBox.Show(
                    "Your previous default terminal has been restored.",
                    "TerminalGroupie", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }

        /// <summary>Public helper for the app to refresh tray items (e.g., after default-terminal changes).</summary>
        public void RefreshMenu() => RebuildMenu();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mgr.SessionsChanged -= RebuildMenu;
            _notify.Visible = false;
            _notify.Dispose();
            _menu.Dispose();
        }
    }
}
