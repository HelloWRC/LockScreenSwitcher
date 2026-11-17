using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

namespace LockScreenSwitcher;

internal sealed unsafe class IslandWindow
{
    private const string WindowClass = "LockScreenSwitcher.Host";
    internal const string Title = "选择锁屏应用";
    private const WINDOW_STYLE Style = WINDOW_STYLE.WS_OVERLAPPED | WINDOW_STYLE.WS_CAPTION |
        WINDOW_STYLE.WS_SYSMENU | WINDOW_STYLE.WS_CLIPCHILDREN;
    private const WINDOW_EX_STYLE ExtendedStyle = WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME;
    private const uint WorkCompleted = PInvoke.WM_APP + 1;
    private const uint FocusIsland = PInvoke.WM_APP + 2;
    private const SET_WINDOW_POS_FLAGS PositionFlags =
        SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
    private static IslandWindow? current;
    private HWND window;
    private HWND child;
    private DesktopWindowXamlSource? source;
    private ObjectReference<IslandInterop.Vtable>? native;
    private SwitcherView? view;
    private ExceptionDispatchInfo? windowError;

    internal int Run()
    {
        current = this;
        try
        {
            using var module = PInvoke.GetModuleHandle((string?)null);
            Check(!module.IsInvalid);
            fixed (char* className = WindowClass)
            {
                var windowClass = new WNDCLASSW
                {
                    hInstance = (HINSTANCE)module.DangerousGetHandle(),
                    lpfnWndProc = &WindowProc,
                    hCursor = PInvoke.LoadCursor(default, (char*)32512),
                    lpszClassName = className,
                };
                Check(PInvoke.RegisterClass(in windowClass) != 0);
            }

            window = PInvoke.CreateWindowEx(ExtendedStyle, WindowClass, Title, Style,
                PInvoke.CW_USEDEFAULT, PInvoke.CW_USEDEFAULT, 429, 541, default, default, module, null);
            Check(window != default);

            using var manager = WindowsXamlManager.InitializeForCurrentThread();
            try
            {
                source = new DesktopWindowXamlSource();
                native = IslandInterop.GetNativeInterface(source);
                child = IslandInterop.Attach(native, window);
                Check(child != default);
                HWND host = window;
                view = new SwitcherView(
                    () => PInvoke.PostMessage(host, WorkCompleted, default, default),
                    () => FitWindow());
                source.Content = view.Root;
                view.Root.ActualThemeChanged += OnThemeChanged;
                OnThemeChanged(view.Root, null);
                FitWindow(center: true);
                LayoutIsland();
                PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOW);
                view.Load(PInvoke.GetDpiForWindow(window));
                return RunMessageLoop();
            }
            finally
            {
                try
                {
                    if (view is not null)
                    {
                        view.Root.ActualThemeChanged -= OnThemeChanged;
                    }
                    native?.Dispose();
                    if (source is not null)
                    {
                        source.Dispose();
                    }
                }
                finally
                {
                    child = default;
                    view = null;
                    DestroyHost();
                }
            }
        }
        finally
        {
            DestroyHost();
            current = null;
        }
    }

    private void OnThemeChanged(FrameworkElement element, object? args)
    {
        BOOL dark = element.ActualTheme == ElementTheme.Dark;
        PInvoke.DwmSetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
            &dark, (uint)sizeof(BOOL));
    }

    private void DestroyHost()
    {
        if (window != default)
        {
            PInvoke.DestroyWindow(window);
            window = default;
        }
    }

    private int RunMessageLoop()
    {
        while (true)
        {
            int result = PInvoke.GetMessage(out MSG message, default, 0, 0);
            Check(result != -1);
            if (result == 0)
            {
                windowError?.Throw();
                return (int)message.wParam.Value;
            }

            if (message.hwnd == window && message.message == PInvoke.WM_KEYDOWN &&
                message.wParam.Value == (nuint)VIRTUAL_KEY.VK_TAB)
            {
                var direction = PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) < 0
                    ? XamlSourceFocusNavigationReason.Last
                    : XamlSourceFocusNavigationReason.First;
                if (EnterIsland(direction))
                {
                    continue;
                }
            }

            if (!IslandInterop.PreTranslateMessage(native!, message))
            {
                PInvoke.TranslateMessage(in message);
                PInvoke.DispatchMessage(in message);
            }
        }
    }

    private bool EnterIsland(XamlSourceFocusNavigationReason direction) =>
        view?.CanReceiveFocus == true && source?.NavigateFocus(new XamlSourceFocusNavigationRequest(direction))
            .WasFocusMoved == true;

    private void FitWindow(bool center = false, RECT* suggested = null, uint dpi = 0)
    {
        dpi = dpi == 0 ? PInvoke.GetDpiForWindow(window) : dpi;
        int clientHeight = view?.HasError == true ? 583 : 541;
        var bounds = new RECT
        {
            right = (int)Math.Round(429 * dpi / 96d),
            bottom = (int)Math.Round(clientHeight * dpi / 96d),
        };
        Check(PInvoke.AdjustWindowRectExForDpi(ref bounds, Style, false, ExtendedStyle, dpi));

        var monitor = suggested is null
            ? PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST)
            : PInvoke.MonitorFromRect(in *suggested, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        Check(PInvoke.GetMonitorInfo(monitor, ref info));
        RECT work = info.rcWork;
        int width = Math.Min(bounds.right - bounds.left, work.right - work.left);
        int height = Math.Min(bounds.bottom - bounds.top, work.bottom - work.top);

        Check(PInvoke.GetWindowRect(window, out RECT previous));
        RECT origin = suggested is null ? previous : *suggested;
        int x = center ? work.left + (work.right - work.left - width) / 2
            : Math.Clamp(origin.left, work.left, work.right - width);
        int y = center ? work.top + (work.bottom - work.top - height) / 2
            : Math.Clamp(origin.top, work.top, work.bottom - height);
        Check(PInvoke.SetWindowPos(window, default, x, y, width, height, PositionFlags));
    }

    private void LayoutIsland()
    {
        if (child != default)
        {
            Check(PInvoke.GetClientRect(window, out RECT client));
            Check(PInvoke.SetWindowPos(child, default, 0, 0, client.right, client.bottom,
                PositionFlags | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (current is { } host)
        {
            try
            {
                switch (message)
                {
                    case PInvoke.WM_SIZE:
                        host.LayoutIsland();
                        return default;
                    case PInvoke.WM_DPICHANGED when host.view is not null:
                        host.FitWindow(suggested: (RECT*)lParam.Value, dpi: (uint)(wParam.Value & 0xffff));
                        return default;
                    case PInvoke.WM_SETFOCUS when host.source?.Content is not null:
                        PInvoke.PostMessage(hwnd, FocusIsland, default, default);
                        return default;
                    case FocusIsland when PInvoke.GetFocus() == hwnd:
                        host.EnterIsland(XamlSourceFocusNavigationReason.First);
                        return default;
                    case WorkCompleted:
                        host.view?.CompletePendingWork();
                        if (PInvoke.GetFocus() == hwnd)
                        {
                            host.EnterIsland(XamlSourceFocusNavigationReason.First);
                        }
                        return default;
                    case PInvoke.WM_CLOSE:
                        if (host.view?.IsApplying != true)
                        {
                            PInvoke.PostQuitMessage(0);
                        }
                        return default;
                    case PInvoke.WM_DESTROY:
                        host.window = default;
                        PInvoke.PostQuitMessage(0);
                        return default;
                }
            }
            catch (Exception exception)
            {
                // Do not unwind a managed exception through the native window procedure.
                host.windowError = ExceptionDispatchInfo.Capture(exception);
                PInvoke.PostQuitMessage(1);
                return default;
            }
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void Check(bool result)
    {
        if (!result)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}
