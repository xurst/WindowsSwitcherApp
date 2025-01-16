using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D2D = SharpDX.Direct2D1;
using PixelFormat = SharpDX.Direct2D1.PixelFormat;
using AlphaMode = SharpDX.Direct2D1.AlphaMode;
using System.Runtime.InteropServices;
using Matrix3x2 = SharpDX.Mathematics.Interop.RawMatrix3x2;

namespace WindowsApplicationSwiper
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public class WindowAnimator : Form
    {
        // Remove all local constants and use NativeMethods instead
        private void DisableWindowEffects(IntPtr hwnd)
        {
            // Use NativeMethods constants
            var disabled = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref disabled, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_MICA_EFFECT, ref disabled, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref disabled, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BYPASS_COMPOSITOR, ref disabled, sizeof(int));
        }
        
        private int[] originalStyles;  // Store original window styles
        
        private WindowRenderTarget d2dRenderTarget;
        private SharpDX.Direct2D1.Bitmap currentWindowBitmap;
        private SharpDX.Direct2D1.Bitmap nextWindowBitmap;
        private D2D.Factory d2dFactory;
        
        private readonly System.Diagnostics.Stopwatch animationStopwatch;
        private float currentPosition = 0;
        private readonly bool isForward;
        private readonly Rectangle workArea;
        private IntPtr currentWindowHandle;
        private IntPtr nextWindowHandle;
        private bool wasCurrentWindowVisible;
        private bool wasNextWindowVisible;
        private const int ANIMATION_DURATION = 300;
        private const int TARGET_FPS = 144;
        private DateTime lastFrameTime;
        private float lastProgress = 0;
        private bool isDisposed = false;

        private static readonly object animationLock = new object();
        private static DateTime lastAnimationTime = DateTime.MinValue;
        private const double DEBOUNCE_SECONDS = 0.3;

        public static bool CanStartAnimation()
        {
            lock (animationLock)
            {
                var now = DateTime.Now;
                if ((now - lastAnimationTime).TotalSeconds < DEBOUNCE_SECONDS)
                    return false;
                
                lastAnimationTime = now;
                return true;
            }
        }

        public WindowAnimator(IntPtr currentWindow, IntPtr nextWindow, Screen screen, bool forward)
        {
            // Store original styles
            originalStyles = new int[2];
            originalStyles[0] = NativeMethods.GetWindowLong(currentWindow, NativeMethods.GWL_STYLE);
            originalStyles[1] = NativeMethods.GetWindowLong(nextWindow, NativeMethods.GWL_STYLE);
            
            currentWindowHandle = currentWindow;
            nextWindowHandle = nextWindow;
            isForward = forward;
            workArea = screen.WorkingArea;

            // Store window visibility states
            wasCurrentWindowVisible = NativeMethods.IsWindowVisible(currentWindow);
            wasNextWindowVisible = NativeMethods.IsWindowVisible(nextWindow);

            // Make both windows visible but not focused during animation
            NativeMethods.ShowWindow(currentWindow, NativeMethods.SW_SHOWNA);
            NativeMethods.ShowWindow(nextWindow, NativeMethods.SW_SHOWNA);
            
            // Completely disable DWM transitions and effects for both windows
            DisableWindowEffects(currentWindow);
            DisableWindowEffects(nextWindow);
        
            // Make animation window fully layered and transparent
            SetupAnimationWindow();

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            
            // Set extended window styles
            int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, exStyle);
    
            // Ensure exact positioning
            this.Location = new Point(screen.WorkingArea.Left, screen.WorkingArea.Top);
            this.Size = new Size(screen.WorkingArea.Width, screen.WorkingArea.Height);
    
            // Lock windows in place
            LockWindowPositions(currentWindow, nextWindow);
            
            this.Location = workArea.Location;
            this.Size = workArea.Size;
            this.DoubleBuffered = true;
            
            this.BackColor = Color.Black;
            this.TransparencyKey = Color.Black;
            
            InitializeDirectX();
            CaptureWindows(currentWindow, nextWindow);

            animationStopwatch = new System.Diagnostics.Stopwatch();
            lastFrameTime = DateTime.Now;

            Application.Idle += Application_Idle;
            this.FormClosing += (s, e) => 
            {
                Application.Idle -= Application_Idle;
                RestoreWindows();
                CleanupDirectX();
            };
        }
        
        private void PrepareWindowForCapture(IntPtr window, int index)
        {
            // Remove window decorations temporarily
            int style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
            style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME);
            NativeMethods.SetWindowLong(window, NativeMethods.GWL_STYLE, style);
        
            // Force immediate visual update
            NativeMethods.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | 
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED | 
                NativeMethods.SWP_NOACTIVATE);
            
            // Ensure window is ready for capture
            NativeMethods.RedrawWindow(window, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_ALLCHILDREN);
        }
        
        private void RestoreWindowStyle(IntPtr window, int index)
        {
            // Restore original style
            NativeMethods.SetWindowLong(window, NativeMethods.GWL_STYLE, originalStyles[index]);
            NativeMethods.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | 
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED | 
                NativeMethods.SWP_NOACTIVATE);
        }
        private void CaptureWindows(IntPtr currentWindow, IntPtr nextWindow)
    {
        // Prepare windows for capture
        PrepareWindowForCapture(currentWindow, 0);
        PrepareWindowForCapture(nextWindow, 1);

        try
        {
            // Capture logic
            using (var currentBitmap = new System.Drawing.Bitmap(workArea.Width, workArea.Height))
            using (var currentGraphics = Graphics.FromImage(currentBitmap))
            {
                currentGraphics.Clear(Color.Transparent);
                var currentHdc = currentGraphics.GetHdc();
                try
                {
                    bool success = NativeMethods.PrintWindow(currentWindow, currentHdc, 
                        NativeMethods.PW_RENDERFULLCONTENT);
                    
                    if (!success)
                    {
                        NativeMethods.PrintWindow(currentWindow, currentHdc, 0);
                    }
                }
                finally
                {
                    currentGraphics.ReleaseHdc(currentHdc);
                }

                currentWindowBitmap?.Dispose();
                currentWindowBitmap = CreateD2DBitmapFromGDIBitmap(currentBitmap);
            }

            // Repeat for next window...
            using (var nextBitmap = new System.Drawing.Bitmap(workArea.Width, workArea.Height))
            using (var nextGraphics = Graphics.FromImage(nextBitmap))
            {
                nextGraphics.Clear(Color.Transparent);
                var nextHdc = nextGraphics.GetHdc();
                try
                {
                    bool success = NativeMethods.PrintWindow(nextWindow, nextHdc, 
                        NativeMethods.PW_RENDERFULLCONTENT);
                    
                    if (!success)
                    {
                        NativeMethods.PrintWindow(nextWindow, nextHdc, 0);
                    }
                }
                finally
                {
                    nextGraphics.ReleaseHdc(nextHdc);
                }

                nextWindowBitmap?.Dispose();
                nextWindowBitmap = CreateD2DBitmapFromGDIBitmap(nextBitmap);
            }
        }
        finally
        {
            // Restore window styles
            RestoreWindowStyle(currentWindow, 0);
            RestoreWindowStyle(nextWindow, 1);
        }
    }

        private void SetupAnimationWindow()
        {
            // Set up the animation window to be completely invisible to mouse and composition
            int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED | 
                       NativeMethods.WS_EX_TRANSPARENT | 
                       NativeMethods.WS_EX_TOOLWINDOW | 
                       NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, exStyle);

            // Set window to be fully transparent
            NativeMethods.SetLayeredWindowAttributes(this.Handle, 0, 255, NativeMethods.LWA_ALPHA);
        }
        
        private void LockWindowPositions(IntPtr current, IntPtr next)
        {
            // Get exact window bounds
            RECT currentRect, nextRect;
            NativeMethods.GetWindowRect(current, out currentRect);
            NativeMethods.GetWindowRect(next, out nextRect);

            // Remove window decorations during animation
            int currentStyle = NativeMethods.GetWindowLong(current, NativeMethods.GWL_STYLE);
            int nextStyle = NativeMethods.GetWindowLong(next, NativeMethods.GWL_STYLE);

            // Create mask to remove window decorations
            int strippedStyle = ~(NativeMethods.WS_CAPTION | 
                                  NativeMethods.WS_THICKFRAME | 
                                  NativeMethods.WS_MINIMIZEBOX | 
                                  NativeMethods.WS_MAXIMIZEBOX | 
                                  NativeMethods.WS_SYSMENU);
                         
            currentStyle &= strippedStyle;
            nextStyle &= strippedStyle;

            // Apply stripped styles
            NativeMethods.SetWindowLong(current, NativeMethods.GWL_STYLE, currentStyle);
            NativeMethods.SetWindowLong(next, NativeMethods.GWL_STYLE, nextStyle);

            // Create flags for window positioning
            uint flags = NativeMethods.SWP_NOSIZE | 
                         NativeMethods.SWP_NOMOVE | 
                         NativeMethods.SWP_NOACTIVATE | 
                         NativeMethods.SWP_NOZORDER | 
                         NativeMethods.SWP_FRAMECHANGED;

            // Lock both windows with no decorations
            NativeMethods.SetWindowPos(current, IntPtr.Zero,
                currentRect.Left, currentRect.Top,
                currentRect.Right - currentRect.Left,
                currentRect.Bottom - currentRect.Top,
                flags);

            NativeMethods.SetWindowPos(next, IntPtr.Zero, 
                nextRect.Left, nextRect.Top,
                nextRect.Right - nextRect.Left,
                nextRect.Bottom - nextRect.Top,
                flags);
        }

        private void RestoreWindows()
        {
            // Restore original window visibility states
            NativeMethods.ShowWindow(currentWindowHandle, wasCurrentWindowVisible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
            NativeMethods.ShowWindow(nextWindowHandle, wasNextWindowVisible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        }

        private void InitializeDirectX()
        {
            d2dFactory = new D2D.Factory(FactoryType.SingleThreaded);

            var renderTargetProperties = new RenderTargetProperties(
                RenderTargetType.Hardware,
                new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                96, 96,
                RenderTargetUsage.None,
                FeatureLevel.Level_DEFAULT);

            var hwndProperties = new HwndRenderTargetProperties
            {
                Hwnd = this.Handle,
                PixelSize = new Size2(workArea.Width, workArea.Height),
                PresentOptions = PresentOptions.None // Changed from Immediately to None for smoother rendering
            };

            d2dRenderTarget = new WindowRenderTarget(d2dFactory, renderTargetProperties, hwndProperties);
            d2dRenderTarget.AntialiasMode = AntialiasMode.Aliased; // Prevent unwanted anti-aliasing
        }
        
        private void RecreateDirectXResources()
        {
            try
            {
                CleanupDirectX();
                InitializeDirectX();
                CaptureWindows(currentWindowHandle, nextWindowHandle);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to recreate DirectX resources: {ex.Message}");
                this.Close();
            }
        }

        private D2D.Bitmap CreateD2DBitmapFromGDIBitmap(System.Drawing.Bitmap gdiBitmap)
        {
            var sourceArea = new Rectangle(0, 0, gdiBitmap.Width, gdiBitmap.Height);
            var bitmapProperties = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied));
            var size = new Size2(gdiBitmap.Width, gdiBitmap.Height);

            BitmapData bitmapData = gdiBitmap.LockBits(
                sourceArea,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            try
            {
                return new D2D.Bitmap(
                    d2dRenderTarget,
                    size,
                    new DataPointer(bitmapData.Scan0, bitmapData.Stride * bitmapData.Height),
                    bitmapData.Stride,
                    bitmapProperties);
            }
            finally
            {
                gdiBitmap.UnlockBits(bitmapData);
            }
        }

        public void StartAnimation()
        {
            animationStopwatch.Start();
            this.Show();
            RenderFrame();
        }

        private void Application_Idle(object sender, EventArgs e)
        {
            while (IsApplicationIdle())
            {
                var now = DateTime.Now;
                if ((now - lastFrameTime).TotalMilliseconds >= 1000.0 / TARGET_FPS)
                {
                    UpdateAnimation();
                    lastFrameTime = now;
                }
            }
        }

        private bool IsApplicationIdle()
        {
            NativeMethods.Message msg;
            return !NativeMethods.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);
        }

        private void UpdateAnimation()
        {
            float elapsed = (float)animationStopwatch.ElapsedMilliseconds;
            currentPosition = elapsed;
            float progress = currentPosition / ANIMATION_DURATION;

            if (progress >= 1.0f)
            {
                animationStopwatch.Stop();
                this.Close();
                return;
            }

            if (Math.Abs(progress - lastProgress) > float.Epsilon)
            {
                RenderFrame();
                lastProgress = progress;
            }
        }

