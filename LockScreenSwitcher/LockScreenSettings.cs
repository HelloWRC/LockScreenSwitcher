using System.Security.Principal;
using Microsoft.Win32;

namespace LockScreenSwitcher;

internal static class LockScreenSettings
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lock Screen";
    private const string ValueName = "LockAppAumId";
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    internal static string? Read()
    {
        foreach (var view in Views)
        {
            if (ReadValue(view).Value is string { Length: > 0 } aumid)
            {
                return aumid;
            }
        }

        return null;
    }

    internal static void Apply(string aumid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var gate = new Mutex(false, $@"Global\LockScreenSwitcher.{identity.User?.Value}");
        try
        {
            if (!gate.WaitOne(0))
            {
                throw new InvalidOperationException("另一个锁屏切换器窗口正在应用更改，请稍后重试。");
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous process exited while it owned the mutex. Read the current values below.
        }

        try
        {
            ProviderCatalog.EnsureAvailable(aumid);
            Save(aumid, ReadValue, WriteValue);
            try
            {
                ShellRefresh.Run();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"已写入锁屏设置，但未能刷新当前会话。可重新选择此应用后确认以重试。\n{exception.Message}", exception);
            }
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    internal static void Save(string aumid, Func<RegistryView, RegistryValue> read,
        Action<RegistryView, RegistryValue> write)
    {
        var previous = Views.Select(read).ToArray();
        var requested = new RegistryValue(aumid, RegistryValueKind.String);
        int attempted = 0;
        try
        {
            for (int i = 0; i < Views.Length; i++)
            {
                attempted = i + 1;
                write(Views[i], requested);
            }

            foreach (var view in Views)
            {
                if (read(view) != requested)
                {
                    throw new InvalidOperationException($"无法验证 {view} 注册表视图中的锁屏设置。");
                }
            }
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<string>();
            for (int i = attempted - 1; i >= 0; i--)
            {
                try
                {
                    // Leave concurrent external writes intact. The two Windows registry views can share one value.
                    var actual = read(Views[i]);
                    if (actual == requested)
                    {
                        write(Views[i], previous[i]);
                    }
                }
                catch (Exception rollback)
                {
                    rollbackErrors.Add($"{Views[i]}：{rollback.Message}");
                }
            }

            string message = $"无法写入锁屏设置。\n{exception.Message}";
            if (rollbackErrors.Count > 0)
            {
                message += $"\n恢复原值失败：{string.Join("；", rollbackErrors)}";
            }
            throw new InvalidOperationException(message, exception);
        }
    }

    private static RegistryValue ReadValue(RegistryView view)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var key = root.OpenSubKey(KeyPath);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? default : new(value, key!.GetValueKind(ValueName));
    }

    private static void WriteValue(RegistryView view, RegistryValue value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        if (value.Value is null)
        {
            using var existing = root.OpenSubKey(KeyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = root.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, value.Value, value.Kind);
    }

    internal readonly record struct RegistryValue(object? Value, RegistryValueKind Kind);
}
