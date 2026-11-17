using System.Diagnostics;
using Windows.Win32;

namespace LockScreenSwitcher;

internal static class ShellRefresh
{
    private const int ProcessExitTimeout = 1500;
    private const int ShellStartTimeout = 3000;

    internal static void Run()
    {
        using var current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        var failures = new List<string>();
        StopLockScreenProcesses("LockAppHost", sessionId, failures);
        StopLockScreenProcesses("LockApp", sessionId, failures);
        try
        {
            RestartExplorer(sessionId);
        }
        catch (Exception exception)
        {
            failures.Add($"Explorer：{exception.Message}");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", failures));
        }
    }

    private static void StopLockScreenProcesses(string name, int sessionId, List<string> failures)
    {
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == sessionId)
                    {
                        Stop(process);
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
                {
                    if (!process.HasExited)
                    {
                        failures.Add($"{name}：{exception.Message}");
                    }
                }
            }
        }
    }

    private static void RestartExplorer(int sessionId)
    {
        using Process? shell = GetDesktopExplorer(sessionId);
        int previousPid = shell?.Id ?? 0;
        if (shell is not null)
        {
            Stop(shell);
            if (WaitForExplorer(sessionId, previousPid))
            {
                return;
            }
        }

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using var replacement = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
        if (!WaitForExplorer(sessionId, previousPid))
        {
            throw new TimeoutException("等待桌面资源管理器恢复超时。");
        }
    }

    private static void Stop(Process process)
    {
        process.Kill();
        if (!process.WaitForExit(ProcessExitTimeout))
        {
            throw new TimeoutException("等待进程退出超时。");
        }
    }

    private static bool WaitForExplorer(int sessionId, int previousPid)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ShellStartTimeout)
        {
            using var process = GetDesktopExplorer(sessionId);
            if (process is not null && process.Id != previousPid)
            {
                return true;
            }
            Thread.Sleep(100);
        }

        return false;
    }

    private static unsafe Process? GetDesktopExplorer(int sessionId)
    {
        var window = PInvoke.GetShellWindow();
        uint pid;
        if (window == default || PInvoke.GetWindowThreadProcessId(window, &pid) == 0)
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById((int)pid);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (process.SessionId == sessionId && string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }
        }
        catch (InvalidOperationException)
        {
            // Explorer may exit between reading the shell window and checking the process.
        }

        process.Dispose();
        return null;
    }
}
