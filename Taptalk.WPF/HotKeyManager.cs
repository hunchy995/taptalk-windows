using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Taptalk.WPF;

/// <summary>
/// Global hotkey: Ctrl+Shift+Space push-to-talk toggle.
/// (Alt+Space was avoided — Windows reserves it for the system menu.)
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_SPACE = 0x0020;
    private const int HOTKEY_ID = 0xCAFE;
    private const int WM_HOTKEY = 0x0312;

    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? OnHotKeyPressed;

    public void Register(IntPtr handle)
    {
        _hwnd = handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_SPACE);
    }

    public void Unregister()
    {
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HOTKEY_ID);
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
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
}
