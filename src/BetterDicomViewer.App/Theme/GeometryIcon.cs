using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterDicomViewer.App.Theme;

/// <summary>Draws a 24x24 stroke icon geometry in the inherited foreground colour.</summary>
public sealed class GeometryIcon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(GeometryIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillDataProperty = DependencyProperty.Register(
        nameof(FillData), typeof(Geometry), typeof(GeometryIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GeometryIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(GeometryIcon), new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public GeometryIcon()
    {
        Width = 18;
        Height = 18;
        SnapsToDevicePixels = true;
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Geometry? FillData
    {
        get => (Geometry?)GetValue(FillDataProperty);
        set => SetValue(FillDataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        if (scale <= 0) return;
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (FillData is not null) dc.DrawGeometry(Foreground, null, FillData);
        if (Data is not null) dc.DrawGeometry(null, pen, Data);
        dc.Pop();
    }
}
