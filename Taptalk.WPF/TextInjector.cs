using System.Runtime.InteropServices;
using System.Windows;

namespace Taptalk.WPF;

/// <summary>
/// Injects text into the focused window. Short text → Unicode keystrokes.
/// Long text → clipboard + Ctrl+V fallback. Preserves clipboard.
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

    public static async Task InjectTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (text.Length < 30)
            SendUnicodeKeys(text);
        else
            await InjectViaClipboardAsync(text);
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

        try
        {
            Clipboard.SetText(text);
            SendPaste();
            await Task.Delay(120);
        }
        catch
        {
            // Clipboard failed — fall back to keystrokes
            SendUnicodeKeys(text);
        }
        finally
        {
            if (current != null)
            {
                await Task.Delay(80);
                try { Clipboard.SetDataObject(current); } catch { }
            }
        }
    }

    private static void SendPaste()
    {
        var inputs = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x11 } },                    // Ctrl down
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x56 } },                    // V down
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x56, dwFlags = KEYEVENTF_KEYUP } }, // V up
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x11, dwFlags = KEYEVENTF_KEYUP } }  // Ctrl up
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
