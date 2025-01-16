using System.Windows;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace WindowsApplicationSwiper
{
    public partial class App : Application
    {
        private WindowSpy? _windowSpy;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Create debug spy form
            _windowSpy = new WindowSpy();
            _windowSpy.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _windowSpy?.Dispose();
            base.OnExit(e);
        }
    }
}