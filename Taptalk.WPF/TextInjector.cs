using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using IDataObject = System.Windows.IDataObject;
using Clipboard = System.Windows.Clipboard;

namespace Taptalk.WPF;

/// <summary>
/// Types text into the focused window. Short text → direct Unicode keystrokes.
/// Long text → clipboard + paste, with fallback to keystrokes if clipboard is busy.
/// </summary>
public static class TextInjector
{
    private const int DirectTypeLimit = 200;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    /// <summary>Send a raw keyboard shortcut (e.g. Ctrl+V) to the active window.</summary>
    public static void SendKeyboardShortcut(bool ctrl, bool shift, bool alt, ushort vKey)
    {
        var list = new List<INPUT>();

        void KeyDown(ushort vk) => list.Add(new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk }
        });
        void KeyUp(ushort vk) => list.Add(new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP }
        });

        if (ctrl) KeyDown(0x11); // VK_CONTROL
        if (shift) KeyDown(0x10); // VK_SHIFT
        if (alt) KeyDown(0x12); // VK_MENU
        KeyDown(vKey);
        KeyUp(vKey);
        if (alt) KeyUp(0x12);
        if (shift) KeyUp(0x10);
        if (ctrl) KeyUp(0x11);

        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static async Task InjectTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Taptalk.Core.DebugRecorder.Log("INJ", "Text empty — skipping injection");
            return;
        }

        // Capture the foreground window title for diagnostics
        string target = "Unknown";
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(256);
                if (GetWindowText(hwnd, sb, 256) > 0) target = sb.ToString();
            }
        }
        catch { }

        if (text.Length <= DirectTypeLimit)
        {
            Taptalk.Core.DebugRecorder.Log("INJ", $"Direct keystrokes → '{target}' | chars={text.Length} | text=\"{text}\"");
            SendUnicodeKeys(text);
            Taptalk.Core.DebugRecorder.Log("INJ", "Direct keystrokes complete");
        }
        else
        {
            Taptalk.Core.DebugRecorder.Log("INJ", $"Clipboard+Ctrl+V → '{target}' | chars={text.Length} | text=\"{text}\"");
            await InjectViaClipboardAsync(text);
            Taptalk.Core.DebugRecorder.Log("INJ", "Clipboard injection complete");
        }
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

    private static async Task InjectViaClipboardAsync(string text)
    {
        IDataObject? current = null;
        try { current = Clipboard.GetDataObject(); } catch { /* clipboard busy */ }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                SendPaste();
                await Task.Delay(120);
                break; // success
            }
            catch
            {
                if (attempt == 9)
                {
                    // Clipboard failed repeatedly — fall back to keystrokes
                    SendUnicodeKeys(text);
                    break;
                }
                await Task.Delay(50);
            }
        }

        if (current != null)
        {
            await Task.Delay(80);
            try { Clipboard.SetDataObject(current); } catch { }
        }
    }

    private static void SendPaste()
    {
        // Give the target field a moment to receive focus before sending Ctrl+V
        Thread.Sleep(80);
        SendKeyboardShortcut(true, false, false, 0x56); // Ctrl + V
    }
}
