using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BetterDicomViewer.App.Services;
using BetterDicomViewer.App.Theme;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core.Viewing;
using static BetterDicomViewer.App.Services.CommandRegistry;

namespace BetterDicomViewer.App;

public partial class MainWindow
{
    private static readonly (ToolKind Tool, string Title, string Icon, Key Key, ModifierKeys Mods, string Group)[] ToolDefs =
    [
        (ToolKind.Scroll, "Scroll through images", "Icon.Scroll", Key.S, ModifierKeys.None, "nav"),
        (ToolKind.WindowLevel, "Window / level", "Icon.WindowLevel", Key.W, ModifierKeys.None, "nav"),
        (ToolKind.Pan, "Pan", "Icon.Pan", Key.P, ModifierKeys.None, "nav"),
        (ToolKind.Zoom, "Zoom", "Icon.Zoom", Key.Z, ModifierKeys.None, "nav"),
        (ToolKind.Rotate, "Rotate in plane", "Icon.Rotate", Key.O, ModifierKeys.None, "nav"),
        (ToolKind.Crosshair, "Crosshair and oblique planes", "Icon.Crosshair", Key.C, ModifierKeys.None, "nav"),
        (ToolKind.Length, "Length", "Icon.Length", Key.L, ModifierKeys.None, "measure"),
        (ToolKind.Angle, "Angle", "Icon.Angle", Key.A, ModifierKeys.None, "measure"),
        (ToolKind.Ellipse, "Ellipse ROI", "Icon.Ellipse", Key.E, ModifierKeys.None, "measure"),
        (ToolKind.Rectangle, "Rectangle ROI", "Icon.Rectangle", Key.Q, ModifierKeys.None, "measure"),
        (ToolKind.Freehand, "Freehand ROI", "Icon.Freehand", Key.F, ModifierKeys.None, "measure"),
        (ToolKind.Probe, "Point value", "Icon.Probe", Key.D, ModifierKeys.None, "measure"),
        (ToolKind.Arrow, "Arrow annotation", "Icon.Arrow", Key.K, ModifierKeys.None, "measure"),
        (ToolKind.Text, "Text annotation", "Icon.Text", Key.T, ModifierKeys.None, "measure"),
        (ToolKind.Brush, "Segmentation brush", "Icon.Brush", Key.B, ModifierKeys.None, "segment"),
        (ToolKind.Eraser, "Segmentation eraser", "Icon.Eraser", Key.X, ModifierKeys.None, "segment"),
        (ToolKind.RegionGrow, "Region grow", "Icon.RegionGrow", Key.G, ModifierKeys.None, "segment"),
    ];

