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
        private bool _disposed;

        public TrayIcon(SessionManager mgr, Action showMain)
        {
            _mgr = mgr;
            _showMain = showMain;

            _menu = new ContextMenuStrip();
            _notify = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "TerminalGroupie",
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

            _menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (a, b) => App.ExitApp();
            _menu.Items.Add(exit);
        }

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
