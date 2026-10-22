using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.App.Viewer;

public enum ToolKind
{
    Scroll,
    WindowLevel,
    Pan,
    Zoom,
    Rotate,
    Crosshair,
    Length,
    Angle,
    Ellipse,
    Rectangle,
    Freehand,
    Arrow,
    Text,
    Probe,
    Brush,
    Eraser,
    RegionGrow,
    Redact,
}

public enum ViewChange
{
    Slice,
    Camera,
    Window,
    Content,
    Overlay,
}

public sealed class SegmentationSettings
{
    public double BrushRadiusMm { get; set; } = 6;
    public bool Sphere { get; set; }
    public bool UseThreshold { get; set; }
    public double ThresholdLow { get; set; } = -100;
    public double ThresholdHigh { get; set; } = 300;
    public bool GrowIn3D { get; set; } = true;
    public int MaxGrowVoxels { get; set; } = 20_000_000;
}

/// <summary>Services a viewport needs from the application (tools, shared stores, linking).</summary>
public interface IViewerHost
{
    ToolKind ActiveTool { get; }
    AnnotationStore Annotations { get; }
    UndoStack Undo { get; }
    ImageStore Images { get; }
    IReadOnlyList<ViewportControl> Viewports { get; }
    ViewportControl? ActiveViewport { get; }
    bool PrivacyMode { get; }
    bool LinkEnabled { get; }
    bool ShowReferenceLines { get; }
    SegmentationSettings Segmentation { get; }
    Annotation? SelectedAnnotation { get; set; }
    Dictionary<string, List<Core.Deidentification.RedactionRegion>> Redactions { get; }

    void Activate(ViewportControl viewport);
    void OnViewChanged(ViewportControl source, ViewChange change);
    void OnAnnotationsChanged();
    void OnSegmentationChanged(ViewportControl source);
    void ReportHover(string text);
    void ShowStatus(string text);
    void ToggleMaximize(ViewportControl viewport);
    Task<string?> PromptTextAsync(string title, string initial);
    void SetCursorPoint(ViewportControl source, Vec3 point);
}