private void RenderFrame()
{
    if (d2dRenderTarget == null || isDisposed) return;

    try
    {
        d2dRenderTarget.BeginDraw();
        d2dRenderTarget.Clear(new RawColor4(0, 0, 0, 0));

        float progress = EaseInOutQuint(currentPosition / ANIMATION_DURATION);
        float offset = (float)(workArea.Width * progress * (isForward ? -1 : 1));
        offset = (float)Math.Round(offset); // Ensure pixel-perfect alignment

        var opacity = 1.0f;
        var interpolation = BitmapInterpolationMode.Linear;

        if (nextWindowBitmap != null && !nextWindowBitmap.IsDisposed)
        {
            float nextX = offset + (isForward ? workArea.Width : -workArea.Width);
            nextX = (float)Math.Round(nextX);
            
            // Create transformation matrix for next window
            d2dRenderTarget.Transform = new Matrix3x2(1, 0, 0, 1, nextX, 0);
            
            var nextDestRect = new RawRectangleF(0, 0, workArea.Width, workArea.Height);
            d2dRenderTarget.DrawBitmap(nextWindowBitmap, nextDestRect, opacity, interpolation);
        }

        if (currentWindowBitmap != null && !currentWindowBitmap.IsDisposed)
        {
            // Create transformation matrix for current window
            d2dRenderTarget.Transform = new Matrix3x2(1, 0, 0, 1, offset, 0);
            
            var currentDestRect = new RawRectangleF(0, 0, workArea.Width, workArea.Height);
            d2dRenderTarget.DrawBitmap(currentWindowBitmap, currentDestRect, opacity, interpolation);
        }

        // Reset transform
        d2dRenderTarget.Transform = new Matrix3x2(1, 0, 0, 1, 0, 0);
        d2dRenderTarget.EndDraw();
    }
    catch (Exception)
    {
        RecreateDirectXResources();
    }
}

        private float EaseInOutQuint(float t)
        {
            // Clamp input value between 0 and 1
            t = Math.Max(0, Math.Min(1, t));
    
            return t < 0.5f
                ? 16 * t * t * t * t * t
                : 1 - (float)Math.Pow(-2 * t + 2, 5) / 2;
        }

        private void CleanupDirectX()
        {
            currentWindowBitmap?.Dispose();
            nextWindowBitmap?.Dispose();
            d2dRenderTarget?.Dispose();
            d2dFactory?.Dispose();
        }
    }

internal static class NativeMethods
{
    // Window Messages
    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public IntPtr HWnd;
        public uint Msg;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public System.Drawing.Point Point;
    }

    // Show Window Commands
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNA = 8;

    // Window Styles (WS_*)
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;

    // Extended Window Styles (WS_EX_*)
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // SetWindowPos Flags (SWP_*)
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    // GetWindow/SetWindow Commands
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // DWM Attributes
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_MICA_EFFECT = 1029;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWA_BYPASS_COMPOSITOR = 22;

    // PrintWindow flags
    public const int PW_RENDERFULLCONTENT = 0x00000002;

    // RedrawWindow flags
    public const uint RDW_UPDATENOW = 0x0100;
    public const uint RDW_ALLCHILDREN = 0x0080;

    // Layered Window Attributes
    public const uint LWA_ALPHA = 0x2;
        
        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out Message msg, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax, uint flags);
        
        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, 
            IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}