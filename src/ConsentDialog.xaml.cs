using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalGroupie
{
    /// <summary>
    /// One-time consent dialog: asks the user to allow us to change their default terminal
    /// from Windows Terminal → Windows Console Host (so our wrapper can grab new consoles).
    /// </summary>
    public partial class ConsentDialog : Window
    {
        private bool _canRestore;

        public bool Allowed { get; private set; } = false;

        public ConsentDialog()
        {
            InitializeComponent();

            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            // Load our tray icon as the window icon.
            try
            {
                Icon = TrayIcons.ToImageSource(Elevation.IsElevated);
            }
            catch
            {
                Icon = null;
            }

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Update the button text to reflect whether a restore is available.
            _canRestore = !string.IsNullOrEmpty(DefaultTerminal.GetCurrent()?.Item2);
            if (!_canRestore)
            {
                ((Button)FindName("AllowBtn")).Content = "Apply setting";
            }

            AllowBtn.Focus();
        }

        private void OnAllowed(object sender, RoutedEventArgs e)
        {
            Allowed = true;
            DefaultTerminal.MarkConsented();

            // Apply the change.
            if (DefaultTerminal.SaveAndSetToWindowsConsoleHost())
            {
                MessageBox.Show(
                    "Your default terminal has been changed to Windows Console Host.\n" +
                    "New terminals will appear in TerminalGroupie automatically." +
                    (_canRestore ? "\n\nYou can restore your previous setting from the tray icon." : ""),
                    "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "Could not change the default terminal. You may need to run as administrator.",
                    "TerminalGroupie", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Close();
        }

        private void OnDenied(object sender, RoutedEventArgs e)
        {
            Allowed = false;
            Close();
        }
    }
}
