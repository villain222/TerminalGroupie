using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalGroupie
{
    /// <summary>
    /// Wrapper window for a single terminal. It provides a custom title bar with a
    /// "Minimize to tray" button and a "Close (kill)" button, and hosts the real
    /// console window, which is re-parented into it.
    /// Every form of minimization (this button, the taskbar button, Win+D, Alt+Space)
    /// is redirected to the system tray; the shell keeps running while hidden.
    /// </summary>
    public partial class TerminalWindow : Window
    {
        private const int WM_SIZE = 0x0005;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const int SIZE_MINIMIZED = 2;
        private const int WM_ACTIVATE = 0x0006;
        private const int WA_INACTIVE = 0;
        private const int WA_ACTIVE = 1;
        private const int WA_CLICKACTIVE = 2;
        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>Sets a window as the foreground window. Returns true on success.</summary>
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly TermSession _session;
        private readonly SessionManager _mgr;

        private HwndSource? _source;
        private int _lastFitX, _lastFitY, _lastFitW, _lastFitH;
        private bool _allowClose;
        private bool _focusRoutedOnShow;

        // Deferred focus-restore state. A click on the title bar (or any activation)
        // hands keyboard focus to the WPF window, stealing it from the embedded
        // console; these drive the retry loop that hands it back once the click settles.
        private readonly object _focusGate = new object();
        private Timer? _focusTimer;
        private int _focusAttempt;

        public TerminalWindow(TermSession session, SessionManager mgr)
        {
            InitializeComponent();
            _session = session;
            _mgr = mgr;

            Title = session.Title;
            TitleText.Text = session.Title;
            KindText.Text = session.Kind == ShellKind.PowerShell ? "PowerShell" : "cmd";

            Icon = TrayIcons.ToImageSource(Elevation.IsElevated);

            SourceInitialized += OnSourceInitialized;
            Loaded += (s, e) => RefitConsole();
            SizeChanged += (s, e) => RequestRefit();
            ContentRendered += OnFirstContentRendered;
            // Keyboard input reaches the embedded console only while it's foreground.
            Activated += (s, e) => FocusConsole();

            // A click that activates must not steal focus until the click is finished.
            PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        }

        private void OnPreviewMouseLeftButtonUp(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
            ScheduleFocusRestore();
        }

        public void RequestClose()
        {
            _allowClose = true;
            Close();
        }

        /// <summary>Sizes and moves the embedded console so it fills the host area.</summary>
        public void RefitConsole()
        {
            if (_source == null) return;
            IntPtr console = _session.ConsoleHwnd;
            if (console == IntPtr.Zero) return;
            if (HostArea.ActualWidth <= 0 || HostArea.ActualHeight <= 0) return;

            double scale = Math.Abs(_source.CompositionTarget.TransformToDevice.M11);
            if (scale <= 0) scale = 1;

            Point? p = HostArea.TranslatePoint(new Point(0, 0), this);
            if (!p.HasValue) return;

            int x = (int)Math.Round(p.Value.X * scale);
            int y = (int)Math.Round(p.Value.Y * scale);
            int w = (int)Math.Round(HostArea.ActualWidth * scale);
            int h = (int)Math.Round(HostArea.ActualHeight * scale);
            if (w < 40 || h < 20) return;

            if (x == _lastFitX && y == _lastFitY && w == _lastFitW && h == _lastFitH) return;
            _lastFitX = x; _lastFitY = y; _lastFitW = w; _lastFitH = h;

            Native.FitConsole(console, x, y, w, h);
        }

        /// <summary>Re-fit + refocus after WPF finishes the layout pass.</summary>
        private void RequestRefit()
        {
            if (_source == null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                RefitConsole();
                FocusConsole();
            }));
        }

        private static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        /// <summary>True if the element or any ancestor is a button.</summary>
        private static bool IsInsideButton(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is ButtonBase) return true;
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        /// <summary>
        /// Schedule a focus-restore to the embedded console. Safe to call from any
        /// trigger (activation, title-bar click, client-area click); the latest call
        /// wins. Retries at increasing intervals until the mouse button is released and
        /// the window is visible, then hands the keyboard to the console and stops.
        /// </summary>
        private void ScheduleFocusRestore()
        {
            lock (_focusGate)
            {
                _focusAttempt = 0;
                var prev = _focusTimer;
                _focusTimer = null;
                prev?.Dispose();
            }
            FocusTick();
        }

        /// <summary>One step of the focus-restore retry chain.</summary>
        private void FocusTick()
        {
            const int MaxTries = 30; // ~6s of backoff as a safety net

            int attempt;
            lock (_focusGate)
            {
                if (_focusAttempt >= MaxTries)
                {
                    StopFocusRestore();
                    return;
                }
                attempt = _focusAttempt++;
            }

            int delay = attempt == 0 ? 1 : Math.Min(8 << attempt, 250);

            var timer = new Timer(_ =>
            {
                // Marshal to the UI thread (IsVisible / DoFocusConsole are WPF-bound).
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    if (!IsVisible || App.ShuttingDown)
                    {
                        StopFocusRestore();
                        return;
                    }

                    if (IsLeftButtonDown())
                    {
                        FocusTick(); // still mid-click / drag — wait for the release
                        return;
                    }

                    DoFocusConsole();
                    StopFocusRestore();
                }));
            }, null, delay, Timeout.Infinite);

            lock (_focusGate)
            {
                var old = _focusTimer;
                _focusTimer = timer;
                old?.Dispose();
            }
        }

        /// <summary>Cancel any in-flight focus-restore chain.</summary>
        private void StopFocusRestore()
        {
            lock (_focusGate)
            {
                _focusAttempt = 0;
                var t = _focusTimer;
                _focusTimer = null;
                t?.Dispose();
            }
        }

        /// <summary>
        /// Actual focus assertion: hand the keyboard to the embedded console. Every path
        /// that calls this leaves the wrapper as the foreground window (it is triggered by
        /// our own activation or by a click on it), so we only need to bridge the input
        /// queue across to the console's conhost thread and set the focus.
        /// </summary>
        private void DoFocusConsole()
        {
            IntPtr console = _session.ConsoleHwnd;
            if (console == IntPtr.Zero) return;
            if (IsLeftButtonDown()) return;

            // The console lives on the conhost thread; attach our UI thread's input
            // queue so SetFocus can cross the thread boundary, then hand it the keyboard.
            bool attached = Native.AttachInput(console, true);
            try
            {
                SetForegroundWindow(console);
                Native.SetFocus(console);
            }
            finally
            {
                if (attached)
                    Native.AttachInput(console, false);
            }
        }

        /// <summary>Public focus assertion used by event handlers.</summary>
        private void FocusConsole()
        {
            if (IsLeftButtonDown()) return;
            DoFocusConsole();
        }

        /// <summary>Route focus once after first content render.</summary>
        private void OnFirstContentRendered(object? sender, EventArgs e)
        {
            if (_focusRoutedOnShow) return;
            _focusRoutedOnShow = true;
            FocusConsole();
        }

        /// <summary>Hide the window to the tray (the shell keeps running).</summary>
        public void HideToTray()
        {
            if (!IsVisible) return;
            Hide();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            _session.IsHidden = true;
            _mgr.NotifyStateChanged(_session);
        }

        /// <summary>Bring the window back and focus the embedded console.</summary>
        public void ShowFromTray()
        {
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();
            Activate();
            RefitConsole();
            FocusConsole();
            _session.IsHidden = false;
            _mgr.NotifyStateChanged(_session);
        }

        // ---------- event handlers ----------

        private void TrayBtn_Click(object sender, RoutedEventArgs e) => HideToTray();

        private void KillBtn_Click(object sender, RoutedEventArgs e)
        {
            // A pending focus-restore (scheduled when this window was activated) would
            // otherwise yank focus to the console behind the confirm dialog below.
            StopFocusRestore();
            var result = MessageBox.Show(this,
                "Close this terminal?\nEvery command running in it will be killed.",
                "TerminalGroupie", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
                _mgr.KillTerminal(_session);
        }

        // ---------- window plumbing ----------

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ACTIVATE)
            {
                int state = (int)(wParam.ToInt64() & 0xFFFF);
                if (state != WA_INACTIVE)
                {
                    // The window just took (or kept) the foreground. WPF sets its own
                    // keyboard focus during this, stealing it from the embedded console;
                    // hand it back after the current input settles.
                    ScheduleFocusRestore();
                }
                return IntPtr.Zero;
            }

            // A click on the custom title bar (the WindowChrome caption) activates the
            // window but never raises WPF's PreviewMouseLeftButtonUp, so restore focus on
            // the non-client mouse-up. HTCLIENT/HTCAPTION cover the whole top strip.
            if (msg == 0x00A2 /* WM_NCLBUTTONUP */)
            {
                int ht = (int)(short)wParam.ToInt64();
                if (ht == 1 /* HTCLIENT */ || ht == 2 /* HTCAPTION */)
                    ScheduleFocusRestore();
                return IntPtr.Zero;
            }

            if (Native.IsGetMinMaxInfo(msg))
            {
                if (Native.ApplyMaximizeClamp(hwnd, lParam))
                    handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SIZE && (int)wParam != SIZE_MINIMIZED)
            {
                Dispatcher.BeginInvoke(new Action(RefitConsole));
                return IntPtr.Zero;
            }

            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
            {
                Dispatcher.BeginInvoke(new Action(HideToTray));
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && IsVisible)
                Dispatcher.BeginInvoke(new Action(HideToTray));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose && _session.IsAlive)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            StopFocusRestore();
            base.OnClosing(e);
        }
    }
}
