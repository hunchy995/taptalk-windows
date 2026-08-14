using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Taptalk.WPF;

/// <summary>
/// Configurable global hotkey manager. Default is Ctrl+Shift+Space.
/// Modifiers and key are stored as Win32 modifier flags and virtual-key codes.
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public const uint VK_SPACE = 0x0020;

    private const int HOTKEY_ID = 0xCAFE;
    private const int WM_HOTKEY = 0x0312;

    private HwndSource? _source;
    private IntPtr _hwnd;

    public uint Modifiers { get; private set; } = MOD_CONTROL | MOD_SHIFT;
    public uint VirtualKey { get; private set; } = VK_SPACE;

    public event Action? OnHotKeyPressed;

    /// <summary>Update the global hotkey. Unregisters the old one and registers the new combo.</summary>
    public void SetHotKey(IntPtr handle, uint modifiers, uint virtualKey)
    {
        if (handle == IntPtr.Zero) return;

        // Reject combos with no modifiers — bare letter keys would steal typing.
        if ((modifiers & (MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN)) == 0)
        {
            DebugRecorder.Log("SYS", "Hotkey rejected: at least one modifier required");
            return;
        }

        if (_hwnd != IntPtr.Zero && _hwnd == handle)
            UnregisterHotKey(_hwnd, HOTKEY_ID);

        Modifiers = modifiers;
        VirtualKey = virtualKey;

        if (_source == null || _hwnd != handle)
        {
            _hwnd = handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
        }

        bool ok = RegisterHotKey(handle, HOTKEY_ID, modifiers, virtualKey);
        DebugRecorder.Log("SYS", ok
            ? $"Registered global hotkey: {FormatHotKey(modifiers, virtualKey)}"
            : "Failed to register global hotkey (already in use?)");
    }

    public void Register(IntPtr handle)
    {
        SetHotKey(handle, Modifiers, VirtualKey);
    }

    public void Unregister()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            _hwnd = IntPtr.Zero;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            OnHotKeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    /// <summary>Convert Win32 modifiers+VK to a readable string like "Ctrl+Shift+Space".</summary>
    public static string FormatHotKey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(FormatVirtualKey(virtualKey));
        return string.Join("+", parts);
    }

    public static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey == VK_SPACE) return "Space";
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString().ToUpperInvariant();
        // Function keys
        if (virtualKey is >= 0x70 and <= 0x87)
            return $"F{virtualKey - 0x6F}";
        return $"0x{virtualKey:X}";
    }

    /// <summary>Parse a single character/letter into a virtual-key code. Returns 0 if not alphnumeric/function.</summary>
    public static uint ParseKeyChar(char key)
    {
        char upper = char.ToUpperInvariant(key);
        if (upper is >= 'A' and <= 'Z') return (uint)upper;
        if (key == ' ') return VK_SPACE;
        if (key is >= '0' and <= '9') return (uint)key;
        return 0;
    }
}
