using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Taptalk.WPF;

/// <summary>
/// Types text into a target window using fake Unicode keystrokes.
/// Focus is restored by the caller before calling InjectText.
/// </summary>
public static class TextInjector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    /// <summary>Type the transcription into the focused window. If a target handle is provided, focus is restored first.</summary>
    public static void InjectText(string text, IntPtr targetHwnd)
    {
        if (string.IsNullOrEmpty(text))
        {
            Taptalk.Core.DebugRecorder.Log("INJ", "Text empty — skipping injection");
            return;
        }

        string target = "Unknown";
        try
        {
            if (targetHwnd != IntPtr.Zero)
            {
                SetForegroundWindow(targetHwnd);
                var sb = new StringBuilder(256);
                if (GetWindowText(targetHwnd, sb, 256) > 0) target = sb.ToString();
            }
            else
            {
                target = GetForegroundWindowTitle();
            }
        }
        catch { }

        Taptalk.Core.DebugRecorder.Log("INJ", $"Typing {text.Length} chars into active window '{target}': \"{text}\"");
        SendUnicodeKeys(text);
        Taptalk.Core.DebugRecorder.Log("INJ", "Typing complete");
    }

    private static string GetForegroundWindowTitle()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                if (GetWindowText(hwnd, sb, 256) > 0) return sb.ToString();
            }
        }
        catch { }
        return "Unknown";
    }

    private static void SendUnicodeKeys(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE }
            };
            inputs[i * 2 + 1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
