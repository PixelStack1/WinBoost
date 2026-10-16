using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinBoost.Controls;

/// <summary>
/// Circular Fluent gauge drawn with a Path arc: starts exactly at 12 o'clock (-90°), runs clockwise, round caps,
/// track and value share the same centre and thickness. Values animate smoothly (~300 ms ease-out).
/// </summary>
public sealed class Gauge : Grid
{
    private const double Thickness = 10;
    private const double MinSweep = 8;   // degrees: tiny non-zero values still show a short, clean arc
    private readonly double _size, _radius, _center;
    private readonly Path _arc;
    private readonly TextBlock _value, _unit, _label, _sub;
    private double _shown, _from, _target;
    private DateTime _animStart;
    private DispatcherQueueTimer? _anim;

    public Gauge(double size = 112)
    {
        _size = size; _center = size / 2; _radius = (size - Thickness) / 2;
        HorizontalAlignment = HorizontalAlignment.Center;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var canvas = new Grid { Width = size, Height = size };
        canvas.Children.Add(new Ellipse
        {
            Width = size - Thickness, Height = size - Thickness, StrokeThickness = Thickness,
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        _arc = new Path
        {
            StrokeThickness = Thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Stroke = AccentBrush(), Width = size, Height = size, Visibility = Visibility.Collapsed
        };
        canvas.Children.Add(_arc);
        _value = new TextBlock { FontSize = size * 0.24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _unit = new TextBlock { FontSize = 12, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center };
        canvas.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { _value, _unit } });
        Children.Add(canvas);

        _label = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        _sub = new TextBlock { FontSize = 12, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        SetRow(_label, 1); SetRow(_sub, 2);
        Children.Add(_label); Children.Add(_sub);
        Unloaded += (_, _) => { try { _anim?.Stop(); } catch { } };
    }

    private static Brush AccentBrush() =>
        Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b) && b is Brush br ? br
        : new SolidColorBrush(ColorHelper.FromArgb(255, 0x7C, 0x4D, 0xFF));

    public string Label { set => _label.Text = value; }
    public string Sub { set => _sub.Text = value; }

    public void SetPercent(double? v, string na)
    {
        if (v is null) { ShowNa(na); return; }
        _arc.Stroke = AccentBrush();
        AnimateTo(v.Value);
        _value.Text = $"{v.Value:0}"; _unit.Text = "%";
    }

    /// <summary>Temperature: green &lt;65 °C, orange 65–80, red &gt;80. Scale 0–100 °C.</summary>
    public void SetTemperature(double? c, string na)
    {
        if (c is null) { ShowNa(na); return; }
        _arc.Stroke = new SolidColorBrush(c.Value < 65 ? ColorHelper.FromArgb(255, 0x2E, 0xC2, 0x7E)
            : c.Value <= 80 ? ColorHelper.FromArgb(255, 0xF5, 0xA6, 0x23) : ColorHelper.FromArgb(255, 0xE8, 0x48, 0x55));
        AnimateTo(c.Value);
        _value.Text = $"{c.Value:0}"; _unit.Text = "°C";
    }

    private void ShowNa(string na) { AnimateTo(0); _value.Text = na; _unit.Text = ""; }

    private void AnimateTo(double target)
    {
        _target = Math.Clamp(target, 0, 100);
        _from = _shown; _animStart = DateTime.UtcNow;
        try
        {
            if (_anim is null)
            {
                _anim = DispatcherQueue.CreateTimer();
                _anim.Interval = TimeSpan.FromMilliseconds(16);
                _anim.Tick += (_, _) => Step();
            }
            _anim.Start();
        }
        catch { _shown = _target; Draw(); }
    }

    private void Step()
    {
        double t = Math.Min(1, (DateTime.UtcNow - _animStart).TotalMilliseconds / 300.0);
        double ease = 1 - Math.Pow(1 - t, 3);
        _shown = _from + (_target - _from) * ease;
        Draw();
        if (t >= 1) _anim?.Stop();
    }

    private Point At(double deg)
    {
        double rad = deg * Math.PI / 180; // 0° = 12 o'clock, clockwise
        return new Point(_center + _radius * Math.Sin(rad), _center - _radius * Math.Cos(rad));
    }

    private void Draw()
    {
        if (_shown < 0.5) { _arc.Visibility = Visibility.Collapsed; return; }
        double sweep = Math.Clamp(_shown * 3.6, MinSweep, 359.99);
        Geometry geo;
        if (sweep >= 359.9)
            geo = new EllipseGeometry { Center = new Point(_center, _center), RadiusX = _radius, RadiusY = _radius };
        else
        {
            var fig = new PathFigure { StartPoint = At(0), IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = At(sweep), Size = new Size(_radius, _radius),
                SweepDirection = SweepDirection.Clockwise, IsLargeArc = sweep > 180
            });
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            geo = pg;
        }
        _arc.Data = geo;
        _arc.Visibility = Visibility.Visible;
    }
}
