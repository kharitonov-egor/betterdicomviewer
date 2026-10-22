using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Viewing;

namespace BetterDicomViewer.App.Viewer;

public sealed partial class ViewportControl
{
    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)));
    private static readonly Brush DimTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)));
    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0)));
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x10, 0x10)));
    private static readonly Brush AnnotationBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly Brush SelectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));
    private static readonly Brush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC3, 0x4D)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush RedactBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x40, 0x40)));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.Open();
        DrawOverlayContent(dc, forExport: false);
    }

    private Point ToDip(Vec3 world)
    {
        var (x, y, _) = Model.View.WorldToScreen(world);
        var s = DpiScale;
        return new Point(x / s, y / s);
    }

    private Vec3 FromDip(Point p)
    {
        var s = DpiScale;
        return Model.View.ScreenToWorld(p.X * s, p.Y * s);
    }

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiFaceBold : UiFace, size, brush, Ppd);

    private static readonly Pen OutlinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)), 3) { LineJoin = PenLineJoin.Round });

    /// <summary>Text with a dark outline so it stays legible on bright images (inverted PET, radiographs).</summary>
    private void DrawTextShadowed(DrawingContext dc, string text, Point at, double size = 12, Brush? brush = null, bool bold = false, TextAlignment align = TextAlignment.Left)
    {
        var main = Text(text, size, brush ?? TextBrush, bold);
        main.TextAlignment = align;
        var geometry = main.BuildGeometry(at);
        dc.DrawGeometry(null, OutlinePen, geometry);
        dc.DrawGeometry(brush ?? TextBrush, null, geometry);
    }

    private void DrawLines(DrawingContext dc, IEnumerable<string> lines, Point origin, bool alignRight, bool fromBottom, Brush? brush = null)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        const double lineHeight = 16;
        for (var i = 0; i < list.Count; i++)
        {
            var y = fromBottom ? origin.Y - (list.Count - i) * lineHeight : origin.Y + i * lineHeight;
            DrawTextShadowed(dc, list[i], new Point(origin.X, y), 12, i == 0 && !fromBottom ? TextBrush : brush ?? TextBrush, i == 0 && !fromBottom, alignRight ? TextAlignment.Right : TextAlignment.Left);
        }
    }

    private void DrawOverlayContent(DrawingContext dc, bool forExport)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (Model.Set is null)
        {
            if (!forExport)
            {
                DrawTextShadowed(dc, "Double-click a series in the library, or drag it here", new Point(w / 2, h / 2 - 10), 13, DimTextBrush, false, TextAlignment.Center);
            }
            return;
        }

        var inst = Model.Set.Representative;
        var privacy = _host.PrivacyMode;
        var compact = w < 640 || h < 420;

        // Top left: patient and series.
        var topLeft = new List<string>();
        if (compact)
        {
        }
        else if (!privacy)
        {
            topLeft.Add(PersonName(inst.PatientName));
            topLeft.Add(string.Join("  ", new[] { inst.PatientId, DicomDates.FormatDate(inst.PatientBirthDate), inst.PatientSex }.Where(s => !string.IsNullOrWhiteSpace(s))));
        }
        else
        {
            topLeft.Add("Patient details hidden");
        }
        topLeft.Add($"{inst.Modality} · {(string.IsNullOrWhiteSpace(inst.SeriesDescription) ? "Series " + inst.SeriesNumber : inst.SeriesDescription)}");
        topLeft.Add(Model.Set.Label);
        DrawLines(dc, topLeft, new Point(10, 8), false, false);

        // Top right: study.
        var topRight = new List<string>();
        if (!privacy && !compact) topRight.Add(inst.StudyDescription ?? "");
        if (!privacy) topRight.Add(DicomDates.FormatDate(inst.StudyDate));
        if (!privacy && !compact && !string.IsNullOrWhiteSpace(inst.Manufacturer)) topRight.Add(inst.Manufacturer!);
        DrawLines(dc, topRight, new Point(w - 10, 8), true, false, DimTextBrush);

        // Orientation badge.
        var badge = Model.PlaneLabel;
        if (!string.IsNullOrEmpty(badge))
        {
            var color = new SolidColorBrush(Model.OrientationColor);
            var t = Text(badge, 11, Brushes.Black, true);
            var rect = new Rect(w - t.Width - 22, h - 28, t.Width + 12, 20);
            dc.DrawRoundedRectangle(color, null, rect, 4, 4);
            dc.DrawText(t, new Point(rect.X + 6, rect.Y + 2));
        }

        if (Model.Is3D)
        {
            DrawOverlay3D(dc, w, h, forExport);
            DrawMessages(dc, w, h);
            return;
        }

        // Bottom left: slice and geometry.
        var bottomLeft = new List<string>();
        var (k, count) = Model.SliceIndex();
        bottomLeft.Add(Model.Volume is null && Model.Set.Kind == DisplaySetKind.Frames && count > 1
            ? $"Frame {k + 1} / {count}" + (Model.CinePlaying ? $"  ▶ {Model.CineFps:0} fps" : "")
            : $"Image {k + 1} / {count}");
        if (Model.Volume is { } vol)
        {
            var pos = Vec3.Dot(Model.View.Center, Model.View.Normal);
            var thickness = Model.View.SlabMode != SlabMode.None && Model.View.SlabThicknessMm > 0
                ? $"{Model.View.SlabMode.ToString().ToUpperInvariant()} slab {Model.View.SlabThicknessMm:0.#} mm"
                : $"Thickness {inst.SliceThickness?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"} mm";
            bottomLeft.Add(string.Create(CultureInfo.InvariantCulture, $"Position {pos:0.0} mm · {thickness}"));
            if (!vol.IsComplete) bottomLeft.Add(string.Create(CultureInfo.InvariantCulture, $"Loading {vol.Progress * 100:0}%"));
        }
        DrawLines(dc, bottomLeft, new Point(10, h - 6), false, true);

        // Bottom right: display.
        var bottomRight = new List<string>();
        if (Model.Set.IsColor) bottomRight.Add("Colour image");
        else if (Model.Volume?.Suv is { IsAvailable: true } && Model.Display.ValueFactor != 1)
            bottomRight.Add(string.Create(CultureInfo.InvariantCulture, $"SUV {Model.Display.Center - Model.Display.Width / 2:0.0}–{Model.Display.Center + Model.Display.Width / 2:0.0}"));
        else
        {
            var fmt = Model.Display.Width >= 20 ? "0" : "0.##";
            bottomRight.Add($"W {Model.Display.Width.ToString(fmt, CultureInfo.InvariantCulture)}  L {Model.Display.Center.ToString(fmt, CultureInfo.InvariantCulture)}" + (Model.Display.Invert ? "  inverted" : ""));
        }
        var zoom = PixelZoomPercent();
        bottomRight.Add(string.Create(CultureInfo.InvariantCulture, $"Zoom {zoom:0}% · {(Model.Interpolation == Imaging.Interpolation.Nearest ? "nearest" : Model.Interpolation == Imaging.Interpolation.Cubic ? "cubic" : "linear")}"));
        if (Model.Fusion is { Visible: true } fu)
            bottomRight.Add(string.Create(CultureInfo.InvariantCulture, $"Fusion {fu.Opacity * 100:0}% · {fu.Unit} {fu.Lower:0.#}–{fu.Upper:0.#}"));
        DrawLines(dc, bottomRight, new Point(w - 10, h - 30), true, true, DimTextBrush);

        DrawOrientationLabels(dc, w, h);
        DrawScaleBar(dc, w, h);
        if (!forExport || true) DrawReferenceLines(dc, w, h, forExport);
        DrawRedactions(dc);
        DrawAnnotations(dc, forExport);
        if (!forExport) DrawToolPreview(dc);
        DrawMessages(dc, w, h);
    }

    private static string PersonName(string? pn) => string.IsNullOrWhiteSpace(pn) ? "Unnamed patient" : pn.Replace('^', ' ').Trim();

    private double PixelZoomPercent()
    {
        if (Model.Volume is { } v)
        {
            var spacing = Math.Min(v.Spacing.X, v.Spacing.Y);
            return spacing / Model.View.Scale * 100 / DpiScale;
        }
        return 1 / Model.View.Scale * 100 / DpiScale;
    }

    private void DrawOrientationLabels(DrawingContext dc, double w, double h)
    {
        (string L, string T, string R, string B)? labels = null;
        if (Model.Volume is not null)
        {
            labels = OrientationLabels.ForView(Model.View.Right, Model.View.Down);
        }
        else if (Model.Set?.Frames[Model.FrameIndex].Instance is { } inst)
        {
            Vec3? row = null, col = null;
            if (inst.ImageOrientationPatient is { } iop)
            {
                row = new Vec3(iop[0], iop[1], iop[2]);
                col = new Vec3(iop[3], iop[4], iop[5]);
            }
            else if (OrientationLabels.FromPatientOrientation(inst.PatientOrientation) is { } po)
            {
                row = po.Row;
                col = po.Column;
            }
            if (row is { } r && col is { } c)
            {
                // Screen axes expressed in image index axes, then in patient directions.
                var right = Model.View.Right;
                var down = Model.View.Down;
                labels = OrientationLabels.ForView(r * right.X + c * right.Y, r * down.X + c * down.Y);
            }
        }
        if (labels is not { } l) return;
        DrawTextShadowed(dc, l.L, new Point(8, h / 2 - 9), 14, TextBrush, true);
        DrawTextShadowed(dc, l.R, new Point(w - 8, h / 2 - 9), 14, TextBrush, true, TextAlignment.Right);
        DrawTextShadowed(dc, l.T, new Point(w / 2, w < 640 || h < 420 ? 44 : 70), 14, TextBrush, true, TextAlignment.Center);
        DrawTextShadowed(dc, l.B, new Point(w / 2, h - 26 - 4), 14, TextBrush, true, TextAlignment.Center);
    }

    private void DrawScaleBar(DrawingContext dc, double w, double h)
    {
        double mmPerDip;
        if (Model.Volume is not null) mmPerDip = Model.View.Scale * DpiScale;
        else if (Model.Frames?.Calibration is { HasUniformSpacing: true } cal) mmPerDip = Model.View.Scale * DpiScale * cal.ColumnSpacingMm!.Value;
        else return;
        var target = Math.Min(h * 0.25, h / 2 - 90) * mmPerDip;
        var nice = new[] { 1.0, 2, 5, 10, 20, 50, 100, 200, 500 }.LastOrDefault(v => v <= target);
        if (nice <= 0) return;
        var len = nice / mmPerDip;
        var x = w - 14;
        var y0 = h / 2 + 28;
        var pen = new Pen(TextBrush, 1);
        var shadow = new Pen(ShadowBrush, 3);
        dc.DrawLine(shadow, new Point(x, y0), new Point(x, y0 + len));
        dc.DrawLine(pen, new Point(x, y0), new Point(x, y0 + len));
        for (var i = 0; i <= 10; i++)
        {
            var y = y0 + len * i / 10;
            var tick = i % 5 == 0 ? 6 : 3;
            dc.DrawLine(pen, new Point(x - tick, y), new Point(x, y));
        }
        DrawTextShadowed(dc, nice >= 10 ? $"{nice / 10:0.#} cm" : $"{nice:0} mm", new Point(x - 8, y0 + len + 2), 11, DimTextBrush, false, TextAlignment.Right);
    }

    private void DrawReferenceLines(DrawingContext dc, double w, double h, bool forExport)
    {
        if (!_host.ShowReferenceLines || Model.Volume is null) return;
        var n1 = Model.View.Normal;
        var c1 = Model.View.Center;
        var crosshair = _host.ActiveTool == ToolKind.Crosshair && !forExport;
        foreach (var other in _host.Viewports)
        {
            if (ReferenceEquals(other, this) || other.Model.Volume is null || other.Model.Is3D) continue;
            if (other.Model.FrameOfReferenceUid != Model.FrameOfReferenceUid || Model.FrameOfReferenceUid is null) continue;
            var n2 = other.Model.View.Normal;
            var dir = Vec3.Cross(n1, n2);
            if (dir.Length < 1e-3) continue;
            dir = dir.Normalized();
            // A point on both planes: solve with the least-squares formula for two planes.
            var d1 = Vec3.Dot(n1, c1);
            var d2 = Vec3.Dot(n2, other.Model.View.Center);
            var n12 = Vec3.Dot(n1, n2);
            var det = 1 - n12 * n12;
            var a = (d1 - d2 * n12) / det;
            var b = (d2 - d1 * n12) / det;
            var p = n1 * a + n2 * b;
            var pa = ToDip(p - dir * 5000);
            var pb = ToDip(p + dir * 5000);
            var color = other.Model.OrientationColor;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, color.R, color.G, color.B)), crosshair ? 1.4 : 1.0);
            if (!crosshair) pen.DashStyle = new DashStyle([6, 4], 0);
            dc.DrawLine(pen, pa, pb);
            if (crosshair)
            {
                var center = ToDip(_cursorWorld ?? p);
                var screenDir = pb - pa;
                screenDir.Normalize();
                var r = Math.Min(w, h) * 0.38;
                foreach (var sgn in new[] { -1.0, 1.0 })
                {
                    var hp = center + screenDir * r * sgn;
                    dc.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.Black, 1), hp, 4.5, 4.5);
                }
            }
        }
        if (crosshair && _cursorWorld is { } cw)
        {
            var cp = ToDip(cw);
            dc.DrawEllipse(null, new Pen(TextBrush, 1), cp, 3, 3);
        }
    }

    private void DrawRedactions(DrawingContext dc)
    {
        if (Model.Volume is not null || Model.Set is null) return;
        if (!_host.Redactions.TryGetValue(Core.Deidentification.DeidentificationOptions.SizeKey(Model.Set.Columns, Model.Set.Rows), out var regions)) return;
        var pen = new Pen(RedactBrush, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
        var fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        foreach (var r in regions)
        {
            var a = ToDip(Model.IndexToWorld.TransformPoint(new Vec3(r.X - 0.5, r.Y - 0.5, 0)));
            var b = ToDip(Model.IndexToWorld.TransformPoint(new Vec3(r.X + r.Width - 0.5, r.Y + r.Height - 0.5, 0)));
            var rect = new Rect(a, b);
            dc.DrawRectangle(fill, pen, rect);
            DrawTextShadowed(dc, "Redact on export", new Point(rect.X + 4, rect.Y + 2), 11, RedactBrush);
        }
    }

    private void DrawMessages(DrawingContext dc, double w, double h)
    {
        var y = h / 2 + 20;
        if (_renderError is not null)
        {
            DrawTextShadowed(dc, _renderError, new Point(w / 2, y), 13, ErrorBrush, false, TextAlignment.Center);
            y += 20;
        }
        if (Model.Message is not null)
        {
            DrawTextShadowed(dc, Model.Message, new Point(w / 2, y), 13, WarningBrush, false, TextAlignment.Center);
        }
        if (Model.Volume is { IsComplete: false } v)
        {
            var barW = Math.Min(240, w * 0.5);
            var rect = new Rect(w / 2 - barW / 2, h - 46, barW, 4);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), null, rect, 2, 2);
            dc.DrawRoundedRectangle((Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], null, new Rect(rect.X, rect.Y, rect.Width * v.Progress, rect.Height), 2, 2);
        }
        var warnings = Model.Volume?.Warnings ?? Model.Set?.Warnings;
        if (warnings is { Count: > 0 } && !Model.Is3D)
        {
            var text = "⚠ " + warnings[0] + (warnings.Count > 1 ? $" (+{warnings.Count - 1} more in Image panel)" : "");
            var ft = Text(text, 11, WarningBrush);
            ft.MaxTextWidth = Math.Max(100, w - 240);
            ft.MaxLineCount = 2;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(10, h - 62 - (ft.Height > 16 ? 14 : 0)));
        }
    }

    private void DrawOverlay3D(DrawingContext dc, double w, double h, bool forExport)
    {
        var vr = Model.Vr;
        var mode = vr.Mode switch
        {
            VrMode.Mip => "Maximum intensity projection",
            VrMode.MinIp => "Minimum intensity projection",
            VrMode.Average => "Average projection",
            _ => vr.PresetName,
        };
        var lines = new List<string> { mode };
        if (!forExport) lines.Add(string.Create(CultureInfo.InvariantCulture, $"{vr.LastRenderMs:0} ms per frame"));
        DrawLines(dc, lines, new Point(10, h - 6), false, true, DimTextBrush);
        var dir = vr.Direction;
        DrawTextShadowed(dc, "View from " + OrientationLabels.ForDirection(-dir), new Point(w - 10, h - 50), 11, DimTextBrush, false, TextAlignment.Right);
    }

    private bool IsVisibleHere(Annotation a)
    {
        if (Model.Set is null || Model.Is3D) return false;
        if (Model.Volume is null)
            return a.DisplaySetId == Model.Set.Id && a.FrameIndex == Model.FrameIndex;
        if (!a.PatientSpace) return false;
        var sameSpace = a.DisplaySetId == Model.Set.Id || (a.FrameOfReferenceUid is not null && a.FrameOfReferenceUid == Model.FrameOfReferenceUid);
        if (!sameSpace) return false;
        if (Math.Abs(Vec3.Dot(a.PlaneNormal, Model.View.Normal)) < 0.995) return false;
        var tolerance = Math.Max(Model.SliceStep() / 2, 0.25);
        return Math.Abs(Vec3.Dot(a.Points[0] - Model.View.Center, Model.View.Normal)) <= tolerance;
    }

    public IEnumerable<Annotation> VisibleAnnotations()
    {
        if (Model.Set is null) return [];
        return _host.Annotations.ForStudy(Model.Set.Series.StudyInstanceUid).Where(IsVisibleHere);
    }

    private void DrawAnnotations(DrawingContext dc, bool forExport)
    {
        foreach (var a in VisibleAnnotations()) DrawAnnotation(dc, a, ReferenceEquals(_host.SelectedAnnotation, a) || _host.SelectedAnnotation?.Id == a.Id, draft: false, forExport);
        if (_draft is not null && !forExport) DrawAnnotation(dc, _draft, false, draft: true, forExport);
    }

    private void DrawAnnotation(DrawingContext dc, Annotation a, bool selected, bool draft, bool forExport)
    {
        var brush = selected ? SelectedBrush : AnnotationBrush;
        var pen = new Pen(brush, selected ? 2 : 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (draft) pen.DashStyle = new DashStyle([4, 3], 0);
        var shadow = new Pen(ShadowBrush, pen.Thickness + 2) { LineJoin = PenLineJoin.Round };
        var pts = a.Points.Select(ToDip).ToList();
        if (pts.Count == 0) return;

        void Line(Point p, Point q)
        {
            dc.DrawLine(shadow, p, q);
            dc.DrawLine(pen, p, q);
        }

        switch (a.Kind)
        {
            case AnnotationKind.Length when pts.Count >= 2:
                Line(pts[0], pts[1]);
                DrawEndTick(dc, pen, pts[0], pts[1]);
                DrawEndTick(dc, pen, pts[1], pts[0]);
                break;
            case AnnotationKind.Angle:
                for (var i = 0; i + 1 < pts.Count; i++) Line(pts[i], pts[i + 1]);
                break;
            case AnnotationKind.Arrow when pts.Count >= 2:
            {
                Line(pts[0], pts[1]);
                var dir = pts[1] - pts[0];
                if (dir.Length > 1)
                {
                    dir.Normalize();
                    var normal = new Vector(-dir.Y, dir.X);
                    var tip = pts[1];
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(tip, true, true);
                        g.LineTo(tip - dir * 12 + normal * 5, true, false);
                        g.LineTo(tip - dir * 12 - normal * 5, true, false);
                    }
                    dc.DrawGeometry(brush, shadow, geo);
                }
                break;
            }
            case AnnotationKind.Probe:
                dc.DrawEllipse(null, shadow, pts[0], 4, 4);
                dc.DrawEllipse(null, pen, pts[0], 4, 4);
                break;
            case AnnotationKind.Text:
                break;
            default:
                if (a.IsRoi && a.Points.Count >= 2)
                {
                    var outline = a.Outline().Select(ToDip).ToList();
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(outline[0], false, a.Kind != AnnotationKind.Freehand || !draft);
                        g.PolyLineTo(outline.Skip(1).ToList(), true, true);
                    }
                    dc.DrawGeometry(null, shadow, geo);
                    dc.DrawGeometry(null, pen, geo);
                }
                break;
        }

        if (selected && !forExport)
        {
            foreach (var p in HandlePoints(a).Select(ToDip))
                dc.DrawRectangle(Brushes.White, new Pen(SelectedBrush, 1.5), new Rect(p.X - 4, p.Y - 4, 8, 8));
        }

        // Label with results.
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(a.Label) && a.Kind != AnnotationKind.Text) lines.Add(a.Label);
        if (!string.IsNullOrEmpty(a.Text)) lines.Add(a.Text!);
        if (!draft || a.Kind is AnnotationKind.Length or AnnotationKind.Angle)
        {
            var result = _host is { } host ? ResultFor(a) : null;
            if (result is not null) lines.AddRange(result.Lines());
        }
        if (lines.Count == 0) return;
        var anchor = LabelAnchor(a, pts);
        DrawLabelBox(dc, lines, anchor, brush, a.Kind == AnnotationKind.Text);
    }

    private static void DrawEndTick(DrawingContext dc, Pen pen, Point at, Point other)
    {
        var d = other - at;
        if (d.Length < 1) return;
        d.Normalize();
        var n = new Vector(-d.Y, d.X) * 5;
        dc.DrawLine(pen, at - n, at + n);
    }

    private Point LabelAnchor(Annotation a, List<Point> pts)
    {
        if (a.IsRoi)
        {
            var outline = a.Outline().Select(ToDip).ToList();
            return new Point(outline.Max(p => p.X) + 10, outline.Min(p => p.Y));
        }
        var last = a.Kind == AnnotationKind.Angle && pts.Count >= 2 ? pts[1] : pts[^1];
        return new Point(last.X + 10, last.Y + 6);
    }

    private void DrawLabelBox(DrawingContext dc, List<string> lines, Point at, Brush accent, bool plain)
    {
        var texts = lines.Select((l, i) => Text(l, 12, i == 0 && !plain ? accent : TextBrush, i == 0 && !plain)).ToList();
        var width = texts.Max(t => t.WidthIncludingTrailingWhitespace) + 12;
        var height = texts.Sum(t => t.Height) + 8;
        var x = Math.Min(at.X, ActualWidth - width - 4);
        var y = Math.Min(Math.Max(at.Y, 4), ActualHeight - height - 4);
        dc.DrawRoundedRectangle(LabelBackground, null, new Rect(x, y, width, height), 4, 4);
        var cy = y + 4;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(x + 6, cy));
            cy += t.Height;
        }
    }

    public MeasurementResult? ResultFor(Annotation a)
    {
        var ctx = MeasurementContextFor(a);
        return ctx is null ? null : MeasurementCache.Get(a, ctx);
    }

    public MeasurementContext? MeasurementContextFor(Annotation a)
    {
        if (Model.Volume is { } v)
        {
            MeasurementContext? secondary = null;
            if (Model.Fusion?.Pet is { } pet)
            {
                secondary = new MeasurementContext
                {
                    Data = pet.Data,
                    IndexToWorld = pet.IndexToWorld,
                    PatientSpace = true,
                    ValueUnit = pet.ValueUnit,
                    Suv = pet.Suv,
                    IsSliceLoaded = pet.IsSliceLoaded,
                };
            }
            return new MeasurementContext
            {
                Data = v.Data,
                IndexToWorld = v.IndexToWorld,
                PatientSpace = true,
                ValueUnit = v.ValueUnit,
                Suv = v.Suv,
                IsSliceLoaded = v.IsSliceLoaded,
                Secondary = secondary,
            };
        }
        if (Model.Frames is { } frames && a.FrameIndex is { } f)
        {
            var buffer = frames.TryGet(f);
            if (buffer is null) return null;
            return new MeasurementContext
            {
                Data = buffer,
                IndexToWorld = frames.IndexToWorld,
                PatientSpace = false,
                ValueUnit = frames.ValueUnit,
                Calibration = FrameCalibration.For(Model.Set!.Frames[f].Instance, Model.Set.Frames[f].FrameIndex),
            };
        }
        return null;
    }
}

/// <summary>Caches measurement results; ROI statistics are only recomputed when the annotation or data changes.</summary>
public static class MeasurementCache
{
    private static readonly Dictionary<Guid, (string Key, MeasurementResult Result)> Cache = [];

    public static MeasurementResult Get(Annotation a, MeasurementContext ctx)
    {
        var key = string.Join(";", a.Points.Select(p => p.ToString())) + $"|{a.Kind}|{ctx.Data.Version}|{ctx.Secondary?.Data.Version}|{ctx.Suv?.IsAvailable}";
        if (Cache.TryGetValue(a.Id, out var hit) && hit.Key == key) return hit.Result;
        var result = MeasurementCalculator.Compute(a, ctx);
        Cache[a.Id] = (key, result);
        return result;
    }

    public static void Invalidate(Guid id) => Cache.Remove(id);
}
