using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BetterDicomViewer.App.Services;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Deidentification;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Library;

namespace BetterDicomViewer.App;

public partial class MainWindow : Window, IViewerHost
{
    private readonly List<ViewportControl> _pool = [];
    private readonly List<ViewportControl> _visible = [];
    private readonly CommandRegistry _commands = new();
    private readonly DispatcherTimer _cineTimer = new(DispatcherPriority.Render);
    private readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<ToolKind, System.Windows.Controls.Primitives.ToggleButton> _toolButtons = [];
    private ViewportControl? _active;
    private ViewportControl? _maximized;
    private ToolKind _tool = ToolKind.Scroll;
    private Annotation? _selected;
    private bool _privacy;
    private bool _link = true;
    private bool _referenceLines = true;
    private TaskCompletionSource<string?>? _prompt;

    public MainWindow()
    {
        InitializeComponent();
        Library = new DicomLibrary();
        Library.Changed += (_, _) => Dispatcher.BeginInvoke(ScheduleLibraryRefresh, DispatcherPriority.Background);
        Annotations.Changed += _ => Dispatcher.BeginInvoke(RefreshMeasurementList);
        Undo.Changed += () => Dispatcher.BeginInvoke(CommandManager.InvalidateRequerySuggested);

        for (var i = 0; i < 4; i++)
        {
            var vc = new ViewportControl(this, i) { AllowDrop = true };
            vc.Drop += OnViewportDrop;
            vc.DragOver += OnViewportDragOver;
            vc.Model.Changed += _ => { };
            _pool.Add(vc);
        }

        RegisterCommands();
        BuildToolbar();
        InitializeInspector();
        ApplyLayout(LayoutKind.Single);
        Activate(_pool[0]);

        _cineTimer.Tick += OnCineTick;
        _perfTimer.Tick += (_, _) => UpdatePerf();
        _perfTimer.Start();

        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnWindowDrop;
        Closing += (_, _) => SaveDirtySegmentations();
        Closed += (_, _) =>
        {
            foreach (var v in _pool) v.Dispose();
        };
        RefreshLibrary();
        SetStatus("Ready. Open a folder (Ctrl+O) or press F1 for shortcuts.");
    }

    public DicomLibrary Library { get; }
    internal CommandRegistry Commands => _commands;
    public AnnotationStore Annotations { get; } = new();
    public UndoStack Undo { get; } = new();
    public ImageStore Images { get; } = new();
    public SegmentationSettings Segmentation { get; } = new();
    public Dictionary<string, List<RedactionRegion>> Redactions { get; } = [];
    public ToolKind ActiveTool => _tool;
    public IReadOnlyList<ViewportControl> Viewports => _visible;
    public ViewportControl? ActiveViewport => _active;
    public bool PrivacyMode => _privacy;
    public bool LinkEnabled => _link;
    public bool ShowReferenceLines => _referenceLines;

    public Annotation? SelectedAnnotation
    {
        get => _selected;
        set
        {
            _selected = value;
            SyncMeasureSelection();
        }
    }

    public void Activate(ViewportControl viewport)
    {
        if (ReferenceEquals(_active, viewport)) return;
        if (_active is not null) _active.IsActive = false;
        _active = viewport;
        viewport.IsActive = true;
        RefreshInspector();
    }

    public void OnViewChanged(ViewportControl source, ViewChange change)
    {
        source.RequestRender();
        switch (change)
        {
            case ViewChange.Slice:
                PropagateSlice(source);
                break;
            case ViewChange.Window:
                PropagateWindow(source);
                if (ReferenceEquals(source, _active)) RefreshWindowBoxes();
                break;
            case ViewChange.Content:
                RefreshInspector();
                break;
        }
        foreach (var v in _visible)
            if (!ReferenceEquals(v, source)) v.RequestRender();
    }

    public void OnAnnotationsChanged()
    {
        foreach (var v in _visible) v.RequestRender();
        RefreshMeasurementList();
    }

    public void OnSegmentationChanged(ViewportControl source)
    {
        foreach (var v in _pool)
        {
            if (source.Model.Labels is { } labels && ReferenceEquals(v.Model.Volume, source.Model.Volume) && v.Model.Volume is not null)
            {
                v.Model.Labels = labels;
                v.Model.ShowLabels = true;
            }
            if (ReferenceEquals(v.Model.Labels, source.Model.Labels) || ReferenceEquals(v, source)) v.RequestRender();
        }
        ScheduleSegmentationSave();
        RefreshSegmentList();
    }

    public void ReportHover(string text) => HoverText.Text = text;

    public void ShowStatus(string text) => SetStatus(text);

    private void SetStatus(string text) => StatusText.Text = text;

    public void SetCursorPoint(ViewportControl source, Vec3 point)
    {
        foreach (var v in _visible)
        {
            v.CursorWorld = point;
            if (ReferenceEquals(v, source) || v.Model.Volume is null || v.Model.Is3D) continue;
            if (v.Model.FrameOfReferenceUid != source.Model.FrameOfReferenceUid && source.Model.FrameOfReferenceUid is not null && !source.Model.Is3D) continue;
            v.Model.MovePlaneThrough(point);
            v.RequestRender();
        }
        source.RequestRender();
    }

