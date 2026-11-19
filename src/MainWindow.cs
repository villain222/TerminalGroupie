using System;
using System.ComponentModel;
using System.Windows;

namespace TerminalGroupie
{
    /// <summary>
    /// The manager window: lists every running terminal and lets you open it,
    /// minimize it to the tray, kill it, or create new cmd / PowerShell terminals.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SessionManager _mgr;

        public MainWindow(SessionManager mgr)
        {
            _mgr = mgr;
            InitializeComponent();

            // Mark the instance kind in the title bar (e.g. "TerminalGroupie (elevated)")
            // and carry the matching icon: green = standard, red = elevated.
            Title = "TerminalGroupie" + Elevation.Suffix;
            Icon = TrayIcons.ToImageSource(Elevation.IsElevated);

            _mgr.SessionsChanged += Refresh;

            NewCmdBtn.Click += (s, e) => _mgr.NewTerminal(ShellKind.Cmd);
            NewPsBtn.Click += (s, e) => _mgr.NewTerminal(ShellKind.PowerShell);

            MinimizeTrayBtn.Click += (s, e) => Hide();

            KillAllBtn.Click += (s, e) =>
            {
                var result = MessageBox.Show(
                    "Kill ALL terminals and every command running in them?",
                    "TerminalGroupie", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _mgr.KillAll();
                }
            };

            ExitBtn.Click += (s, e) => App.ExitApp();

            DefaultTerminalBtn.Click += (s, e) => OnDefaultTerminalClicked();

            OpenBtn.Click += (s, e) => { var sel = Selected(); if (sel != null) _mgr.ShowTerminal(sel); };
            HideBtn.Click += (s, e) => { var sel = Selected(); if (sel != null) _mgr.HideTerminal(sel); };
            KillBtn.Click += (s, e) =>
            {
                var sel = Selected();
                if (sel != null)
                {
                    var result = MessageBox.Show(
                        "Kill '" + sel.Title + "' and every command running in it?",
                        "TerminalGroupie", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                    {
                        _mgr.KillTerminal(sel);
                    }
                }
            };

            Refresh();
        }

        private void OnTerminalListDoubleClicked(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var sel = Selected();
            if (sel != null) _mgr.ShowTerminal(sel);
        }

        private TermSession? Selected() => TerminalList.SelectedItem as TermSession;

        private void TerminalList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool has = Selected() != null;
            OpenBtn.IsEnabled = has;
            HideBtn.IsEnabled = has;
            KillBtn.IsEnabled = has;
        }

        private void Refresh()
        {
            // Re-assigning the source forces the ListView to rebuild its rows.
            TerminalList.ItemsSource = null;
            TerminalList.ItemsSource = _mgr.Sessions;
        }

        // Closing the window just hides it to the tray; terminals keep running.
        // (During a real Exit we let it close for good.)
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!App.ShuttingDown)
            {
                e.Cancel = true;
                Hide();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _mgr.SessionsChanged -= Refresh;
            base.OnClosed(e);
        }

        private void OnDefaultTerminalClicked()
        {
            // If already WCH or locked by policy, show status instead.
            bool lockedByPolicy = DefaultTerminal.DefaultTerminalLockedByPolicy();
            if (lockedByPolicy)
            {
                MessageBox.Show(
                    "Your default terminal is managed by Group Policy and cannot be changed.\n\n" +
                    "Contact your system administrator to modify the setting.",
                    "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (DefaultTerminal.IsWindowsConsoleHost())
            {
                // Currently WCH — offer restore.
                bool hasOriginals = DefaultTerminal.GetCurrent() != null;
                if (!hasOriginals)
                {
                    MessageBox.Show(
                        "Default terminal is Windows Console Host.\nNo previous setting was saved to restore.",
                        "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    "Restore your previous default terminal?\n\n" +
                    "(You can also do this from the tray icon.)",
                    "TerminalGroupie", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    bool success = DefaultTerminal.RestoreOriginal();
                    RefreshDefaultTerminalButton();

                    if (success)
                        MessageBox.Show(
                            "Your previous default terminal has been restored.",
                            "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Information);
                    else
                        MessageBox.Show(
                            "Could not restore. The original setting may no longer be available.",
                            "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                // Not WCH — show consent dialog to change it.
                if (!DefaultTerminal.HasUserConsented())
                {
                    var dlg = new ConsentDialog();
                    dlg.Owner = this;
                    dlg.ShowDialog();

                    if (!dlg.Allowed) return;
                }

                bool success = DefaultTerminal.SaveAndSetToWindowsConsoleHost();
                RefreshDefaultTerminalButton();

                if (success)
                    MessageBox.Show(
                        "Default terminal changed to Windows Console Host.\n" +
                        "New terminals will appear in TerminalGroupie.",
                        "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void RefreshDefaultTerminalButton()
        {
            if (DefaultTerminalBtn == null) return;

            bool lockedByPolicy = DefaultTerminal.DefaultTerminalLockedByPolicy();
            if (lockedByPolicy)
            {
                DefaultTerminalBtn.Content = "Default Terminal… (locked by policy)";
                DefaultTerminalBtn.IsEnabled = false;
                return;
            }

            string stateText;
            if (DefaultTerminal.IsWindowsConsoleHost())
                stateText = "Default: WCH [ok]";
            else if (!string.IsNullOrEmpty(DefaultTerminal.GetCurrent()?.Item2))
                stateText = "Change to Windows Console Host…";
            else
                stateText = "Default Terminal…";

            DefaultTerminalBtn.Content = stateText;
        }
    }
}