    private void RegisterCommands()
    {
        var c = _commands;
        const string file = "File";
        const string layout = "Layout and views";
        const string image = "Image";
        const string nav = "Navigation";
        const string tools = "Tools";
        const string edit = "Edit";
        const string seg = "Segmentation";
        const string help = "Help";

        c.Add("open.folder", "Open folder…", file, OpenFolderDialog, K(Key.O, ModifierKeys.Control));
        c.Add("open.dicomdir", "Open DICOMDIR file…", file, OpenDicomDirDialog, K(Key.O, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("open.demo", "Open synthetic demo study", file, () => _ = OpenDemoAsync());
        c.Add("export.screenshot", "Save screenshot…", file, SaveScreenshot, K(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("export.copy", "Copy viewport image", file, CopyViewportImage, K(Key.C, ModifierKeys.Control));
        c.Add("export.measurements", "Export measurements…", file, () => ExportMeasurements(csv: true), K(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("export.deid", "Export de-identified copy…", file, ShowDeidentifyDialog, K(Key.D, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("export.segmentation", "Export segmentation…", file, ExportSegmentation, K(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("library.search", "Search the library", file, () => { SearchBox.Focus(); SearchBox.SelectAll(); }, K(Key.F, ModifierKeys.Control));

        c.Add("layout.1", "Layout: single viewport", layout, () => ApplyLayout(LayoutKind.Single), K(Key.D1, ModifierKeys.Control));
        c.Add("layout.2", "Layout: side by side (comparison)", layout, () => ApplyLayout(LayoutKind.SideBySide), K(Key.D2, ModifierKeys.Control));
        c.Add("layout.4", "Layout: 2 × 2", layout, () => ApplyLayout(LayoutKind.Grid2x2), K(Key.D3, ModifierKeys.Control));
        c.Add("layout.mpr", "Layout: MPR (axial, coronal, sagittal)", layout, () => ApplyMprLayout(with3D: false), K(Key.D4, ModifierKeys.Control));
        c.Add("layout.mpr3d", "Layout: MPR + 3D", layout, () => ApplyMprLayout(with3D: true), K(Key.D5, ModifierKeys.Control));
        c.Add("layout.petct", "Layout: PET/CT fusion", layout, () => _ = ApplyPetCtLayoutAsync(), K(Key.D6, ModifierKeys.Control));
        c.Add("view.maximize", "Maximize or restore the active viewport", layout, () => { if (_active is not null) ToggleMaximize(_active); }, K(Key.M));
        c.Add("view.nextViewport", "Next viewport", layout, NextViewport, K(Key.Tab, ModifierKeys.Control));
        c.Add("view.library", "Show or hide the library", layout, ToggleLibrary, K(Key.B, ModifierKeys.Control));
        c.Add("view.inspector", "Show or hide the inspector", layout, ToggleInspector, K(Key.I, ModifierKeys.Control));
        c.Add("view.fullscreen", "Full screen", layout, ToggleFullScreen, K(Key.F11));
        c.Add("view.privacy", "Hide or show patient details", layout, TogglePrivacy, K(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("view.reference", "Show or hide reference lines", layout, () => { _referenceLines = !_referenceLines; RenderAll(); }, K(Key.R, ModifierKeys.Control));
        c.Add("view.link", "Link scrolling between viewports", layout, ToggleLink, K(Key.L, ModifierKeys.Control));
        c.Add("plane.native", "Plane: native (as acquired)", layout, () => SetPlane(PlaneKind.Acquisition), K(Key.D1, ModifierKeys.Alt));
        c.Add("plane.axial", "Plane: axial", layout, () => SetPlane(PlaneKind.Axial), K(Key.D2, ModifierKeys.Alt));
        c.Add("plane.coronal", "Plane: coronal", layout, () => SetPlane(PlaneKind.Coronal), K(Key.D3, ModifierKeys.Alt));
        c.Add("plane.sagittal", "Plane: sagittal", layout, () => SetPlane(PlaneKind.Sagittal), K(Key.D4, ModifierKeys.Alt));
        c.Add("plane.3d", "3D volume rendering", layout, Toggle3D, K(Key.D5, ModifierKeys.Alt));

        c.Add("image.reset", "Reset view and window", image, ResetActiveView, K(Key.R));
        c.Add("image.fit", "Fit image to viewport", image, () => WithActive(v => { v.Model.Fit(); }), K(Key.D0));
        c.Add("image.zoomIn", "Zoom in", image, () => WithActive(v => v.Model.View.ZoomAt(v.Model.View.Width / 2.0, v.Model.View.Height / 2.0, 1.25)), K(Key.OemPlus), K(Key.Add));
        c.Add("image.zoomOut", "Zoom out", image, () => WithActive(v => v.Model.View.ZoomAt(v.Model.View.Width / 2.0, v.Model.View.Height / 2.0, 0.8)), K(Key.OemMinus), K(Key.Subtract));
        c.Add("image.invert", "Invert grey scale", image, () => WithActive(v => { v.Model.Display.Invert = !v.Model.Display.Invert; PropagateWindow(v); }), K(Key.I));
        c.Add("image.flipH", "Flip horizontally", image, () => WithActive(v => v.Model.View.FlipHorizontal()), K(Key.H));
        c.Add("image.flipV", "Flip vertically", image, () => WithActive(v => v.Model.View.FlipVertical()), K(Key.V));
        c.Add("image.rotL", "Rotate 90° left", image, () => WithActive(v => v.Model.View.RotateInPlane(-Math.PI / 2)), K(Key.OemOpenBrackets));
        c.Add("image.rotR", "Rotate 90° right", image, () => WithActive(v => v.Model.View.RotateInPlane(Math.PI / 2)), K(Key.OemCloseBrackets));
        c.Add("image.interp", "Cycle interpolation (nearest, linear, cubic)", image, CycleInterpolation, K(Key.N));
        c.Add("image.slab", "Cycle thick-slab mode (off, mean, MIP, MinIP)", image, CycleSlab, K(Key.M, ModifierKeys.Shift));
        c.Add("image.slabThicker", "Thicker slab", image, () => AdjustSlab(1.25), K(Key.OemCloseBrackets, ModifierKeys.Shift));
        c.Add("image.slabThinner", "Thinner slab", image, () => AdjustSlab(0.8), K(Key.OemOpenBrackets, ModifierKeys.Shift));
        c.Add("fusion.toggle", "Show or hide PET fusion", image, ToggleFusionVisibility, K(Key.F, ModifierKeys.Shift));
        c.Add("fusion.more", "More PET fusion opacity", image, () => AdjustFusion(0.1), K(Key.Up, ModifierKeys.Shift));
        c.Add("fusion.less", "Less PET fusion opacity", image, () => AdjustFusion(-0.1), K(Key.Down, ModifierKeys.Shift));
        for (var i = 1; i <= 9; i++)
        {
            var n = i;
            c.Add($"wl.{n}", $"Window preset {n}", image, () => ApplyWindowPreset(n), K(Key.D0 + n), K(Key.NumPad0 + n));
        }

        c.Add("slice.next", "Next image", nav, () => ScrollActive(1), K(Key.Down));
        c.Add("slice.prev", "Previous image", nav, () => ScrollActive(-1), K(Key.Up));
        c.Add("slice.next10", "Forward 10 images", nav, () => ScrollActive(10), K(Key.Next));
        c.Add("slice.prev10", "Back 10 images", nav, () => ScrollActive(-10), K(Key.Prior));
        c.Add("slice.first", "First image", nav, () => WithActive(v => { v.Model.GoToSlice(0); PropagateSlice(v); }), K(Key.Home));
        c.Add("slice.last", "Last image", nav, () => WithActive(v => { v.Model.GoToSlice(int.MaxValue); PropagateSlice(v); }), K(Key.End));
        c.Add("set.next", "Next image set or series", nav, () => StepSeries(1), K(Key.Right));
        c.Add("set.prev", "Previous image set or series", nav, () => StepSeries(-1), K(Key.Left));
        c.Add("cine.toggle", "Play or pause cine", nav, ToggleCine, K(Key.Space));

        foreach (var t in ToolDefs)
        {
            var tool = t.Tool;
            c.Add("tool." + tool, t.Title, t.Group == "segment" ? seg : tools, () => SetTool(tool), K(t.Key, t.Mods));
        }

        c.Add("edit.undo", "Undo", edit, () => Undo.Undo(), () => Undo.CanUndo, null, K(Key.Z, ModifierKeys.Control));
        c.Add("edit.redo", "Redo", edit, () => Undo.Redo(), () => Undo.CanRedo, null, K(Key.Y, ModifierKeys.Control), K(Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("edit.delete", "Delete selected measurement", edit, DeleteSelectedAnnotation, K(Key.Delete));
        c.Add("seg.new", "New segment", seg, AddSegment, K(Key.N, ModifierKeys.Control | ModifierKeys.Shift));
        c.Add("seg.bigger", "Larger brush", seg, () => ResizeBrush(1.2), K(Key.OemCloseBrackets, ModifierKeys.Control));
        c.Add("seg.smaller", "Smaller brush", seg, () => ResizeBrush(1 / 1.2), K(Key.OemOpenBrackets, ModifierKeys.Control));

        c.Add("help.shortcuts", "Keyboard and mouse shortcuts", help, ShowShortcuts, K(Key.F1), K(Key.OemQuestion, ModifierKeys.Shift));
        c.Add("help.palette", "Command palette", help, ShowCommandPalette, K(Key.K, ModifierKeys.Control), K(Key.P, ModifierKeys.Control | ModifierKeys.Shift));

        var h = c.Hints;
        h.Add(new("Mouse", "Left drag", "Use the active tool"));
        h.Add(new("Mouse", "Right drag", "Window / level (any tool)"));
        h.Add(new("Mouse", "Middle drag or Ctrl + left drag", "Pan"));
        h.Add(new("Mouse", "Ctrl + right drag, Shift + left drag", "Zoom"));
        h.Add(new("Mouse", "Ctrl + wheel", "Zoom at the cursor"));
        h.Add(new("Mouse", "Wheel", "Next or previous image"));
        h.Add(new("Mouse", "Shift + wheel", "Move 5 images (with the brush: change brush size)"));
        h.Add(new("Mouse", "Double-click", "Maximize or restore the viewport"));
        h.Add(new("Mouse", "Drag a series from the library", "Show it in a viewport"));
        h.Add(new("3D view", "Left drag", "Rotate"));
        h.Add(new("3D view", "Ctrl + left drag", "Roll"));
        h.Add(new("3D view", "Right drag or wheel", "Zoom"));
        h.Add(new("3D view", "Middle drag", "Pan"));
        h.Add(new("3D view", "Double-click", "Move linked 2D views to the structure under the cursor"));
        h.Add(new("Crosshair tool", "Drag", "Move all linked views to the point"));
        h.Add(new("Crosshair tool", "Drag a line handle", "Rotate that plane (oblique MPR)"));
        h.Add(new("Measurements", "Drag a handle", "Edit a measurement"));
        h.Add(new("Measurements", "Drag a line", "Move a measurement"));
        h.Add(new("Segmentation", "Shift + drag with the brush", "Erase"));
        h.Add(new("Any", "Esc", "Cancel drawing, clear selection, leave full screen"));
    }

    private void BuildToolbar()
    {
        var left = ToolbarLeft;
        left.Children.Add(CommandButton("open.folder", glyph: ""));
        left.Children.Add(Separator());
        left.Children.Add(CommandButton("layout.1", icon: "Icon.Layout1"));
        left.Children.Add(CommandButton("layout.2", icon: "Icon.Layout2"));
        left.Children.Add(CommandButton("layout.4", icon: "Icon.Layout4"));
        left.Children.Add(CommandButton("layout.mpr", icon: "Icon.LayoutMpr"));
        left.Children.Add(CommandButton("layout.mpr3d", icon: "Icon.LayoutMpr3d"));
        left.Children.Add(CommandButton("layout.petct", icon: "Icon.LayoutPetCt"));
        left.Children.Add(Separator());

        string? group = null;
        foreach (var t in ToolDefs)
        {
            if (group is not null && group != t.Group) left.Children.Add(Separator());
            group = t.Group;
            var cmd = _commands.Get("tool." + t.Tool);
            var button = new ToggleButton
            {
                Style = (Style)FindResource("ToolToggle"),
                ToolTip = cmd.ToolTip,
                Content = MakeIcon(t.Icon),
                IsChecked = t.Tool == _tool,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, cmd.Title);
            var tool = t.Tool;
            button.Click += (_, _) => SetTool(tool);
            _toolButtons[tool] = button;
            left.Children.Add(button);
        }
        left.Children.Add(Separator());
        left.Children.Add(CommandButton("plane.3d", icon: "Icon.Cube"));
        left.Children.Add(CommandButton("fusion.toggle", icon: "Icon.Fusion"));

        var right = ToolbarRight;
        right.Children.Add(CommandButton("edit.undo", glyph: ""));
        right.Children.Add(CommandButton("edit.redo", glyph: ""));
        right.Children.Add(Separator());
        _linkButton = new ToggleButton { Style = (Style)FindResource("ToolToggle"), Content = MakeIcon("Icon.Link"), IsChecked = _link, ToolTip = _commands.Get("view.link").ToolTip };
        System.Windows.Automation.AutomationProperties.SetName(_linkButton, "Link scrolling");
        _linkButton.Click += (_, _) => ToggleLink();
        right.Children.Add(_linkButton);
        right.Children.Add(CommandButton("export.screenshot", glyph: ""));
        right.Children.Add(CommandButton("export.deid", icon: "Icon.Shield"));
        right.Children.Add(Separator());
        right.Children.Add(CommandButton("help.palette", glyph: ""));
        right.Children.Add(CommandButton("help.shortcuts", glyph: ""));
    }

    private ToggleButton? _linkButton;

    private FrameworkElement MakeIcon(string key)
    {
        var icon = new GeometryIcon { Data = (Geometry)FindResource(key) };
        if (key == "Icon.WindowLevel") icon.FillData = (Geometry)FindResource("Icon.WindowLevelFill");
        return icon;
    }

    private Button CommandButton(string id, string? glyph = null, string? icon = null)
    {
        var cmd = _commands.Get(id);
        object content = icon is not null ? MakeIcon(icon) : new TextBlock { Text = glyph, Style = (Style)FindResource("GlyphText") };
        var b = new Button { Style = (Style)FindResource("ToolButton"), Content = content, ToolTip = cmd.ToolTip, Command = cmd };
        System.Windows.Automation.AutomationProperties.SetName(b, cmd.Title);
        return b;
    }

    private Border Separator() => new() { Style = (Style)FindResource("ToolbarSeparator") };

    private void WithActive(Action<ViewportControl> action)
    {
        if (_active is null || !_active.Model.HasContent) return;
        action(_active);
        _active.RequestRender();
        OnViewChanged(_active, ViewChange.Camera);
        RefreshDisplayControls();
    }

    private void ScrollActive(int delta)
    {
        if (_active is null || !_active.Model.HasContent || _active.Model.Is3D) return;
        _active.Model.ScrollBy(delta);
        OnViewChanged(_active, ViewChange.Slice);
    }

    private void ShowShortcuts()
    {
        var w = new Dialogs.ShortcutsWindow(_commands) { Owner = this };
        w.ShowDialog();
    }

    private void ShowCommandPalette()
    {
        var w = new Dialogs.CommandPaletteWindow(_commands) { Owner = this };
        if (w.ShowDialog() == true && w.Selected is { } cmd) cmd.Execute();
    }

    private void ToggleLink()
    {
        _link = !_link;
        if (_linkButton is not null) _linkButton.IsChecked = _link;
        SetStatus(_link
            ? "Linked. Views of other studies keep their current alignment, so scroll them to matching anatomy with linking off, then turn it back on (Ctrl+L)."
            : "Viewports scroll independently. Align the studies, then press Ctrl+L to link them at this offset.");
        if (_link) CaptureLinkOffsets();
    }

    private void TogglePrivacy()
    {
        _privacy = !_privacy;
        PrivacyCheck.IsChecked = _privacy;
        RenderAll();
        RefreshLibrary();
    }

    private void ToggleLibrary()
    {
        var hidden = LibraryPane.Visibility == Visibility.Visible;
        LibraryPane.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        LibraryColumn.Width = hidden ? new GridLength(0) : new GridLength(300);
    }

    private void ToggleInspector()
    {
        var hidden = InspectorPane.Visibility == Visibility.Visible;
        InspectorPane.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        InspectorColumn.Width = hidden ? new GridLength(0) : new GridLength(330);
    }

    private void RenderAll()
    {
        foreach (var v in _visible) v.RequestRender();
    }
}