    public Task<string?> PromptTextAsync(string title, string initial)
    {
        _prompt?.TrySetResult(null);
        _prompt = new TaskCompletionSource<string?>();
        PromptTitle.Text = title;
        PromptBox.Text = initial;
        PromptOverlay.Visibility = Visibility.Visible;
        PromptBox.Focus();
        PromptBox.SelectAll();
        return _prompt.Task;
    }

    private void ClosePrompt(string? result)
    {
        PromptOverlay.Visibility = Visibility.Collapsed;
        var p = _prompt;
        _prompt = null;
        p?.TrySetResult(result);
        _active?.Focus();
    }

    private void OnPromptOk(object sender, RoutedEventArgs e) => ClosePrompt(PromptBox.Text);
    private void OnPromptCancel(object sender, RoutedEventArgs e) => ClosePrompt(null);

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ClosePrompt(PromptBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClosePrompt(null);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PromptOverlay.Visibility == Visibility.Visible) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var focus = Keyboard.FocusedElement;
        var typing = focus is TextBox or PasswordBox || focus is ComboBox { IsEditable: true };
        if (typing && (mods == ModifierKeys.None || mods == ModifierKeys.Shift) && key != Key.Escape && key is not (Key.F1 or Key.F11)) return;
        if (typing && mods == ModifierKeys.Control && key is Key.C or Key.V or Key.X or Key.A or Key.Z or Key.Y) return;
        if (focus is ListBox or ListBoxItem or TreeViewItem or TreeView or ComboBox && key is Key.Up or Key.Down or Key.Home or Key.End or Key.Prior or Key.Next && mods == ModifierKeys.None) return;
        if (focus is Slider && key is Key.Left or Key.Right or Key.Up or Key.Down) return;

        if (key == Key.Escape)
        {
            if (_active is not null) _active.CancelDraft();
            if (WindowStyle == WindowStyle.None) ToggleFullScreen();
            SelectedAnnotation = null;
            OnAnnotationsChanged();
            e.Handled = true;
            return;
        }
        if (_commands.TryHandle(key, mods)) e.Handled = true;
    }

    private void UpdatePerf()
    {
        var mem = Environment.WorkingSet / 1024.0 / 1024.0;
        var render = _active?.LastRenderMs ?? 0;
        var vr = _active?.Model.Is3D == true ? $" · 3D {_active.Model.Vr.LastRenderMs:0} ms" : "";
        PerfText.Text = string.Create(CultureInfo.InvariantCulture, $"Frame {render:0.0} ms{vr} · Memory {mem:0} MB · Volumes {Images.VolumeBytes / 1048576.0:0} MB");
    }

    public void SetTool(ToolKind tool)
    {
        _active?.CancelDraft();
        _tool = tool;
        foreach (var (kind, button) in _toolButtons) button.IsChecked = kind == tool;
        RedactToggle.IsChecked = tool == ToolKind.Redact;
        foreach (var v in _visible)
        {
            v.Cursor = tool switch
            {
                ToolKind.Pan => Cursors.SizeAll,
                ToolKind.Scroll => Cursors.SizeNS,
                ToolKind.Brush or ToolKind.Eraser => Cursors.None,
                _ => Cursors.Cross,
            };
            v.RequestRender();
        }
        SetStatus(ToolHint(tool));
        if (tool is ToolKind.Brush or ToolKind.Eraser or ToolKind.RegionGrow) SegmentSection.IsExpanded = true;
    }

    private static string ToolHint(ToolKind tool) => tool switch
    {
        ToolKind.Scroll => "Scroll: drag up or down to move through images.",
        ToolKind.WindowLevel => "Window/level: drag horizontally for width, vertically for level. Right-drag works with any tool.",
        ToolKind.Pan => "Pan: drag to move the image. Middle-drag works with any tool.",
        ToolKind.Zoom => "Zoom: drag up or down. Ctrl+wheel zooms at the cursor.",
        ToolKind.Rotate => "Rotate: drag around the centre to rotate the image in plane.",
        ToolKind.Crosshair => "Crosshair: drag to move linked views to a point. Drag a coloured line handle to rotate that plane (oblique).",
        ToolKind.Length => "Length: drag between two points.",
        ToolKind.Angle => "Angle: drag the first arm, release, then click the end of the second arm.",
        ToolKind.Ellipse => "Ellipse ROI: drag a bounding box. Shows area, mean, SD, min and max (and SUV for PET).",
        ToolKind.Rectangle => "Rectangle ROI: drag a box.",
        ToolKind.Freehand => "Freehand ROI: draw a closed outline.",
        ToolKind.Arrow => "Arrow: drag from tail to head, then type an optional label.",
        ToolKind.Text => "Text: click to place a note.",
        ToolKind.Probe => "Point: click to record the value at a point.",
        ToolKind.Brush => "Brush: paint the active segment. Shift+drag erases, Shift+wheel changes the size.",
        ToolKind.Eraser => "Eraser: remove the active segment.",
        ToolKind.RegionGrow => "Region grow: click a seed; connected voxels inside the intensity range join the active segment.",
        ToolKind.Redact => "Redaction region: drag a box over burned-in text; it is blacked out in de-identified exports of images this size.",
        _ => "",
    };

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
    }
}
