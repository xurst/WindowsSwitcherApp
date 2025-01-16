using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsApplicationSwiper
{
    public partial class WindowSwitcher : Form
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZE = 0x01000000;
        private const int MOD_ALT = 0x0001;
        private const int WM_HOTKEY = 0x0312;
        private const int VK_J = 0x4A;
        private const int VK_K = 0x4B;
        private const int HOTKEY_ID_PREV = 1;
        private const int HOTKEY_ID_NEXT = 2;

        private Dictionary<Screen, List<IntPtr>> windowsByScreen;
        private Dictionary<Screen, int> currentPositionByScreen;
        private NotifyIcon notifyIcon;
        private System.Windows.Forms.Timer windowCheckTimer;
        private WindowSpy windowSpyForm;

        public WindowSwitcher()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // Initialize tracking
            windowsByScreen = new Dictionary<Screen, List<IntPtr>>();
            currentPositionByScreen = new Dictionary<Screen, int>();

            // Set up the invisible form
            this.ShowInTaskbar = false;
            this.Opacity = 0;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(1, 1);
            this.WindowState = FormWindowState.Minimized;

            // Set up window check timer
            windowCheckTimer = new System.Windows.Forms.Timer();
            windowCheckTimer.Interval = 100;
            windowCheckTimer.Tick += WindowCheckTimer_Tick;
            windowCheckTimer.Start();

            // Create notify icon with context menu
            notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Window Switcher (Alt+J/K)",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            
            // Add Window Spy menu item
            var spyMenuItem = new ToolStripMenuItem("Window Spy");
            spyMenuItem.Click += SpyMenuItem_Click;
            contextMenu.Items.Add(spyMenuItem);
            
            // Add separator
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // Add Exit menu item
            var exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += (s, e) => Application.Exit();
            contextMenu.Items.Add(exitMenuItem);
            
            notifyIcon.ContextMenuStrip = contextMenu;

            InitializeScreens();
            RegisterHotKeys();
            RefreshWindowLists();
        }
        
        private void SpyMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                // If window spy is already open, just focus it
                if (windowSpyForm != null && !windowSpyForm.IsDisposed)
                {
                    windowSpyForm.Focus();
                    return;
                }

                // Create and show a new window spy form
                windowSpyForm = new WindowSpy();
                windowSpyForm.Show();

                // Handle when WindowSpy is closed
                windowSpyForm.FormClosed += (s, args) => windowSpyForm = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Window Spy: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void WindowCheckTimer_Tick(object sender, EventArgs e)
        {
            UpdateCurrentPosition();
            CheckForNewMaximizedWindows();
        }

        private void UpdateCurrentPosition()
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return;

            // Find which screen the window is on
            RECT rect;
            if (!GetWindowRect(foregroundWindow, out rect)) return;
            var center = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            var screen = Screen.FromPoint(center);

            // Update position if window is in our list
            var windows = windowsByScreen[screen];
            var position = windows.IndexOf(foregroundWindow);
            if (position != -1)
            {
                currentPositionByScreen[screen] = position;
            }
        }

        private void CheckForNewMaximizedWindows()
        {
            EnumWindows(new EnumWindowsProc(delegate (IntPtr hWnd, IntPtr lParam)
            {
                if (IsWindowVisible(hWnd) && IsWindowMaximized(hWnd))
                {
                    RECT rect;
                    if (GetWindowRect(hWnd, out rect))
                    {
                        var center = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                        var screen = Screen.FromPoint(center);
                        var windows = windowsByScreen[screen];

                        // Add window if it's not already in our list
                        if (!windows.Contains(hWnd))
                        {
                            windows.Add(hWnd);
                        }
                    }
                }
                return true;
            }), IntPtr.Zero);

            // Remove any windows that are no longer maximized
            foreach (var screen in Screen.AllScreens)
            {
                var windows = windowsByScreen[screen];
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    var window = windows[i];
                    if (!IsWindowVisible(window) || !IsWindowMaximized(window))
                    {
                        windows.RemoveAt(i);
                        if (i <= currentPositionByScreen[screen])
                        {
                            currentPositionByScreen[screen]--;
                        }
                    }
                }
            }
        }

        private void InitializeScreens()
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                windowsByScreen[screen] = new List<IntPtr>();
                currentPositionByScreen[screen] = -1;
            }
        }

        private void RefreshWindowLists()
        {
            // Clear existing lists
            foreach (var screen in Screen.AllScreens)
            {
                windowsByScreen[screen].Clear();
            }

            // Get current foreground window and add it first
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero && IsWindowVisible(foregroundWindow) && IsWindowMaximized(foregroundWindow))
            {
                RECT rect;
                if (GetWindowRect(foregroundWindow, out rect))
                {
                    var center = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                    var screen = Screen.FromPoint(center);
                    windowsByScreen[screen].Add(foregroundWindow);
                    currentPositionByScreen[screen] = 0;
                }
            }

            // Add all other maximized windows
            CheckForNewMaximizedWindows();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            this.Hide();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                UpdateCurrentPosition(); // Update position before switching
                
                int id = m.WParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_ID_PREV:
                        SwitchWindow(false);
                        break;
                    case HOTKEY_ID_NEXT:
                        SwitchWindow(true);
                        break;
                }
            }
            base.WndProc(ref m);
        }
        
        private void SwitchWindow(bool forward)
        {
            // First check the debounce
            if (!WindowAnimator.CanStartAnimation())
                return;

            // Get current screen
            GetCursorPos(out POINT cursorPos);
            var currentScreen = Screen.FromPoint(new Point(cursorPos.X, cursorPos.Y));

            var windows = windowsByScreen[currentScreen];
            var currentPosition = currentPositionByScreen[currentScreen];

            // Check if we have enough windows to switch
            if (windows.Count <= 1) return;

            // Get current window and validate position
            var currentWindow = GetForegroundWindow();
            int actualPosition = windows.IndexOf(currentWindow);
            if (actualPosition != -1)
            {
                currentPosition = actualPosition;
                currentPositionByScreen[currentScreen] = currentPosition;
            }

            int newPosition;
            if (forward)
            {
                if (currentPosition >= windows.Count - 1) return;
                newPosition = currentPosition + 1;
            }
            else
            {
                if (currentPosition <= 0) return;
                newPosition = currentPosition - 1;
            }

            // Get target window
            var targetWindow = windows[newPosition];
            if (IsWindowVisible(targetWindow) && IsWindowMaximized(targetWindow))
            {
                // Create and start the animation
                var animator = new WindowAnimator(currentWindow, targetWindow, currentScreen, forward);
                animator.StartAnimation();

                // Switch to new window
                SetForegroundWindow(targetWindow);
                currentPositionByScreen[currentScreen] = newPosition;
            }
            else
            {
                // Remove invalid window and try again
                windows.RemoveAt(newPosition);
                if (forward && newPosition > currentPosition) 
                    currentPositionByScreen[currentScreen]--;
                SwitchWindow(forward);
            }
        }

        private bool IsWindowMaximized(IntPtr hWnd)
        {
            int style = GetWindowLong(hWnd, GWL_STYLE);
            return (style & WS_MAXIMIZE) != 0;
        }

        private void RegisterHotKeys()
        {
            bool success = true;
            success &= RegisterHotKey(this.Handle, HOTKEY_ID_PREV, MOD_ALT, VK_J);
            success &= RegisterHotKey(this.Handle, HOTKEY_ID_NEXT, MOD_ALT, VK_K);

            if (!success)
            {
                MessageBox.Show("Failed to register hotkeys (Alt+J/K).",
                    "Hotkey Registration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            windowCheckTimer.Stop();
            windowCheckTimer.Dispose();
            
            UnregisterHotKey(this.Handle, HOTKEY_ID_PREV);
            UnregisterHotKey(this.Handle, HOTKEY_ID_NEXT);
            
            // Clean up WindowSpy if it's open
            if (windowSpyForm != null && !windowSpyForm.IsDisposed)
            {
                windowSpyForm.Close();
                windowSpyForm.Dispose();
            }
            
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
            }
            
            base.OnFormClosing(e);
        }
    }
}