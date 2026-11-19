using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace BambuHistoryScreenshot
{
    public static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;
        public const uint WM_GETOBJECT = 0x003D;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x01000;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_SHIFT = 0x10;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static void EnablePerMonitorDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                // PROCESS_PER_MONITOR_DPI_AWARE = 2
                SetProcessDpiAwareness(2);
                return;
            }
            catch
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        public static bool IsEscapePressed()
        {
            return (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
        }

        public static string GetWindowTitle(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetWindowClassName(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            RECT r;
            if (GetWindowRect(hWnd, out r))
            {
                return r.ToRectangle();
            }
            return Rectangle.Empty;
        }

        public static IntPtr FindBambuStudioMainWindow(out string windowTitle)
        {
            windowTitle = string.Empty;
            HashSet<uint> bambuPids = new HashSet<uint>();

            try
            {
                Process[] procs = Process.GetProcesses();
                for (int i = 0; i < procs.Length; i++)
                {
                    Process p = procs[i];
                    try
                    {
                        string name = (p.ProcessName ?? string.Empty).ToLowerInvariant();
                        if (name == "bambu-studio" || name == "bambustudio" || name.Contains("bambu-studio"))
                        {
                            bambuPids.Add((uint)p.Id);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
            }

            IntPtr bestHwnd = IntPtr.Zero;
            int bestArea = 0;
            string bestTitle = string.Empty;

            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string title = GetWindowTitle(hWnd);

                bool isBambu = bambuPids.Contains(pid);
                if (!isBambu && !string.IsNullOrEmpty(title))
                {
                    if (title.IndexOf("Bambu Studio", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isBambu = true;
                    }
                }

                if (!isBambu) return true;

                Rectangle rect = GetWindowBounds(hWnd);
                // Exclude tiny helper windows or toolbars; allow iconic (minimized) main windows
                int area = IsIconic(hWnd) ? 500000 : (rect.Width * rect.Height);
                if (!IsIconic(hWnd) && (rect.Width < 300 || rect.Height < 200))
                {
                    return true;
                }

                // Prefer the largest visible Bambu Studio window (and prefer ones with non-empty title)
                int score = area + (string.IsNullOrEmpty(title) ? 0 : 10000000);
                if (score > bestArea)
                {
                    bestArea = score;
                    bestHwnd = hWnd;
                    bestTitle = title;
                }

                return true;
            }, IntPtr.Zero);

            windowTitle = bestTitle;
            return bestHwnd;
        }

        public static List<IntPtr> FindWebViewRenderWindows(IntPtr parentHwnd)
        {
            List<IntPtr> result = new List<IntPtr>();
            if (parentHwnd == IntPtr.Zero) return result;

            EnumChildWindows(parentHwnd, delegate(IntPtr childHwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(childHwnd)) return true;
                string cls = GetWindowClassName(childHwnd);
                if (cls == "Chrome_RenderWidgetHostHWND")
                {
                    Rectangle r = GetWindowBounds(childHwnd);
                    if (r.Width >= 150 && r.Height >= 120)
                    {
                        result.Add(childHwnd);
                    }
                }
                return true;
            }, IntPtr.Zero);

            // Sort largest first so the main content WebView (home.html / print history) is scanned before sidebars
            result.Sort(delegate(IntPtr a, IntPtr b)
            {
                Rectangle ra = GetWindowBounds(a);
                Rectangle rb = GetWindowBounds(b);
                return (rb.Width * rb.Height).CompareTo(ra.Width * ra.Height);
            });

            return result;
        }

        public static void WakeUpWebViewAccessibility(IntPtr chromeRenderHwnd)
        {
            if (chromeRenderHwnd == IntPtr.Zero) return;
            IntPtr result;
            // OBJID_CLIENT = -4 triggers Chromium / WebView2 to construct its full UIA accessibility tree
            SendMessageTimeout(chromeRenderHwnd, WM_GETOBJECT, IntPtr.Zero, new IntPtr(-4), SMTO_ABORTIFHUNG, 500, out result);
        }

        public static bool RestoreAndFocusWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;

            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(hWnd, SW_SHOW);
            }

            IntPtr fg = GetForegroundWindow();
            if (fg == hWnd) return true;

            uint fgPid;
            uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
            uint curThread = GetCurrentThreadId();
            bool attached = false;

            try
            {
                if (fgThread != 0 && fgThread != curThread)
                {
                    attached = AttachThreadInput(curThread, fgThread, true);
                }
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(curThread, fgThread, false);
                }
            }

            return GetForegroundWindow() == hWnd;
        }

        public static void ScrollVerticalAt(Point screenPt, int wheelNotches)
        {
            SetCursorPos(screenPt.X, screenPt.Y);
            System.Threading.Thread.Sleep(35);
            // Positive = up, Negative = down; 120 per notch
            int delta = wheelNotches * 120;
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, UIntPtr.Zero);
        }

        public static void ScrollHorizontalAt(Point screenPt, int wheelNotches)
        {
            SetCursorPos(screenPt.X, screenPt.Y);
            System.Threading.Thread.Sleep(35);
            // Positive = right, Negative = left
            int delta = wheelNotches * 120;
            mouse_event(MOUSEEVENTF_HWHEEL, 0, 0, delta, UIntPtr.Zero);
            System.Threading.Thread.Sleep(40);
            // Also send Shift+Wheel as fallback because some Chromium/WebView2 horizontal flex containers respond to Shift+Wheel
            keybd_event((byte)VK_SHIFT, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -delta, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            keybd_event((byte)VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
