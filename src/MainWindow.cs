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
    }
}
