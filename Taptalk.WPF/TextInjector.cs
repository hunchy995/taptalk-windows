using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Taptalk.WPF;

/// <summary>
/// Types text into the currently focused window by simulating a real keyboard.
/// Uses scan-code based SendInput (the same signals a physical keyboard sends),
/// which is accepted by Chrome, Edge, Word, Notepad, and most modern apps.
/// Unicode events are used only for characters that have no keyboard mapping.
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
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LSHIFT = 0xA0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanExW(char ch, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>Type the text into the focused window. If targetHwnd is provided, focus is restored first.</summary>
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
        SendTextAsKeyboard(text);
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

    private static void SendTextAsKeyboard(string text)
    {
        var inputs = new List<INPUT>();
        IntPtr hkl = GetKeyboardLayout(0);

        foreach (char c in text)
        {
            short vkAndShift = VkKeyScanExW(c, hkl);
            if (vkAndShift == -1)
            {
                // No keyboard mapping for this character (e.g. emoji, some Unicode) — fall back to Unicode event.
                inputs.Add(MakeUnicode(c, false));
                inputs.Add(MakeUnicode(c, true));
                continue;
            }

            byte vk = (byte)(vkAndShift & 0xFF);
            byte shiftState = (byte)((vkAndShift >> 8) & 0xFF);
            bool needShift = (shiftState & 1) != 0;
            bool needCtrl = (shiftState & 2) != 0;
            bool needAlt = (shiftState & 4) != 0;

            uint scan = MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC, hkl);
            uint flags = KEYEVENTF_SCANCODE;
            // Some keys (arrows, etc.) are extended; for typing text this is rare.

            if (needShift) inputs.Add(MakeKey(VK_SHIFT, 0, false));
            if (needCtrl) inputs.Add(MakeKey(0x11, 0, false)); // VK_CONTROL
            if (needAlt) inputs.Add(MakeKey(0x12, 0, false));  // VK_MENU

            inputs.Add(MakeKey(vk, (ushort)scan, false, flags));
            inputs.Add(MakeKey(vk, (ushort)scan, true, flags));

            if (needAlt) inputs.Add(MakeKey(0x12, 0, true));
            if (needCtrl) inputs.Add(MakeKey(0x11, 0, true));
            if (needShift) inputs.Add(MakeKey(VK_SHIFT, 0, true));
        }

        if (inputs.Count == 0) return;

        var arr = inputs.ToArray();
        uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
        {
            int err = Marshal.GetLastWin32Error();
            Taptalk.Core.DebugRecorder.Log("INJ", $"SendInput sent {sent}/{arr.Length}, last error={err}");
        }
    }

    private static INPUT MakeKey(uint vk, ushort scan, bool up, uint extraFlags = 0)
    {
        uint flags = extraFlags;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (vk == 0) flags |= KEYEVENTF_UNICODE; // scan-only fallback safety

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = scan,
                dwFlags = flags
            }
        };
    }

    private static INPUT MakeUnicode(char c, bool up)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0)
            }
        };
    }
}
