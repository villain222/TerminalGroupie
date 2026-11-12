using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TerminalGroupie
{
    /// <summary>
    /// Win32 interop: locate the console window of a freshly launched shell,
    /// re-parent it into our wrapper window, resize it to fit, and
    /// show / hide / raise / focus it.
    /// </summary>
    internal static class Native
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        private const int WS_CHILD = 0x40000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        private const int WS_EX_WINDOWEDGE = 0x00000100;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_APPWINDOW = 0x00040000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        // ---------- P/Invoke ----------

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic")]
        private static extern bool IsIconicNative(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // NOTE: .NET resolves P/Invokes by exact name, then name+"A"/"W".
        // Suffixes like "Native" never resolve, so every mangled API name here
        // must use an explicit EntryPoint (verified against this machine's exports).
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameNative(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWnd, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetFocus")]
        private static extern IntPtr SetFocusNative(IntPtr hWnd);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint GetCurrentThreadIdNative();

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "AttachThreadInput")]
        private static extern bool AttachThreadInputNative(uint idAttach, uint idDetach, bool fAttach);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // ---------- public API ----------

        /// <summary>Snapshot of all currently visible top-level window handles.</summary>
        public static HashSet<IntPtr> TopLevelWindows()
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    set.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return set;
        }

        /// <summary>
        /// Top-level console windows (Win32 class "ConsoleWindowClass") that were not
        /// present in the <paramref name="before"/> snapshot. Windows owned by
        /// <paramref name="preferredPid"/> come first, so back-to-back terminal
        /// creations claim their own console instead of each other's.
        /// </summary>
        public static List<IntPtr> NewConsoleWindows(HashSet<IntPtr> before, uint preferredPid)
        {
            var preferred = new List<IntPtr>();
            var others = new List<IntPtr>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd) && !before.Contains(hWnd))
                {
                    var sb = new StringBuilder(256);
                    GetClassNameNative(hWnd, sb, sb.Capacity);
                    if (sb.ToString() == "ConsoleWindowClass")
                    {
                        uint pid = 0;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == preferredPid) preferred.Add(hWnd);
                        else others.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
            preferred.AddRange(others);
            return preferred;
        }

        public static bool IsMinimized(IntPtr hWnd)
        {
            return IsWindow(hWnd) && IsIconicNative(hWnd);
        }

        public static void Show(IntPtr hWnd, int nCmdShow)
        {
            ShowWindow(hWnd, nCmdShow);
        }

        /// <summary>Restore and force a (possibly hidden or minimized) top-level window to the foreground.</summary>
        public static void BringToFront(IntPtr hWnd)
        {
            ShowWindow(hWnd, SW_RESTORE);
            SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            SetForegroundWindow(hWnd);
        }

        public static void SetFocus(IntPtr hWnd)
        {
            SetFocusNative(hWnd);
        }

        /// <summary>
        /// Makes <paramref name="hWnd"/> the foreground window. The calling process must
        /// currently hold foreground rights (e.g. it owns the foreground window), which is
        /// true whenever we call this in response to our own wrapper being activated. This is
        /// what routes keyboard input to a child window living on another thread/process:
        /// the console only receives keystrokes while it is the foreground window on its own
        /// thread.
        /// </summary>
        public static bool SetForeground(IntPtr hWnd)
        {
            return SetForegroundWindow(hWnd);
        }

        /// <summary>The OS thread id of the calling thread.</summary>
        public static uint CurrentThreadId()
        {
            return GetCurrentThreadIdNative();
        }

        [DllImport("user32.dll", EntryPoint = "GetFocus")]
        private static extern IntPtr GetFocusNative();

        /// <summary>The window with keyboard focus on the calling thread.</summary>
        public static IntPtr GetFocus()
        {
            return GetFocusNative();
        }

        /// <summary>
        /// Read-only diagnostic: report the current keyboard-ownership state so we can tell
        /// apart "the real code path routed the console to foreground" from "the wrapper
        /// still owns the foreground". Deliberately changes NOTHING (no AttachThreadInput,
        /// no SetForegroundWindow, no SetFocus) so the diag measures the real app's behavior
        /// rather than being helped by the probe itself.
        /// </summary>
        public static string FocusProbe(IntPtr console)
        {
            if (console == IntPtr.Zero) return "console=0";
            uint conTid = ThreadOf(console);
            if (conTid == 0) return "conTid=0";
            uint uiTid = CurrentThreadId();

            IntPtr fg = GetForegroundWindow();
            IntPtr focus = GetFocusNative();
            bool consoleIsFg = fg == console;
            bool sameThread = fg != IntPtr.Zero && GetWindowThreadProcessId(fg, out uint pid) == conTid;

            return "uiTid=" + uiTid + " conTid=" + conTid
                + " fg=0x" + fg.ToString("X")
                + " focus=0x" + focus.ToString("X")
                + (consoleIsFg ? " [CONSOLE-FG]" : " [WRAPPER-FG]")
                + (sameThread ? " [SAME-THREAD]" : " [CROSS-THREAD]");
        }

        /// <summary>
        /// Attaches (or detaches) the calling thread's input queue to the thread that
        /// owns <paramref name="hWnd"/>. This is what lets keyboard input cross from our
        /// UI thread (the owner of the foreground wrapper window) to a child console that
        /// lives on the conhost thread. Returns true when the two queues are (or were
        /// already) attached / detached.
        /// </summary>
        public static bool AttachInput(IntPtr hWnd, bool attach)
        {
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            uint conTid = GetWindowThreadProcessId(hWnd, out uint pid);
            if (conTid == 0)
            {
                return false;
            }

            return AttachInputByThread(conTid, attach);
        }

        /// <summary>
        /// Attaches (or detaches) the calling thread's input queue to the given thread.
        /// <paramref name="attach"/> = false detaches. Same-thread is a no-op that
        /// reports success. This is the primitive that lets keyboard input cross from our
        /// UI thread (owner of the foreground wrapper window) to the conhost thread that
        /// owns the embedded console.
        /// </summary>
        public static bool AttachInputByThread(uint otherThreadId, bool attach)
        {
            if (otherThreadId == 0)
            {
                return false;
            }

            uint uiTid = GetCurrentThreadIdNative();
            if (uiTid == otherThreadId)
            {
                return true; // same thread, nothing to attach
            }

            return AttachThreadInputNative(uiTid, otherThreadId, attach);
        }

        /// <summary>Window rectangle (screen coordinates).</summary>
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        public static Rect GetRectOf(IntPtr hWnd)
        {
            GetWindowRect(hWnd, out RECT r);
            return new Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        }

        /// <summary>Parent window (the desktop window when <paramref name="hWnd"/> is top-level).</summary>
        public static IntPtr GetParentOf(IntPtr hWnd)
        {
            return GetParent(hWnd);
        }

        /// <summary>True when the window carries the WS_CHILD style.</summary>
        public static bool IsChildWindow(IntPtr hWnd)
        {
            return (GetWindowLong(hWnd, GWL_STYLE) & WS_CHILD) != 0;
        }

        public static bool IsVisible(IntPtr hWnd)
        {
            return IsWindowVisible(hWnd);
        }

        /// <summary>
        /// Sends a WM_SYSCOMMAND / SC_MINIMIZE message to the window — the same message
        /// the taskbar's minimize button sends. Used to exercise the minimize interception.
        /// </summary>
        public static void PostMinimize(IntPtr hWnd)
        {
            PostMessage(hWnd, WM_SYSCOMMAND, new IntPtr(SC_MINIMIZE), IntPtr.Zero);
        }

        /// <summary>
        /// Re-parents <paramref name="child"/> (a top-level console window) into
        /// <paramref name="parent"/> and turns it into a proper child window
        /// (SetParent does not set WS_CHILD on its own — we add it explicitly,
        /// then strip the console's caption/border styles so it fills its rectangle).
        /// Returns false if the re-parent did not take effect.
        /// </summary>
        public static bool SetParentTo(IntPtr child, IntPtr parent)
        {
            if (child == IntPtr.Zero || parent == IntPtr.Zero)
            {
                return false;
            }

            // SetParent returns the previous parent (the desktop window for a
            // top-level window) on success, NULL on failure.
            IntPtr oldParent = SetParent(child, parent);
            if (oldParent == IntPtr.Zero)
            {
                return false;
            }

            // SetParent does not add WS_CHILD by itself — do it, and remove the
            // console's own caption/frame so no non-client area remains.
            int style = GetWindowLong(child, GWL_STYLE);
            style |= WS_CHILD;
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            SetWindowLong(child, GWL_STYLE, style);

            // Drop the 3D edges and the "show in taskbar" hint so it behaves like plain content.
            int exstyle = GetWindowLong(child, GWL_EXSTYLE);
            exstyle &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_APPWINDOW);
            SetWindowLong(child, GWL_EXSTYLE, exstyle);

            return GetParent(child) == parent;
        }

        /// <summary>
        /// Sizes and positions the console window to fill the pixel rectangle
        /// (x, y, width, height) relative to its parent's client area.
        /// Resizing the console HWND makes conhost re-flow its screen buffer
        /// (the same path as dragging a console's edge) — no console handle,
        /// no AttachConsole/DetachConsole, which are unavailable here.
        /// </summary>
        public static void FitConsole(IntPtr consoleHwnd, int x, int y, int widthPx, int heightPx)
        {
            if (consoleHwnd == IntPtr.Zero || widthPx < 40 || heightPx < 20)
            {
                return;
            }

            if (!IsWindow(consoleHwnd))
            {
                return; // shell already gone
            }

            MoveWindow(consoleHwnd, x, y, widthPx, heightPx, true);
        }

        // ---------- maximize clamping (WM_GETMINMAXINFO) ----------

        private const int WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)]
        private struct MAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        public static bool IsGetMinMaxInfo(int msg)
        {
            return msg == WM_GETMINMAXINFO;
        }

        /// <summary>
        /// Fills the WM_GETMINMAXINFO structure (<paramref name="mmiPtr"/>) so a maximized
        /// window exactly fills the monitor work area — no 8px overhang on the left/top,
        /// which is what clips the console's first character column when maximized.
        /// <paramref name="hwnd"/> identifies the monitor (and is used for DPI-correct
        /// work area). Returns true when the clamp was applied.
        /// </summary>
        public static bool ApplyMaximizeClamp(IntPtr hwnd, IntPtr mmiPtr)
        {
            if (mmiPtr == IntPtr.Zero)
            {
                return false;
            }

            if (!TryGetWorkArea(hwnd, out Rect wa))
            {
                return false;
            }

            var mmi = Marshal.PtrToStructure<MAXINFO>(mmiPtr);
            mmi.ptMaxPosition = new POINT { X = wa.Left, Y = wa.Top };
            mmi.ptMaxSize = new POINT { X = wa.Width, Y = wa.Height };
            Marshal.StructureToPtr(mmi, mmiPtr, true);
            return true;
        }

        // ---------- diagnostics (used by --diag) ----------

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_RETURN = 0x0D;

        private const int SC_MAXIMIZE = 0xF030;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public int rcCaretLeft;
            public int rcCaretTop;
            public int rcCaretRight;
            public int rcCaretBottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, out GUITHREADINFO pinfo);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        /// <summary>True when the handle is a live window.</summary>
        public static bool WindowExists(IntPtr hWnd)
        {
            return IsWindow(hWnd);
        }

        /// <summary>True when the window is enabled (not greyed out).</summary>
        public static bool IsEnabled(IntPtr hWnd)
        {
            return IsWindowEnabled(hWnd);
        }

        /// <summary>Window style (GWL_STYLE) in hex-friendly decimal.</summary>
        public static int GetStyle(IntPtr hWnd)
        {
            return GetWindowLong(hWnd, GWL_STYLE);
        }

        /// <summary>Window extended style.</summary>
        public static int GetExStyle(IntPtr hWnd)
        {
            return GetWindowLong(hWnd, GWL_EXSTYLE);
        }

        /// <summary>Process id that owns <paramref name="hWnd"/>.</summary>
        public static uint PidOf(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            return pid;
        }

        /// <summary>Thread id that owns <paramref name="hWnd"/>.</summary>
        public static uint ThreadOf(IntPtr hWnd)
        {
            return GetWindowThreadProcessId(hWnd, out uint pid);
        }

        /// <summary>The current foreground window.</summary>
        public static IntPtr GetForeground()
        {
            return GetForegroundWindow();
        }

        /// <summary>
        /// Keyboard focus window of the foreground thread (via GetGUIThreadInfo).
        /// Returns false when there is no foreground window or thread.
        /// </summary>
        public static bool TryGetFocusOfForegroundThread(out IntPtr focus)
        {
            focus = IntPtr.Zero;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }

            uint tid = GetWindowThreadProcessId(fg, out uint pid);
            if (tid == 0)
            {
                return false;
            }

            var gi = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(tid, out gi))
            {
                return false;
            }

            focus = gi.hwndFocus;
            return focus != IntPtr.Zero;
        }

        /// <summary>Monitor work area (screen pixels) for the monitor hosting <paramref name="hWnd"/>.</summary>
        public static bool TryGetWorkArea(IntPtr hwnd, out Rect workArea)
        {
            workArea = default;
            const uint MONITOR_DEFAULTTONEAREST = 2;
            const uint MONITOR_DEFAULTTOPRIMARY = 0;

            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero)
            {
                mon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            }

            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi))
            {
                return false;
            }

            workArea = new Rect
            {
                Left = mi.rcWork.Left,
                Top = mi.rcWork.Top,
                Right = mi.rcWork.Right,
                Bottom = mi.rcWork.Bottom
            };
            return true;
        }

        /// <summary>Client area of <paramref name="hWnd"/> in screen coordinates.</summary>
        public static bool TryGetClientAreaScreenRect(IntPtr hWnd, out Rect r)
        {
            r = default;
            if (!GetClientRect(hWnd, out RECT cr))
            {
                return false;
            }

            var tl = new POINT { X = cr.Left, Y = cr.Top };
            var br = new POINT { X = cr.Right, Y = cr.Bottom };
            if (!ClientToScreen(hWnd, ref tl) || !ClientToScreen(hWnd, ref br))
            {
                return false;
            }

            r = new Rect { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y };
            return true;
        }

        /// <summary>Sends a single left mouse click at a screen pixel point.</summary>
        public static void ClickAt(int screenX, int screenY)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int cx = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int cy = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (cx <= 0 || cy <= 0)
            {
                return;
            }

            int ax = (int)((screenX - vx) * 65535.0 / cx);
            int ay = (int)((screenY - vy) * 65535.0 / cy);

            SendOne(MakeMouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE));
            SendOne(MakeMouse(0, 0, MOUSEEVENTF_LEFTDOWN));
            SendOne(MakeMouse(0, 0, MOUSEEVENTF_LEFTUP));
        }

        /// <summary>Types <paramref name="text"/> (any unicode) and then presses Enter.</summary>
        public static void TypeUnicode(string text)
        {
            foreach (char c in text)
            {
                SendOne(MakeKey(c, false));
                SendOne(MakeKey(c, true));
            }

            var enter = new INPUT { type = INPUT_KEYBOARD };
            enter.u.ki.wVk = VK_RETURN;
            SendOne(enter);
        }

        private static INPUT MakeMouse(int ax, int ay, uint flags)
        {
            var inp = new INPUT { type = 0 };
            inp.u.mi.dx = ax;
            inp.u.mi.dy = ay;
            inp.u.mi.dwFlags = flags;
            return inp;
        }

        private static INPUT MakeKey(char c, bool up)
        {
            var inp = new INPUT { type = INPUT_KEYBOARD };
            inp.u.ki.wScan = c;
            inp.u.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
            return inp;
        }

        private static void SendOne(INPUT input)
        {
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        /// <summary>Requests a maximize on <paramref name="hWnd"/> (WM_SYSCOMMAND SC_MAXIMIZE).</summary>
        public static void PostMaximize(IntPtr hWnd)
        {
            PostMessage(hWnd, WM_SYSCOMMAND, new IntPtr(SC_MAXIMIZE), IntPtr.Zero);
        }
    }
}
