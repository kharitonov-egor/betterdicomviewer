using System.Globalization;
using System.Windows;
using System.Windows.Input;
using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Deidentification;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Segmentation;

namespace BetterDicomViewer.App.Viewer;

public sealed partial class ViewportControl
{
    private Vec3? _cursorWorld;
    private Annotation? _draft;
    private Point _downPoint;
    private Point _lastPoint;
    private MouseButton _dragButton;
    private Action<Point>? _onMove;
    private Action<Point>? _onUp;
    private bool _angleAwaitingThirdPoint;
    private Point? _hover;
    private readonly List<LabelEdit> _strokeEdits = [];

    public Vec3? CursorWorld
    {
        get => _cursorWorld;
        set => _cursorWorld = value;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = e.GetPosition(this);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        DrawOverlay();
    }

    private ModifierKeys _mods;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (PointerDown(e.GetPosition(this), e.ChangedButton, Keyboard.Modifiers, e.ClickCount)) e.Handled = true;
    }

    /// <summary>Pointer entry points shared by real mouse input and the --script automation.</summary>
    public bool PointerDown(Point p, MouseButton button, ModifierKeys mods, int clickCount = 1)
    {
        _host.Activate(this);
        _mods = mods;
        if (clickCount == 2 && button == MouseButton.Left)
        {
            if (Model.Is3D) _ = PickIn3D(p);
            else if (_draft is null) _host.ToggleMaximize(this);
            return true;
        }
        if (_onMove is not null && !_angleAwaitingThirdPoint) return false;
        if (!Model.HasContent) return false;

        _downPoint = p;
        _lastPoint = p;
        _dragButton = button;

        if (_angleAwaitingThirdPoint && button == MouseButton.Left)
        {
            CommitDraft();
            _angleAwaitingThirdPoint = false;
            return true;
        }

        if (Model.Is3D) BeginDrag3D(button, mods);
        else if (button == MouseButton.Middle || (button == MouseButton.Left && mods == ModifierKeys.Control)) BeginPan();
        else if (button == MouseButton.Right && mods == ModifierKeys.Control) BeginZoom();
        else if (button == MouseButton.Right) BeginWindowLevel();
        else if (button == MouseButton.Left && mods == ModifierKeys.Shift && _host.ActiveTool is not (ToolKind.Brush or ToolKind.Eraser)) BeginZoom();
        else if (button == MouseButton.Left) BeginTool(p);

        if (_onMove is null) return false;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed) CaptureMouse();
        return true;
    }

    public void PointerMove(Point p)
    {
        _hover = p;
        if (_onMove is not null)
        {
            _onMove(p);
            _lastPoint = p;
        }
        else
        {
            ReportHoverValue(p);
            if (_host.ActiveTool is ToolKind.Brush or ToolKind.Eraser) DrawOverlay();
        }
    }

    public bool PointerUp(Point p, MouseButton button)
    {
        if (_onMove is null || button != _dragButton) return false;
        var up = _onUp;
        up?.Invoke(p);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!_angleAwaitingThirdPoint)
        {
            _onMove = null;
            _onUp = null;
        }
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMove(e.GetPosition(this));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (PointerUp(e.GetPosition(this), e.ChangedButton)) e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_angleAwaitingThirdPoint) return;
        _onMove = null;
        _onUp = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (PointerWheel(e.GetPosition(this), e.Delta, Keyboard.Modifiers)) e.Handled = true;
    }

    public bool PointerWheel(Point p, int delta, ModifierKeys mods)
    {
        if (!Model.HasContent) return false;
        _host.Activate(this);
        var notches = delta / 120.0;
        if (Model.Is3D)
        {
            Model.Vr.Zoom *= Math.Pow(1.12, notches);
            RequestRender();
        }
        else if (mods == ModifierKeys.Control)
        {
            Model.View.ZoomAt(p.X * DpiScale, p.Y * DpiScale, Math.Pow(1.15, notches));
            _host.OnViewChanged(this, ViewChange.Camera);
        }
        else if (mods == ModifierKeys.Shift && _host.ActiveTool is ToolKind.Brush or ToolKind.Eraser)
        {
            _host.Segmentation.BrushRadiusMm = Math.Clamp(_host.Segmentation.BrushRadiusMm * Math.Pow(1.15, notches), 0.5, 100);
            _host.ShowStatus(string.Create(CultureInfo.InvariantCulture, $"Brush radius {_host.Segmentation.BrushRadiusMm:0.#} mm"));
            DrawOverlay();
        }
        else
        {
            var step = mods == ModifierKeys.Shift ? 5 : 1;
            Model.ScrollBy(-Math.Sign(delta) * step);
            _host.OnViewChanged(this, ViewChange.Slice);
        }
        return true;
    }

    private void Drag(Action<Point> move, Action<Point>? up = null)
    {
        _onMove = move;
        _onUp = up;
    }

    private void BeginPan() => Drag(p =>
    {
        var d = p - _lastPoint;
        Model.View.Pan(d.X * DpiScale, d.Y * DpiScale);
        _host.OnViewChanged(this, ViewChange.Camera);
    });

    private void BeginZoom()
    {
        var anchor = _downPoint;
        Drag(p =>
        {
            var dy = p.Y - _lastPoint.Y;
            Model.View.ZoomAt(anchor.X * DpiScale, anchor.Y * DpiScale, Math.Exp(-dy * 0.01));
            _host.OnViewChanged(this, ViewChange.Camera);
        });
    }

    private void BeginWindowLevel()
    {
        if (Model.Set?.IsColor == true) return;
        var range = Model.Volume?.ValueRange ?? (0, 4096);
        var span = Math.Max(range.Item2 - range.Item1, 1) * Model.Display.ValueFactor;
        var sensitivity = Math.Max(span / 1500.0, Model.Display.ValueFactor < 0.01 ? 0.01 : 0.5);
        Drag(p =>
        {
            var d = p - _lastPoint;
            Model.Display.Width = Math.Max(Model.Display.ValueFactor < 1 ? 0.01 : 1, Model.Display.Width + d.X * sensitivity * 2);
            Model.Display.Center -= d.Y * sensitivity;
            _host.OnViewChanged(this, ViewChange.Window);
        });
    }

    private void BeginScroll()
    {
        var accumulated = 0.0;
        Drag(p =>
        {
            accumulated += p.Y - _lastPoint.Y;
            var steps = (int)(accumulated / 4);
            if (steps == 0) return;
            accumulated -= steps * 4;
            Model.ScrollBy(steps);
            _host.OnViewChanged(this, ViewChange.Slice);
        });
    }

    private void BeginRotate()
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        Drag(p =>
        {
            var a0 = Math.Atan2(_lastPoint.Y - c.Y, _lastPoint.X - c.X);
            var a1 = Math.Atan2(p.Y - c.Y, p.X - c.X);
            var center = Model.View.Center;
            Model.View.RotateInPlane(-(a1 - a0));
            Model.View.Center = center;
            _host.OnViewChanged(this, ViewChange.Camera);
        });
    }

    private void BeginDrag3D(MouseButton button, ModifierKeys mods)
    {
        _lastVrInteraction = DateTime.UtcNow;
        _vrInteractive = true;
        Action<Point> move = button switch
        {
            MouseButton.Middle => p =>
            {
                var d = p - _lastPoint;
                var right = Vec3.Cross(Model.Vr.Direction, Model.Vr.Up).Normalized();
                var scale = (Model.Volume?.Source.Geometry?.DiagonalMm ?? 300) / Math.Max(ActualHeight, 1) / Model.Vr.Zoom;
                Model.Vr.Pan -= right * (d.X * scale) - Model.Vr.Up * (d.Y * scale);
                RequestRender();
            },
            MouseButton.Right => p =>
            {
                Model.Vr.Zoom *= Math.Exp(-(p.Y - _lastPoint.Y) * 0.01);
                RequestRender();
            },
            _ when mods == ModifierKeys.Control => p =>
            {
                Model.Vr.Roll((p.X - _lastPoint.X) * 0.01);
                RequestRender();
            },
            _ => p =>
            {
                var d = p - _lastPoint;
                Model.Vr.Orbit(d.X, d.Y);
                RequestRender();
            },
        };
        Drag(p =>
        {
            _lastVrInteraction = DateTime.UtcNow;
            move(p);
        }, _ => EndVrInteraction());
    }

    private async Task PickIn3D(Point p)
    {
        if (_vr is null) return;
        var s = DpiScale;
        var (w, h) = DevicePixelSize();
        var world = await _vr.PickAsync((int)(p.X * s), (int)(p.Y * s), w, h);
        if (world is { } pt)
        {
            _host.SetCursorPoint(this, pt);
            _host.ShowStatus(string.Create(CultureInfo.InvariantCulture, $"Moved linked views to {pt.X:0.0}, {pt.Y:0.0}, {pt.Z:0.0} mm"));
        }
        else
        {
            _host.ShowStatus("Nothing visible under the cursor.");
        }
    }

    private void BeginTool(Point p)
    {
        // Editing an existing annotation takes priority over creating a new one.
        if (_host.ActiveTool is not (ToolKind.Brush or ToolKind.Eraser or ToolKind.RegionGrow or ToolKind.Crosshair) && TryBeginAnnotationEdit(p)) return;

        switch (_host.ActiveTool)
        {
            case ToolKind.Scroll:
                BeginScroll();
                break;
            case ToolKind.WindowLevel:
                BeginWindowLevel();
                break;
            case ToolKind.Pan:
                BeginPan();
                break;
            case ToolKind.Zoom:
                BeginZoom();
                break;
            case ToolKind.Rotate:
                BeginRotate();
                break;
            case ToolKind.Crosshair:
                BeginCrosshair(p);
                break;
            case ToolKind.Length:
            case ToolKind.Arrow:
            case ToolKind.Ellipse:
            case ToolKind.Rectangle:
                BeginTwoPoint(p);
                break;
            case ToolKind.Angle:
                BeginAngle(p);
                break;
            case ToolKind.Freehand:
                BeginFreehand(p);
                break;
            case ToolKind.Text:
                _ = AddTextAnnotation(p);
                break;
            case ToolKind.Probe:
                AddProbe(p);
                break;
            case ToolKind.Brush:
            case ToolKind.Eraser:
                BeginBrush(p, erase: _host.ActiveTool == ToolKind.Eraser || _mods == ModifierKeys.Shift);
                break;
            case ToolKind.RegionGrow:
                _ = RegionGrow(p);
                break;
            case ToolKind.Redact:
                BeginRedact(p);
                break;
        }
    }

    // ---------- Measurements ----------

    private Annotation NewAnnotation(AnnotationKind kind, Vec3 first)
    {
        var set = Model.Set!;
        return new Annotation
        {
            Kind = kind,
            StudyInstanceUid = set.Series.StudyInstanceUid,
            SeriesInstanceUid = set.Series.SeriesInstanceUid,
            DisplaySetId = set.Id,
            FrameOfReferenceUid = Model.FrameOfReferenceUid,
            PatientSpace = Model.Volume is not null,
            FrameIndex = Model.Volume is null ? Model.FrameIndex : null,
            Points = [first],
            PlaneRight = Model.View.Right,
            PlaneDown = Model.View.Down,
        };
    }

    private AnnotationKind KindFor(ToolKind tool) => tool switch
    {
        ToolKind.Length => AnnotationKind.Length,
        ToolKind.Arrow => AnnotationKind.Arrow,
        ToolKind.Ellipse => AnnotationKind.Ellipse,
        ToolKind.Rectangle => AnnotationKind.Rectangle,
        ToolKind.Angle => AnnotationKind.Angle,
        ToolKind.Freehand => AnnotationKind.Freehand,
        ToolKind.Text => AnnotationKind.Text,
        _ => AnnotationKind.Probe,
    };

    private void BeginTwoPoint(Point p)
    {
        var start = FromDip(p);
        _draft = NewAnnotation(KindFor(_host.ActiveTool), start);
        _draft.Points.Add(start);
        Drag(q =>
        {
            _draft!.Points[1] = FromDip(q);
            DrawOverlay();
        }, async q =>
        {
            if ((q - _downPoint).Length < 3)
            {
                _draft = null;
                DrawOverlay();
                return;
            }
            if (_draft!.Kind == AnnotationKind.Arrow)
            {
                var text = await _host.PromptTextAsync("Arrow label (optional)", "");
                if (_draft is null) return;
                _draft.Text = string.IsNullOrWhiteSpace(text) ? null : text;
            }
            CommitDraft();
        });
    }

    private void BeginAngle(Point p)
    {
        var start = FromDip(p);
        _draft = NewAnnotation(AnnotationKind.Angle, start);
        _draft.Points.Add(start);
        Drag(q =>
        {
            if (!_angleAwaitingThirdPoint) _draft!.Points[1] = FromDip(q);
            else _draft!.Points[2] = FromDip(q);
            DrawOverlay();
        }, q =>
        {
            if ((q - _downPoint).Length < 3 && !_angleAwaitingThirdPoint)
            {
                _draft = null;
                DrawOverlay();
                return;
            }
            // Keep tracking the mouse for the third point until the next click.
            _draft!.Points.Add(FromDip(q));
            _angleAwaitingThirdPoint = true;
            _host.ShowStatus("Click to place the end of the second arm. Esc cancels.");
        });
    }

    private void BeginFreehand(Point p)
    {
        _draft = NewAnnotation(AnnotationKind.Freehand, FromDip(p));
        var last = p;
        Drag(q =>
        {
            if ((q - last).Length < 2.5) return;
            _draft!.Points.Add(FromDip(q));
            last = q;
            DrawOverlay();
        }, _ =>
        {
            if (_draft!.Points.Count < 3)
            {
                _draft = null;
                DrawOverlay();
                return;
            }
            CommitDraft();
        });
    }

    private async Task AddTextAnnotation(Point p)
    {
        var text = await _host.PromptTextAsync("Annotation text", "");
        if (string.IsNullOrWhiteSpace(text) || !Model.HasContent) return;
        var a = NewAnnotation(AnnotationKind.Text, FromDip(p));
        a.Text = text;
        _draft = a;
        CommitDraft();
    }

    private void AddProbe(Point p)
    {
        _draft = NewAnnotation(AnnotationKind.Probe, FromDip(p));
        CommitDraft();
    }

    private void CommitDraft()
    {
        if (_draft is null) return;
        var a = _draft;
        _draft = null;
        _onMove = null;
        _onUp = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        a.Label = _host.Annotations.NextLabel(a.StudyInstanceUid, a.Kind);
        var store = _host.Annotations;
        store.Add(a);
        _host.SelectedAnnotation = a;
        _host.Undo.Push(new DelegateAction($"Add {Annotation.KindName(a.Kind)}", () => store.Remove(a), () => store.Add(a)));
        _host.OnAnnotationsChanged();
    }

    public void CancelDraft()
    {
        _draft = null;
        _angleAwaitingThirdPoint = false;
        _onMove = null;
        _onUp = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        DrawOverlay();
    }

    private static IEnumerable<Vec3> HandlePoints(Annotation a)
    {
        if (a.Kind is AnnotationKind.Ellipse or AnnotationKind.Rectangle && a.Points.Count >= 2)
        {
            var (u0, v0) = a.ToPlane(a.Points[0]);
            var (u1, v1) = a.ToPlane(a.Points[1]);
            yield return a.FromPlane(u0, v0);
            yield return a.FromPlane(u1, v0);
            yield return a.FromPlane(u1, v1);
            yield return a.FromPlane(u0, v1);
            yield break;
        }
        if (a.Kind == AnnotationKind.Freehand) yield break;
        foreach (var p in a.Points) yield return p;
    }

    private bool TryBeginAnnotationEdit(Point p)
    {
        const double handleRadius = 7;
        foreach (var a in VisibleAnnotations().Reverse())
        {
            var handles = HandlePoints(a).ToList();
            for (var i = 0; i < handles.Count; i++)
            {
                if ((ToDip(handles[i]) - p).Length > handleRadius) continue;
                BeginHandleDrag(a, i);
                return true;
            }
        }
        foreach (var a in VisibleAnnotations().Reverse())
        {
            if (!HitsBody(a, p)) continue;
            _host.SelectedAnnotation = a;
            BeginMoveAnnotation(a);
            _host.OnAnnotationsChanged();
            return true;
        }
        if (_host.SelectedAnnotation is not null)
        {
            _host.SelectedAnnotation = null;
            _host.OnAnnotationsChanged();
        }
        return false;
    }

    private bool HitsBody(Annotation a, Point p)
    {
        List<Point> poly;
        bool closed;
        if (a.IsRoi)
        {
            poly = a.Outline().Select(ToDip).ToList();
            closed = true;
        }
        else
        {
            poly = a.Points.Select(ToDip).ToList();
            closed = false;
        }
        if (poly.Count == 1) return (poly[0] - p).Length < 8;
        for (var i = 0; i < poly.Count - (closed ? 0 : 1); i++)
        {
            var q0 = poly[i];
            var q1 = poly[(i + 1) % poly.Count];
            if (DistanceToSegment(p, q0, q1) < 5) return true;
        }
        return false;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var t = ab.LengthSquared < 1e-9 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    private void BeginHandleDrag(Annotation a, int handle)
    {
        var before = a.Clone();
        _host.SelectedAnnotation = a;
        var rectLike = a.Kind is AnnotationKind.Ellipse or AnnotationKind.Rectangle;
        if (rectLike)
        {
            var corners = HandlePoints(a).ToList();
            a.Points[0] = corners[(handle + 2) % 4];
            a.Points[1] = corners[handle];
        }
        Drag(q =>
        {
            var w = FromDip(q);
            if (rectLike) a.Points[1] = w;
            else a.Points[handle] = w;
            MeasurementCache.Invalidate(a.Id);
            DrawOverlay();
        }, _ => CommitEdit(a, before));
    }

    private void BeginMoveAnnotation(Annotation a)
    {
        var before = a.Clone();
        Drag(q =>
        {
            var delta = FromDip(q) - FromDip(_lastPoint);
            for (var i = 0; i < a.Points.Count; i++) a.Points[i] += delta;
            MeasurementCache.Invalidate(a.Id);
            DrawOverlay();
        }, q =>
        {
            if ((q - _downPoint).Length < 1) return;
            CommitEdit(a, before);
        });
    }

    private void CommitEdit(Annotation a, Annotation before)
    {
        var after = a.Clone();
        var store = _host.Annotations;
        store.Replace(a);
        _host.Undo.Push(new DelegateAction($"Edit {a.Label}",
            () => { store.Replace(before.Clone()); _host.SelectedAnnotation = null; MeasurementCache.Invalidate(a.Id); },
            () => { store.Replace(after.Clone()); MeasurementCache.Invalidate(a.Id); }));
        _host.OnAnnotationsChanged();
    }

    // ---------- Crosshair ----------

    private void BeginCrosshair(Point p)
    {
        if (Model.Volume is null) return;
        var rotateTarget = FindRotationHandle(p);
        if (rotateTarget is { } target)
        {
            var pivot = _cursorWorld ?? Model.View.Center;
            var pivotDip = ToDip(pivot);
            Drag(q =>
            {
                var a0 = Math.Atan2(_lastPoint.Y - pivotDip.Y, _lastPoint.X - pivotDip.X);
                var a1 = Math.Atan2(q.Y - pivotDip.Y, q.X - pivotDip.X);
                var angle = a1 - a0;
                var axis = Model.View.Normal;
                var tv = target.Model.View;
                var right = Vec3.Rotate(tv.Right, axis, -angle);
                var down = Vec3.Rotate(tv.Down, axis, -angle);
                var centerOffset = Vec3.Rotate(tv.Center - pivot, axis, -angle);
                tv.SetPlane(right, down);
                tv.Center = pivot + centerOffset;
                target.RequestRender();
                _host.OnViewChanged(this, ViewChange.Overlay);
            });
            return;
        }
        void Move(Point q)
        {
            var w = FromDip(q);
            _host.SetCursorPoint(this, w);
        }
        Move(p);
        Drag(Move);
    }

    /// <summary>Screen position (DIP) of one rotation handle for <paramref name="target"/>'s plane, and the pivot.</summary>
    public (Point? Handle, Point Pivot) RotationHandleFor(ViewportControl target)
    {
        var pivotWorld = _cursorWorld ?? Model.View.Center;
        var pivot = ToDip(pivotWorld);
        var dir = Vec3.Cross(Model.View.Normal, target.Model.View.Normal);
        if (dir.Length < 1e-3) return (null, pivot);
        var a = ToDip(pivotWorld - dir.Normalized() * 100);
        var b = ToDip(pivotWorld + dir.Normalized() * 100);
        var sd = b - a;
        sd.Normalize();
        return (pivot + sd * Math.Min(ActualWidth, ActualHeight) * 0.38, pivot);
    }

    private ViewportControl? FindRotationHandle(Point p)
    {
        if (_cursorWorld is null) return null;
        var center = ToDip(_cursorWorld.Value);
        var r = Math.Min(ActualWidth, ActualHeight) * 0.38;
        foreach (var other in _host.Viewports)
        {
            if (ReferenceEquals(other, this) || other.Model.Volume is null || other.Model.Is3D) continue;
            if (other.Model.FrameOfReferenceUid != Model.FrameOfReferenceUid) continue;
            var dir = Vec3.Cross(Model.View.Normal, other.Model.View.Normal);
            if (dir.Length < 1e-3) continue;
            var a = ToDip(_cursorWorld.Value - dir.Normalized() * 100);
            var b = ToDip(_cursorWorld.Value + dir.Normalized() * 100);
            var sd = b - a;
            sd.Normalize();
            foreach (var sgn in new[] { -1.0, 1.0 })
                if ((center + sd * r * sgn - p).Length < 9) return other;
        }
        return null;
    }

    // ---------- Segmentation ----------

    private void BeginBrush(Point p, bool erase)
    {
        if (Model.Volume is null)
        {
            _host.ShowStatus("Segmentation needs a volume (a stack of parallel slices).");
            return;
        }
        var map = Model.Labels ??= _host.Images.GetOrCreateLabels(Model.Volume);
        if (map.ActiveLabel == 0 && map.Segments.Count > 0) map.ActiveLabel = map.Segments[0].Label;
        if (map.ActiveLabel == 0) map.AddSegment();
        Model.ShowLabels = true;
        _strokeEdits.Clear();
        PaintAt(p, erase, map);
        Drag(q =>
        {
            var from = _lastPoint;
            var dist = (q - from).Length;
            var stepDip = Math.Max(1, _host.Segmentation.BrushRadiusMm / (Model.View.Scale * DpiScale) / 3);
            var steps = Math.Max(1, (int)(dist / stepDip));
            for (var i = 1; i <= steps; i++) PaintAt(from + (q - from) * i / steps, erase, map);
            RequestRender();
            _host.OnSegmentationChanged(this);
        }, _ => CommitStroke(map, erase ? "Erase" : "Paint"));
        RequestRender();
    }

    private void PaintAt(Point p, bool erase, LabelMap map)
    {
        var s = _host.Segmentation;
        var center = FromDip(p);
        var volume = Model.Volume!;
        Func<long, bool>? constraint = null;
        if (s.UseThreshold && !erase)
        {
            var lo = s.ThresholdLow / Math.Max(Model.Display.ValueFactor, 1e-12);
            var hi = s.ThresholdHigh / Math.Max(Model.Display.ValueFactor, 1e-12);
            var data = volume.Data;
            constraint = idx =>
            {
                var x = (int)(idx % data.Nx);
                var y = (int)(idx / data.Nx % data.Ny);
                var z = (int)(idx / ((long)data.Nx * data.Ny));
                var v = data.GetValue(x, y, z);
                return v >= lo && v <= hi;
            };
        }
        var normal = s.Sphere ? Vec3.Zero : Model.View.Normal;
        var half = Math.Max(Model.SliceStep() / 2, 0.01) * 0.999;
        var edit = map.Paint(center, s.BrushRadiusMm, normal, half, erase ? (byte)0 : map.ActiveLabel, constraint, erase ? map.ActiveLabel : null);
        if (edit is not null) _strokeEdits.Add(edit);
    }

    private void CommitStroke(LabelMap map, string verb)
    {
        if (_strokeEdits.Count == 0) return;
        var edits = _strokeEdits.ToList();
        _strokeEdits.Clear();
        _host.Undo.Push(new DelegateAction($"{verb} segment",
            () => { for (var i = edits.Count - 1; i >= 0; i--) map.Apply(edits[i], undo: true); RefreshLabelsEverywhere(); },
            () => { foreach (var e in edits) map.Apply(e, undo: false); RefreshLabelsEverywhere(); }));
        _host.OnSegmentationChanged(this);
    }

    private void RefreshLabelsEverywhere() => _host.OnSegmentationChanged(this);

    private async Task RegionGrow(Point p)
    {
        if (Model.Volume is not { } volume)
        {
            _host.ShowStatus("Region growing needs a volume.");
            return;
        }
        var map = Model.Labels ??= _host.Images.GetOrCreateLabels(volume);
        if (map.ActiveLabel == 0) map.AddSegment();
        Model.ShowLabels = true;
        var s = _host.Segmentation;
        var factor = Math.Max(Model.Display.ValueFactor, 1e-12);
        var lo = s.ThresholdLow / factor;
        var hi = s.ThresholdHigh / factor;
        var seed = FromDip(p);
        var seedIdx = volume.WorldToIndexPoint(seed);
        int? onlySlice = s.GrowIn3D ? null : (int)Math.Round(seedIdx.Z);
        _host.ShowStatus("Growing region…");
        var label = map.ActiveLabel;
        var (edit, truncated) = await Task.Run(() =>
        {
            var e = map.RegionGrow(seed, lo, hi, volume.Data, label, s.MaxGrowVoxels, onlySlice, out var t);
            return (e, t);
        });
        if (edit is null)
        {
            _host.ShowStatus(string.Create(CultureInfo.InvariantCulture, $"Seed value is outside the threshold range {s.ThresholdLow:0.##} to {s.ThresholdHigh:0.##}. Adjust it in the Segmentation panel."));
            return;
        }
        _host.Undo.Push(new DelegateAction("Region grow", () => { map.Apply(edit, true); RefreshLabelsEverywhere(); }, () => { map.Apply(edit, false); RefreshLabelsEverywhere(); }));
        _host.ShowStatus(string.Create(CultureInfo.InvariantCulture,
            $"Region grow added {edit.Count:N0} voxels ({edit.Count * map.VoxelVolumeMm3 / 1000:0.##} mL){(truncated ? " and stopped at the voxel limit" : "")}."));
        _host.OnSegmentationChanged(this);
    }

    // ---------- Redaction regions (de-identification) ----------

    private void BeginRedact(Point p)
    {
        if (Model.Volume is not null || Model.Set is null)
        {
            _host.ShowStatus("Redaction rectangles apply to 2D images such as ultrasound and screen captures.");
            return;
        }
        var key = DeidentificationOptions.SizeKey(Model.Set.Columns, Model.Set.Rows);
        var start = Model.IndexToWorld.Inverse().TransformPoint(FromDip(p));
        _draft = NewAnnotation(AnnotationKind.Rectangle, FromDip(p));
        _draft.Points.Add(FromDip(p));
        Drag(q =>
        {
            _draft!.Points[1] = FromDip(q);
            DrawOverlay();
        }, q =>
        {
            var end = Model.IndexToWorld.Inverse().TransformPoint(FromDip(q));
            _draft = null;
            var x0 = (int)Math.Floor(Math.Min(start.X, end.X) + 0.5);
            var y0 = (int)Math.Floor(Math.Min(start.Y, end.Y) + 0.5);
            var x1 = (int)Math.Ceiling(Math.Max(start.X, end.X) + 0.5);
            var y1 = (int)Math.Ceiling(Math.Max(start.Y, end.Y) + 0.5);
            if (x1 - x0 < 2 || y1 - y0 < 2)
            {
                DrawOverlay();
                return;
            }
            var region = new RedactionRegion(Math.Max(0, x0), Math.Max(0, y0), Math.Min(Model.Set.Columns, x1) - Math.Max(0, x0), Math.Min(Model.Set.Rows, y1) - Math.Max(0, y0));
            if (!_host.Redactions.TryGetValue(key, out var list)) _host.Redactions[key] = list = [];
            list.Add(region);
            _host.Undo.Push(new DelegateAction("Add redaction region", () => { list.Remove(region); _host.OnAnnotationsChanged(); }, () => { list.Add(region); _host.OnAnnotationsChanged(); }));
            _host.ShowStatus($"Redaction region set for all {Model.Set.Columns}×{Model.Set.Rows} images. It is applied when exporting with \"Redact\".");
            _host.OnAnnotationsChanged();
        });
    }

    private void DrawToolPreview(System.Windows.Media.DrawingContext dc)
    {
        if (_hover is not { } h || Model.Volume is null) return;
        if (_host.ActiveTool is not (ToolKind.Brush or ToolKind.Eraser)) return;
        var r = _host.Segmentation.BrushRadiusMm / (Model.View.Scale * DpiScale);
        var color = _host.ActiveTool == ToolKind.Eraser ? System.Windows.Media.Colors.White : System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0xFF);
        dc.DrawEllipse(null, new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(color), 1.2), h, r, r);
    }

    private void ReportHoverValue(Point p)
    {
        if (!Model.HasContent || Model.Is3D) return;
        var world = FromDip(p);
        string text;
        if (Model.Volume is { } v)
        {
            var value = v.SampleNearest(world);
            text = string.Create(CultureInfo.InvariantCulture, $"{world.X:0.0}, {world.Y:0.0}, {world.Z:0.0} mm");
            if (value is { } val)
            {
                text += string.Create(CultureInfo.InvariantCulture, $"   {val:0.##} {v.ValueUnit}");
                var k = (int)Math.Round(v.WorldToIndexPoint(world).Z);
                if (v.Suv?.Factor(k) is { } f) text += string.Create(CultureInfo.InvariantCulture, $"   SUV {val * f:0.00}");
            }
            if (Model.Fusion is { } fu && fu.Pet.SampleNearest(world) is { } pv)
            {
                var k = (int)Math.Round(fu.Pet.WorldToIndexPoint(world).Z);
                text += fu.Pet.Suv?.Factor(k) is { } pf
                    ? string.Create(CultureInfo.InvariantCulture, $"   PET SUV {pv * pf:0.00}")
                    : string.Create(CultureInfo.InvariantCulture, $"   PET {pv:0.#} {fu.Pet.ValueUnit}");
            }
        }
        else if (Model.Frames is { } frames && frames.TryGet(Model.FrameIndex) is { } buffer)
        {
            var idx = frames.IndexToWorld.Inverse().TransformPoint(world);
            var x = (int)Math.Round(idx.X);
            var y = (int)Math.Round(idx.Y);
            if (x < 0 || y < 0 || x >= buffer.Nx || y >= buffer.Ny)
            {
                _host.ReportHover("");
                return;
            }
            text = $"x {x}, y {y}";
            if (buffer.Components >= 3)
            {
                unsafe
                {
                    var b = (byte*)buffer.Pointer + (y * buffer.Nx + x) * 3;
                    text += $"   RGB {b[0]}, {b[1]}, {b[2]}";
                }
            }
            else
            {
                text += string.Create(CultureInfo.InvariantCulture, $"   {buffer.GetValue(x, y, 0):0.##} {frames.ValueUnit}");
            }
        }
        else
        {
            text = "";
        }
        _host.ReportHover(text);
    }
}
