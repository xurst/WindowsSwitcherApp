// Program.cs
using System;
using System.Windows.Forms;

namespace WindowsApplicationSwiper
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            // Create and run the window switcher directly
            Application.Run(new WindowSwitcher());
        }
    }
}