using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ViviTune.Ui;

// Мини-график скорости: линия и полупрозрачная заливка под ней, масштаб по максимуму.
// Последнее значение у правого края; недостающие слева, пустые данные и одни нули — ровная линия у низа.
public sealed class Sparkline : Control
{
    public const int Slots = ViewState.HistoryMax;
    const double Thickness = 1.5, Headroom = 0.12; // доля высоты над пиком

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    readonly double[] levels = new double[Slots];
    IPen? pen;
    IBrush? fill;

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    // Уровни 0..1 по точкам графика: значения прижаты вправо, лишние старые отброшены,
    // отрицательные, NaN и бесконечность — 0, максимум видимых — 1.
    public static void Levels(IReadOnlyList<double>? values, Span<double> levels)
    {
        int n = values?.Count ?? 0, len = levels.Length;
        int pad = Math.Max(0, len - n), skip = Math.Max(0, n - len);
        double max = 0;
        levels.Clear();
        for (int i = pad; i < len; i++)
        {
            double v = values![skip + i - pad];
            levels[i] = double.IsFinite(v) && v > 0 ? v : 0;
            max = Math.Max(max, levels[i]);
        }
        if (max > 0)
            for (int i = pad; i < len; i++) levels[i] /= max;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValuesProperty)
        {
            // коллекция живёт столько же, сколько окно (ViewState): отписка нужна только при замене
            if (change.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= OnValuesChanged;
            if (change.NewValue is INotifyCollectionChanged n) n.CollectionChanged += OnValuesChanged;
        }
        else if (change.Property == StrokeProperty)
        {
            var brush = Stroke;
            pen = brush == null ? null : new Pen(brush, Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            fill = brush is ISolidColorBrush s
                ? new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(77, s.Color.R, s.Color.G, s.Color.B), 0),
                        new GradientStop(Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B), 1)
                    }
                }
                : null;
        }
    }

    void OnValuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (pen == null || w <= 0 || h <= 0) return;
        Levels(Values, levels);
        double bottom = h - Thickness / 2, top = h * Headroom, step = w / (Slots - 1);
        Point At(int i) => new(i * step, bottom - levels[i] * (bottom - top));

        if (fill != null)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < Slots; i++) g.LineTo(At(i));
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false);
            for (int i = 1; i < Slots; i++) g.LineTo(At(i));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, line);
    }
}
