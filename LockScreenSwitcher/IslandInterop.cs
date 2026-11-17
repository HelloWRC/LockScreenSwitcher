using System.Runtime.InteropServices;
using Windows.UI.Xaml.Hosting;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

namespace LockScreenSwitcher;

internal static unsafe class IslandInterop
{
    private static readonly Guid NativeInterfaceId = new("e3dcd8c7-3057-4692-99c3-7b7720afda31");

    // IDesktopWindowXamlSourceNative2 adds PreTranslateMessage after the two Native methods.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vtable
    {
        private nint queryInterface;
        private nint addRef;
        private nint release;
        internal delegate* unmanaged[Stdcall]<nint, HWND, int> AttachToWindow;
        internal delegate* unmanaged[Stdcall]<nint, HWND*, int> GetWindowHandle;
        internal delegate* unmanaged[Stdcall]<nint, MSG*, BOOL*, int> PreTranslateMessage;
    }

    internal static ObjectReference<Vtable> GetNativeInterface(DesktopWindowXamlSource source) =>
        ((IWinRTObject)source).NativeObject.As<Vtable>(NativeInterfaceId);

    internal static HWND Attach(ObjectReference<Vtable> native, HWND parent)
    {
        HWND child = default;
        Marshal.ThrowExceptionForHR(native.Vftbl.AttachToWindow(native.ThisPtr, parent));
        Marshal.ThrowExceptionForHR(native.Vftbl.GetWindowHandle(native.ThisPtr, &child));
        return child;
    }

    internal static bool PreTranslateMessage(ObjectReference<Vtable> native, MSG message)
    {
        BOOL handled = default;
        Marshal.ThrowExceptionForHR(native.Vftbl.PreTranslateMessage(native.ThisPtr, &message, &handled));
        return handled;
    }
}
