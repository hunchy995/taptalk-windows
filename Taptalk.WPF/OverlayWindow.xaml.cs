using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Taptalk.WPF;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private bool _dragging;
    private Point _dragStart;
    private bool _downOnButton;
    private bool _wasDragged;

    public event Action? OnTap;
    public event Action? OnDragEnd;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyNoActivate();
    }

    private void ApplyNoActivate()
    {
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void SetIdle()
    {
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7));
        MicIcon.Text = "🎙️";
        PulseRing.Opacity = 0;
    }

    public void SetRecording()
    {
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        MicIcon.Text = "⏹️";
        StartPulse();
    }

    public void SetProcessing()
    {
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
        MicIcon.Text = "⏳";
        StopPulse();
    }

    public void SetDone()
    {
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        MicIcon.Text = "✅";
        StopPulse();
    }

    private void StartPulse()
    {
        var anim = new DoubleAnimation(0.4, 0.0, TimeSpan.FromMilliseconds(1400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        PulseRing.BeginAnimation(OpacityProperty, anim);
        var scale = new DoubleAnimation(0.9, 1.1, TimeSpan.FromMilliseconds(1400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        PulseRing.BeginAnimation(System.Windows.Controls.Canvas.WidthProperty, scale);
        PulseRing.Opacity = 0.4;
    }

    private void StopPulse()
    {
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.Opacity = 0;
    }

    private void MicButton_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _downOnButton = true;
        _wasDragged = false;
        _dragStart = e.GetPosition(this);
    }

    private void MicButton_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_downOnButton || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;
        if (Math.Abs(dx) > 5 || Math.Abs(dy) > 5)
        {
            _wasDragged = true;
            Left += dx;
            Top += dy;
        }
    }

    private void MicButton_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_downOnButton) return;
        _downOnButton = false;
        if (!_wasDragged)
            OnTap?.Invoke();
        else
            OnDragEnd?.Invoke();
    }
}
