using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
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

        private readonly TermSession _session;
        private readonly SessionManager _mgr;

        private HwndSource? _source;
        private int _lastFitX, _lastFitY, _lastFitW, _lastFitH;
        private bool _allowClose;
        private bool _focusRoutedOnShow;

        public TerminalWindow(TermSession session, SessionManager mgr)
        {
            InitializeComponent();
            _session = session;
            _mgr = mgr;

            Title = session.Title;
            TitleText.Text = session.Title;
            KindText.Text = session.Kind == ShellKind.PowerShell ? "PowerShell" : "cmd";

            SourceInitialized += OnSourceInitialized;
            Loaded += (s, e) => RefitConsole();
            SizeChanged += (s, e) => RequestRefit();
            // Cover the "first shown" case (a fresh terminal) where the window is displayed
            // but the Activated event alone might not have delivered keyboard focus to the
            // embedded console yet. Fires once, on the first content render.
            ContentRendered += OnFirstContentRendered;
            // Keyboard input reaches the embedded console only while the console is the
            // foreground window on its own (conhost) thread. Re-assert the console as the
            // foreground window whenever we gain activation, so typing always lands in the
            // shell, including after maximize/restore. (FocusConsole skips this while the
            // left mouse button is down; see below.)
            Activated += (s, e) => FocusConsole();

            // A click that activates the window must not move focus to the console until
            // the click is finished, otherwise WPF loses mouse capture and the title-bar
            // button never sees its mouse-up. Refocus the console after mouse-up instead,
            // unless the click landed on a button (minimize / kill do their own thing).
            PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (IsVisible) FocusConsole();
                }));
            };
        }

        /// <summary>Managers call this to close the window after the shell has been killed.</summary>
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

            // Nothing to do if the geometry is unchanged.
            if (x == _lastFitX && y == _lastFitY && w == _lastFitW && h == _lastFitH) return;
            _lastFitX = x; _lastFitY = y; _lastFitW = w; _lastFitH = h;

            Native.FitConsole(console, x, y, w, h);
        }

        /// <summary>
        /// Re-fits the console (and re-routes keyboard focus to it) once WPF has finished
        /// laying out the new size. Deferring to <see cref="DispatcherPriority.ContextIdle"/>
        /// guarantees the layout pass (which updates <c>HostArea.ActualWidth/Height</c>) has
        /// run, so the fit reads fresh geometry instead of the pre-resize values.
        /// </summary>
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

        /// <summary>True if the element (or any visual/logical ancestor) is a button.</summary>
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
        /// Re-asserts the embedded console as the foreground window. Keyboard input reaches
        /// the console only while it is the foreground window on its own (conhost) thread.
        /// We call this in response to the wrapper being activated, so our process holds the
        /// foreground rights at that moment and is allowed to transfer them to the console.
        /// </summary>
        private void FocusConsole()
        {
            IntPtr console = _session.ConsoleHwnd;
            if (console == IntPtr.Zero) return;

            // Stealing focus mid-click kills WPF's mouse capture and swallows the click on
            // the title-bar buttons. The PreviewMouseLeftButtonUp handler refocuses after
            // the click completes.
            if (IsLeftButtonDown()) return;

            Native.SetForeground(console);
            Native.SetFocus(console);
        }

        /// <summary>
        /// Routes keyboard focus to the console once, after the first content render. This
        /// covers the "fresh terminal" case where the window is shown and active but the
        /// Activated event alone didn't leave the console as the foreground window.
        /// </summary>
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

            // If we got here through a minimize (taskbar / Win+D), clear the
            // minimized state so the next restore doesn't pop up iconified.
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            _session.IsHidden = true;
            _mgr.NotifyStateChanged(_session);
        }

        /// <summary>Bring the window back and focus the embedded console.</summary>
        public void ShowFromTray()
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            if (!IsVisible)
            {
                Show();
            }

            Activate();

            RefitConsole();
            FocusConsole();

            _session.IsHidden = false;
            _mgr.NotifyStateChanged(_session);
        }

        // ---------- event handlers ----------

        private void TrayBtn_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void KillBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Close this terminal?\nEvery command running in it will be killed.",
                "TerminalGroupie", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
            {
                _mgr.KillTerminal(_session);
            }
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
                // Low word of wParam is the activation state (the high word is the
                // minimized flag).
                int state = (int)(wParam.ToInt64() & 0xFFFF);
                if (state != WA_INACTIVE)
                {
                    // Keyboard input would now land on our UI thread and be lost, so
                    // re-assert the embedded console as the foreground window. Skip it for
                    // click activations: refocusing mid-click breaks mouse capture and
                    // swallows title-bar button clicks. The mouse-up handler refocuses
                    // the console once the click has finished.
                    if (state != WA_CLICKACTIVE)
                    {
                        FocusConsole();
                    }
                    return IntPtr.Zero;
                }
            }

            if (Native.IsGetMinMaxInfo(msg))
            {
                // Clamp maximize to the monitor's work area. The default maximize overhangs
                // the work area by the frame width (e.g. -8,-8 1936x1096 over a 1920x1042
                // work area), and that left overhang is exactly what clipped the console's
                // first character column when maximized.
                if (Native.ApplyMaximizeClamp(hwnd, lParam))
                {
                    handled = true;
                }
                return IntPtr.Zero;
            }

            if (msg == WM_SIZE && (int)wParam != SIZE_MINIMIZED)
            {
                // Re-fit after WPF has processed the resize (deferred so ActualWidth is fresh).
                Dispatcher.BeginInvoke(new Action(RefitConsole));
                return IntPtr.Zero;
            }

            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
            {
                // Minimize requested (taskbar button, Alt+Space, ...) -> send it to the tray instead.
                Dispatcher.BeginInvoke(new Action(HideToTray));
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        // Win+D / ShowWindow(SW_MINIMIZE) don't go through WM_SYSCOMMAND, so catch the state change too.
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && IsVisible)
            {
                Dispatcher.BeginInvoke(new Action(HideToTray));
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing while the shell is still alive (Alt+F4, taskbar close, ...) sends it
            // to the tray instead of silently killing whatever is running in it.
            if (!_allowClose && _session.IsAlive)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _session.Wrapper = null;
            base.OnClosing(e);
        }
    }
}