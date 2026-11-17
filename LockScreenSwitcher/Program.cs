using Windows.Win32;
using Windows.Win32.System.WinRT;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LockScreenSwitcher;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            PInvoke.RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED).ThrowOnFailure();
            try
            {
                return new IslandWindow().Run();
            }
            finally
            {
                PInvoke.RoUninitialize();
            }
        }
        catch (Exception exception)
        {
            PInvoke.MessageBox(default, exception.Message, IslandWindow.Title, MESSAGEBOX_STYLE.MB_ICONERROR);
            return 1;
        }
    }
}
