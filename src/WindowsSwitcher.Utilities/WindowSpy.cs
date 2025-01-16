using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace WindowsApplicationSwiper
{
    public class WindowSpy : Form
    {
        private Timer updateTimer;
        private ListView infoListView;

        // DLL imports remain the same
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZE = 0x01000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // Add a property to check if foreground window is maximized
        private bool IsForegroundWindowMaximized
        {
            get
            {
                var foregroundWindow = GetForegroundWindow();
                if (foregroundWindow != IntPtr.Zero)
                {
                    int style = GetWindowLong(foregroundWindow, GWL_STYLE);
                    return (style & WS_MAXIMIZE) != 0;
                }
                return false;
            }
        }

        public WindowSpy()
        {
            InitializeForm();
            InitializeTimer();
        }

        private void InitializeForm()
        {
            // Form settings
            this.Text = "Windows Spy Tool";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(800, 600);
            this.MinimumSize = new Size(400, 300);
            this.BackColor = Color.White;
            this.Font = new Font("Segoe UI Variable", 9F);

            // Enable double buffering
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.UserPaint, true);

            // ListView setup
            infoListView = new ListView
            {
                View = View.Details,
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                GridLines = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White
            };

            infoListView.Columns.Add("Property", 200);
            infoListView.Columns.Add("Value", 250);
            this.Controls.Add(infoListView);

            // Handle resize
            this.Resize += WindowSpy_Resize;
            
            // Do initial column sizing
            WindowSpy_Resize(this, EventArgs.Empty);
        }

        private void InitializeTimer()
        {
            // Create and configure timer
            updateTimer = new Timer();
            updateTimer.Interval = 100;
            updateTimer.Tick += UpdateTimer_Tick;
            
            // Start timer and do initial update
            updateTimer.Start();
            UpdateInformation();
        }
        
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Ensure timer is running when form is shown
            if (updateTimer != null && !updateTimer.Enabled)
            {
                updateTimer.Start();
            }
        }

        private void WindowSpy_Resize(object sender, EventArgs e)
        {
            if (infoListView.Columns.Count >= 2)
            {
                int totalWidth = infoListView.ClientSize.Width;
                infoListView.Columns[0].Width = (int)(totalWidth * 0.4);
                infoListView.Columns[1].Width = (int)(totalWidth * 0.6);
            }
            this.Refresh();
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            UpdateInformation();
        }

        private void UpdateInformation()
        {
            infoListView.BeginUpdate();
            infoListView.Items.Clear();

            AddSeparator("Cursor Information");
            GetCursorPos(out POINT cursorPos);
            var currentScreen = Screen.FromPoint(new Point(cursorPos.X, cursorPos.Y));
            
            AddInfoRow("Global Cursor X", cursorPos.X.ToString());
            AddInfoRow("Global Cursor Y", cursorPos.Y.ToString());
            AddInfoRow("Relative Cursor X", (cursorPos.X - currentScreen.Bounds.X).ToString());
            AddInfoRow("Relative Cursor Y", (cursorPos.Y - currentScreen.Bounds.Y).ToString());

            AddSeparator("Current Screen Information");
            AddInfoRow("Screen Name", currentScreen.DeviceName);
            AddInfoRow("Is Primary", currentScreen.Primary.ToString());
            AddInfoRow("Bounds", $"{currentScreen.Bounds.Width}x{currentScreen.Bounds.Height} at ({currentScreen.Bounds.X},{currentScreen.Bounds.Y})");
            AddInfoRow("Working Area", $"{currentScreen.WorkingArea.Width}x{currentScreen.WorkingArea.Height}");

            AddSeparator("Window Information");
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
            {
                StringBuilder title = new StringBuilder(256);
                GetWindowText(foregroundWindow, title, title.Capacity);
                GetWindowRect(foregroundWindow, out RECT rect);

                AddInfoRow("Foreground Window", title.ToString());
                AddInfoRow("Is Foreground Window Maximized", IsForegroundWindowMaximized.ToString());
                AddInfoRow("Window Style", GetWindowLong(foregroundWindow, GWL_STYLE).ToString("X8"));
                AddInfoRow("Position", $"Left: {rect.Left}, Top: {rect.Top}, Right: {rect.Right}, Bottom: {rect.Bottom}");
                AddInfoRow("Size", $"Width: {rect.Right - rect.Left}, Height: {rect.Bottom - rect.Top}");
                AddInfoRow("Screen", Screen.FromHandle(foregroundWindow).DeviceName);
            }

            AddSeparator("All Screens");
            foreach (var screen in Screen.AllScreens)
            {
                AddInfoRow(screen.DeviceName, 
                    $"Bounds: {screen.Bounds.Width}x{screen.Bounds.Height} at ({screen.Bounds.X},{screen.Bounds.Y})");
            }

            infoListView.EndUpdate();
        }

        private void AddSeparator(string text)
        {
            var item = new ListViewItem(text);
            item.SubItems.Add("");
            item.BackColor = Color.WhiteSmoke;
            item.ForeColor = Color.Black;
            infoListView.Items.Add(item);
        }

        private void AddInfoRow(string property, string value)
        {
            var item = new ListViewItem(property);
            item.SubItems.Add(value);
            infoListView.Items.Add(item);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (updateTimer != null)
            {
                updateTimer.Stop();
                updateTimer.Dispose();
            }
            base.OnFormClosing(e);
        }
    }
}