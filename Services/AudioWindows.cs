using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CallAudioRecorder.Services;

/// <summary>Окно приложения, звук которого можно записать отдельно от остальной системы.</summary>
/// <param name="ProcessId">PID процесса окна. Звук берётся с него вместе с дочерними процессами.</param>
/// <param name="PlayingAudio">Приложение прямо сейчас что-то воспроизводит.</param>
public sealed record AudioWindow(int ProcessId, string ProcessName, string Title, bool PlayingAudio)
{
    public override string ToString()
    {
        var title = Title.Length > 60 ? Title[..57] + "…" : Title;
        return PlayingAudio ? $"♪ {title} — {ProcessName}" : $"{title} — {ProcessName}";
    }
}

/// <summary>Перечисление окон, пригодных для выбора источника звука.</summary>
public static class AudioWindows
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmCloaked = 14;

    /// <summary>
    /// Видимые окна с заголовком, кроме служебных и собственного окна приложения.
    /// Сначала те, что сейчас играют звук, — их и ищут в списке.
    /// </summary>
    public static List<AudioWindow> Enumerate()
    {
        var (activePids, activeNames) = ActiveAudioSources();
        int self = Environment.ProcessId;
        var windows = new List<AudioWindow>();
        var seen = new HashSet<(int, string)>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd) || IsToolWindow(hwnd)) return true;

            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0 || pid == self) return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            string text = title.ToString().Trim();
            if (text.Length == 0) return true;

            string process = ProcessName(pid);
            if (process.Length == 0) return true;
            if (!seen.Add((pid, text))) return true;

            // Для браузеров и Electron звук играет дочерний процесс с тем же именем —
            // поэтому «играет звук» проверяется и по PID, и по имени процесса.
            bool playing = activePids.Contains(pid) || activeNames.Contains(process);
            windows.Add(new AudioWindow(pid, process, text, playing));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderByDescending(w => w.PlayingAudio)
            .ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Процессы с активными сессиями воспроизведения на любом из устройств вывода.</summary>
    private static (HashSet<int> Pids, HashSet<string> Names) ActiveAudioSources()
    {
        var pids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                        int pid = (int)session.GetProcessID;
                        if (pid == 0) continue;
                        pids.Add(pid);
                        var name = ProcessName(pid);
                        if (name.Length > 0) names.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Список сессий — только подсказка «здесь сейчас играет звук»; без него окна всё равно выбираются.
        }
        return (pids, names);
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return ""; // процесс уже закрылся или недоступен под нашими правами
        }
    }

    private static bool IsToolWindow(IntPtr hwnd) => (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0;

    /// <summary>UWP-приложения держат скрытые окна-призраки — Windows помечает их как cloaked.</summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLong(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
