using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

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
    private Storyboard? _pulseStoryboard;
    private readonly List<Rectangle> _waveformBars = new();
    private const int BarCount = 5;

    public event Action? OnTap;
    public event Action? OnDragEnd;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyNoActivate();
            BuildWaveformBars();
            SetIdle();
        };
    }

    private void ApplyNoActivate()
    {
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void BuildWaveformBars()
    {
        if (WaveformCanvas == null) return;

        WaveformCanvas.Children.Clear();
        _waveformBars.Clear();

        double centerX = WaveformCanvas.Width / 2;
        double centerY = WaveformCanvas.Height / 2;
        double radius = 34;
        double barW = 3;
        double maxBarH = 16;

        for (int i = 0; i < BarCount; i++)
        {
            double angle = i * (360.0 / BarCount) * Math.PI / 180.0;
            double x = centerX + Math.Cos(angle) * radius - barW / 2;
            double y = centerY + Math.Sin(angle) * radius - maxBarH / 2;

            var rect = new Rectangle
            {
                Width = barW,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x2B)),
                Opacity = 0.0
            };

            // Rotate each bar to point outward from center
            var transform = new TransformGroup();
            transform.Children.Add(new RotateTransform(angle * 180 / Math.PI, barW / 2, maxBarH / 2));
            transform.Children.Add(new TranslateTransform(x, y));
            rect.RenderTransform = transform;

            WaveformCanvas.Children.Add(rect);
            _waveformBars.Add(rect);
        }
    }

    public void UpdateWaveform(float rms, float peak, bool isRecording)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateWaveform(rms, peak, isRecording));
            return;
        }

        if (_waveformBars.Count == 0) return;

        float level = isRecording ? Math.Max(rms, peak * 0.4f) : 0f;
        level = Math.Min(1f, level * 4f); // amplify for visibility

        for (int i = 0; i < _waveformBars.Count; i++)
        {
            var bar = _waveformBars[i];

            // Offset each bar's phase slightly so it looks organic
            double offset = i / (double)BarCount;
            double thisLevel = Math.Max(0, level - offset * 0.2);
            double height = 3 + thisLevel * 14;
            double opacity = isRecording ? 0.5 + thisLevel * 0.5 : 0;

            bar.Height = height;
            bar.Opacity = opacity;
        }
    }

    public void SetIdle()
    {
        if (MicButton == null || MicIcon == null) return;
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF6));
        MicButton.Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
        MicIcon.Text = "🎙️";
        StopPulse();
        UpdateWaveform(0, 0, false);
    }

    public void SetRecording()
    {
        if (MicButton == null || MicIcon == null) return;
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x2B));
        MicButton.Stroke = new SolidColorBrush(Color.FromRgb(0xCD, 0x4C, 0x22));
        MicIcon.Text = "⏹️";
        MicIcon.Foreground = Brushes.White;
        StartPulse();
    }

    public void SetProcessing()
    {
        if (MicButton == null || MicIcon == null) return;
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
        MicButton.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x6A, 0xDF));
        MicIcon.Text = "⏳";
        MicIcon.Foreground = Brushes.White;
        StopPulse();
        UpdateWaveform(0, 0, false);
    }

    public void SetDone()
    {
        if (MicButton == null || MicIcon == null) return;
        MicButton.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        MicButton.Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0xB4, 0x4C));
        MicIcon.Text = "✅";
        StopPulse();
        UpdateWaveform(0, 0, false);
    }

    private void StartPulse()
    {
        if (PulseRing == null) return;

        StopPulse();

        // Ensure the pulse ring has a scale transform
        if (PulseRing.RenderTransform is not TransformGroup group)
        {
            group = new TransformGroup();
            group.Children.Add(new ScaleTransform(0.9, 0.9));
            PulseRing.RenderTransform = group;
            PulseRing.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        _pulseStoryboard = new Storyboard();
        _pulseStoryboard.Children.Add(new DoubleAnimation(0.9, 1.15, TimeSpan.FromMilliseconds(1200))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
        _pulseStoryboard.Children.Add(new DoubleAnimation(0.45, 0.0, TimeSpan.FromMilliseconds(1200))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

        Storyboard.SetTarget(_pulseStoryboard.Children[0], PulseRing);
        Storyboard.SetTargetProperty(_pulseStoryboard.Children[0], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)"));
        Storyboard.SetTarget(_pulseStoryboard.Children[1], PulseRing);
        Storyboard.SetTargetProperty(_pulseStoryboard.Children[1], new PropertyPath(OpacityProperty));

        _pulseStoryboard.Begin();
    }

    private void StopPulse()
    {
        _pulseStoryboard?.Stop();
        _pulseStoryboard = null;
        if (PulseRing != null) PulseRing.Opacity = 0;
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
