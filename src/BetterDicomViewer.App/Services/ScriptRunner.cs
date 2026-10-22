using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.App.Services;

/// <summary>
/// Runs a semicolon-separated list of UI actions passed with --script, for reproducible screenshots and smoke tests.
/// Example: --script "demo; layout mpr3d; waitload; screenshot out.png; exit".
/// </summary>
public sealed class ScriptRunner(MainWindow window)
{
    public static string? LogPath { get; private set; }

    public static List<string> Parse(string[] args, out string? openPath)
    {
        openPath = null;
        var commands = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--script" && i + 1 < args.Length)
                commands.AddRange(args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else if (args[i] == "--log" && i + 1 < args.Length)
                LogPath = args[++i];
            else if (args[i] == "--demo")
                commands.Insert(0, "demo");
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                openPath = args[i];
        }
        return commands;
    }

    public async Task RunAsync(IEnumerable<string> commands)
    {
        foreach (var raw in commands)
        {
            var parts = raw.Split(' ', 2, StringSplitOptions.TrimEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : "";
            try
            {
                await Run(cmd, arg);
            }
            catch (Exception ex)
            {
                window.ShowStatus($"Script command '{raw}' failed: {ex.Message}");
                if (LogPath is not null) File.AppendAllText(LogPath, $"[{raw}] {ex}{Environment.NewLine}");
            }
            await Idle();
        }
    }

    private static Task Idle() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private async Task Run(string cmd, string arg)
    {
        switch (cmd)
        {
            case "open":
                await window.OpenPathAsync(arg);
                break;
            case "demo":
            {
                var folder = Path.Combine(Core.AppPaths.Root, "demo-study");
                await Task.Run(() => Synthetic.DemoStudy.EnsureGenerated(folder));
                await window.OpenPathAsync(folder);
                break;
            }
            case "size":
            {
                var wh = arg.Split(' ');
                window.WindowState = WindowState.Normal;
                window.Width = double.Parse(wh[0], CultureInfo.InvariantCulture);
                window.Height = double.Parse(wh[1], CultureInfo.InvariantCulture);
                window.Left = 40;
                window.Top = 40;
                break;
            }
            case "series":
            {
                var series = window.Library.Snapshot().SelectMany(s => s.Series).Where(s => s.IsDisplayable).ToList();
                var pick = int.TryParse(arg, out var n)
                    ? series.ElementAtOrDefault(n)
                    : series.Where(s => (s.Description ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase) || s.Modality == arg).OrderByDescending(s => s.FrameCount).FirstOrDefault();
                if (pick is not null && window.ActiveViewport is { } vc) await window.LoadSeriesAsync(vc, pick);
                break;
            }
            case "viewport":
            {
                var i = int.Parse(arg, CultureInfo.InvariantCulture);
                if (i < window.Viewports.Count) window.Activate(window.Viewports[i]);
                break;
            }
            case "key":
                PressShortcut(arg);
                break;
            case "tool":
                window.SetTool(Enum.Parse<ToolKind>(arg, ignoreCase: true));
                break;
            case "wait":
                await Task.Delay(int.Parse(arg, CultureInfo.InvariantCulture));
                break;
            case "waitload":
                for (var i = 0; i < 600 && window.Viewports.Any(v => v.Model.Volume is { IsComplete: false } || v.Model.Fusion?.Pet is { IsComplete: false }); i++)
                    await Task.Delay(50);
                await Task.Delay(400);
                break;
            case "scroll":
                if (window.ActiveViewport is { } a)
                {
                    a.Model.ScrollBy(int.Parse(arg, CultureInfo.InvariantCulture));
                    window.OnViewChanged(a, ViewChange.Slice);
                }
                break;
            case "goto":
                if (window.ActiveViewport is { } g)
                {
                    var xyz = arg.Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    window.SetCursorPoint(g, new Vec3(xyz[0], xyz[1], xyz[2]));
                    g.Model.MovePlaneThrough(new Vec3(xyz[0], xyz[1], xyz[2]));
                    window.OnViewChanged(g, ViewChange.Slice);
                }
                break;
            case "drag":
            {
                // drag <viewport> <left|right|middle> x0 y0 x1 y1 [ctrl|shift] ; coordinates are fractions of the viewport size
                var parts2 = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vc = window.Viewports[int.Parse(parts2[0], CultureInfo.InvariantCulture)];
                var button = Enum.Parse<MouseButton>(parts2[1], ignoreCase: true);
                var p0 = Frac(vc, parts2[2], parts2[3]);
                var p1 = Frac(vc, parts2[4], parts2[5]);
                var mods = parts2.Length > 6 ? Enum.Parse<ModifierKeys>(parts2[6], ignoreCase: true) : ModifierKeys.None;
                vc.PointerDown(p0, button, mods);
                for (var i = 1; i <= 12; i++)
                {
                    vc.PointerMove(p0 + (p1 - p0) * i / 12.0);
                    await Idle();
                }
                vc.PointerUp(p1, button);
                break;
            }
            case "click":
            {
                // click <viewport> x y [left|right] [clicks]
                var parts2 = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vc = window.Viewports[int.Parse(parts2[0], CultureInfo.InvariantCulture)];
                var p = Frac(vc, parts2[1], parts2[2]);
                var button = parts2.Length > 3 ? Enum.Parse<MouseButton>(parts2[3], ignoreCase: true) : MouseButton.Left;
                var clicks = parts2.Length > 4 ? int.Parse(parts2[4], CultureInfo.InvariantCulture) : 1;
                vc.PointerMove(p);
                vc.PointerDown(p, button, ModifierKeys.None, clicks);
                vc.PointerUp(p, button);
                break;
            }
            case "move":
            {
                var parts2 = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vc = window.Viewports[int.Parse(parts2[0], CultureInfo.InvariantCulture)];
                vc.PointerMove(Frac(vc, parts2[1], parts2[2]));
                break;
            }
            case "wheel":
            {
                // wheel <viewport> x y notches [ctrl|shift]
                var parts2 = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vc = window.Viewports[int.Parse(parts2[0], CultureInfo.InvariantCulture)];
                var mods = parts2.Length > 4 ? Enum.Parse<ModifierKeys>(parts2[4], ignoreCase: true) : ModifierKeys.None;
                vc.PointerWheel(Frac(vc, parts2[1], parts2[2]), (int)(120 * double.Parse(parts2[3], CultureInfo.InvariantCulture)), mods);
                break;
            }
            case "export-screenshot":
                window.SaveScreenshotTo(arg, allViewports: true, includeOverlay: true, hidePatient: true);
                break;
            case "export-measurements":
                window.ExportMeasurementsTo(arg);
                break;
            case "export-surface":
                if (window.ActiveViewport?.Model is { Labels: { } map } && map.Segments.FirstOrDefault(x => x.Label == map.ActiveLabel) is { } seg)
                    Imaging.SurfaceExporter.Export(map.Labels, map.IndexToPatient, seg.Label, arg, obj: false);
                break;
            case "deid-export":
            {
                // deid-export <outFolder> <exclude|flag|redact>
                var parts3 = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var options = new Core.Deidentification.DeidentificationOptions
                {
                    BurnedIn = parts3.Length > 1 && parts3[1] == "redact" ? Core.Deidentification.BurnedInPolicy.Redact
                        : parts3.Length > 1 && parts3[1] == "flag" ? Core.Deidentification.BurnedInPolicy.IncludeAndFlag
                        : Core.Deidentification.BurnedInPolicy.Exclude,
                    RetainPatientCharacteristics = true,
                    Redactions = window.Redactions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                };
                var report = await Core.Deidentification.DeidentificationExporter.ExportAsync(window.InstancesForScope(1), window.SourceRoots, parts3[0], options, null, CancellationToken.None);
                window.ShowStatus($"De-identified export: {report.Exported} exported, {report.Redacted} redacted, {report.ExcludedBurnedIn} excluded.");
                break;
            }
            case "drag-handle":
            {
                // drag-handle <viewport> <targetViewport> <degrees>: rotates the target plane with the crosshair handle
                var parts4 = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vc = window.Viewports[int.Parse(parts4[0], CultureInfo.InvariantCulture)];
                var target = window.Viewports[int.Parse(parts4[1], CultureInfo.InvariantCulture)];
                var degrees = double.Parse(parts4[2], CultureInfo.InvariantCulture);
                var (handle, pivot) = vc.RotationHandleFor(target);
                if (handle is not { } h) break;
                var radius = (h - pivot).Length;
                var start = Math.Atan2(h.Y - pivot.Y, h.X - pivot.X);
                vc.PointerDown(h, MouseButton.Left, ModifierKeys.None);
                for (var i = 1; i <= 12; i++)
                {
                    var angle = start + degrees * Math.PI / 180 * i / 12;
                    vc.PointerMove(new Point(pivot.X + radius * Math.Cos(angle), pivot.Y + radius * Math.Sin(angle)));
                    await Idle();
                }
                vc.PointerUp(new Point(pivot.X + radius * Math.Cos(start + degrees * Math.PI / 180), pivot.Y + radius * Math.Sin(start + degrees * Math.PI / 180)), MouseButton.Left);
                break;
            }
            case "vr-preset":
                if (window.ActiveViewport is { } vp)
                {
                    vp.Model.Vr.Mode = VrMode.Preset;
                    vp.Model.Vr.PresetName = arg;
                    vp.RequestRender();
                }
                break;
            case "vr-mode":
                if (window.ActiveViewport is { } vm)
                {
                    vm.Model.Vr.Mode = Enum.Parse<VrMode>(arg, ignoreCase: true);
                    vm.RequestRender();
                }
                break;
            case "vr-orbit":
                if (window.ActiveViewport is { } vo)
                {
                    var d = arg.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    vo.Model.Vr.Orbit(d[0], d[1]);
                    if (d.Length > 2) vo.Model.Vr.Zoom = d[2];
                    vo.RequestRender();
                }
                break;
            case "seg-range":
            {
                var r = arg.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                window.Segmentation.ThresholdLow = r[0];
                window.Segmentation.ThresholdHigh = r[1];
                break;
            }
            case "seg-new":
                if (window.ActiveViewport?.Model is { Volume: { } segVolume } segModel)
                {
                    segModel.Labels ??= window.Images.GetOrCreateLabels(segVolume);
                    var newSeg = segModel.Labels.Segments.FirstOrDefault(x => x.Name == arg) ?? segModel.Labels.AddSegment(arg);
                    segModel.Labels.ActiveLabel = newSeg.Label;
                    segModel.ShowLabels = true;
                    window.OnSegmentationChanged(window.ActiveViewport!);
                }
                break;
            case "grow":
                if (window.ActiveViewport is { Model: { Volume: { } gv, Labels: { } gmap } } gvc)
                {
                    var xyz = arg.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    var edit = gmap.RegionGrow(new Vec3(xyz[0], xyz[1], xyz[2]), window.Segmentation.ThresholdLow, window.Segmentation.ThresholdHigh, gv.Data, gmap.ActiveLabel, 5_000_000, null, out _);
                    window.ShowStatus(edit is null ? "Seed outside range." : $"Region grow added {edit.Count} voxels.");
                    window.OnSegmentationChanged(gvc);
                }
                break;
            case "dump-shortcuts":
            {
                var sb = new System.Text.StringBuilder();
                var registry = GetRegistry();
                foreach (var group in registry.All.Where(c => c.Shortcuts.Count > 0).GroupBy(c => c.Category))
                {
                    sb.AppendLine($"**{group.Key}**").AppendLine();
                    sb.AppendLine("| Keys | Action |").AppendLine("| --- | --- |");
                    foreach (var c in group) sb.AppendLine($"| {string.Join(", ", c.Shortcuts.Select(k => "`" + k + "`"))} | {c.Title} |");
                    sb.AppendLine();
                }
                foreach (var group in registry.Hints.GroupBy(h => h.Category))
                {
                    sb.AppendLine($"**{group.Key}**").AppendLine();
                    sb.AppendLine("| Gesture | Action |").AppendLine("| --- | --- |");
                    foreach (var h in group) sb.AppendLine($"| {h.Gesture} | {h.Action} |");
                    sb.AppendLine();
                }
                File.WriteAllText(arg, sb.ToString());
                break;
            }
            case "clear-local-state":
                foreach (var dir in new[] { Core.AppPaths.AnnotationsDir, Core.AppPaths.SegmentationsDir })
                    foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
                break;
            case "annotate-demo":
                AddDemoAnnotations();
                break;
            case "screenshot":
                await Task.Delay(300);
                await Idle();
                WindowCapture.SavePng(window, arg);
                break;
            case "shortcuts":
                new Dialogs.ShortcutsWindow(GetRegistry()) { Owner = window }.Show();
                break;
            case "deid":
                new Dialogs.DeidentifyWindow(window, string.IsNullOrWhiteSpace(arg) ? null : arg) { Owner = window }.Show();
                break;
            case "screenshot-dialog":
            {
                await Task.Delay(500);
                var dialog = Application.Current.Windows.OfType<Window>().LastOrDefault(w => !ReferenceEquals(w, window));
                if (dialog is not null) WindowCapture.SavePng(dialog, arg);
                break;
            }
            case "close-dialogs":
                foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => !ReferenceEquals(w, window)).ToList()) w.Close();
                break;
            case "exit":
                Application.Current.Shutdown();
                break;
        }
    }

    private CommandRegistry GetRegistry() => window.Commands;

    private static Point Frac(FrameworkElement e, string x, string y) =>
        new(double.Parse(x, CultureInfo.InvariantCulture) * e.ActualWidth, double.Parse(y, CultureInfo.InvariantCulture) * e.ActualHeight);

    private void PressShortcut(string text)
    {
        var mods = ModifierKeys.None;
        var keyPart = text;
        foreach (var token in text.Split('+'))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                default: keyPart = token; break;
            }
        }
        var key = keyPart.Length == 1 && char.IsDigit(keyPart[0]) ? Key.D0 + (keyPart[0] - '0') : Enum.Parse<Key>(keyPart, ignoreCase: true);
        GetRegistry().TryHandle(key, mods);
    }

    /// <summary>Adds a length, an ellipse ROI on the liver lesion and a probe for demo screenshots of the synthetic study.</summary>
    private void AddDemoAnnotations()
    {
        if (window.ActiveViewport is not { Model.Set: { } set } vc || vc.Model.Volume is null) return;
        var plane = vc.Model.View;
        var center = new Vec3(-75, -10, -200);
        vc.Model.MovePlaneThrough(center);
        Annotation Make(AnnotationKind kind, params Vec3[] points) => new()
        {
            Kind = kind,
            StudyInstanceUid = set.Series.StudyInstanceUid,
            SeriesInstanceUid = set.Series.SeriesInstanceUid,
            DisplaySetId = set.Id,
            FrameOfReferenceUid = set.FrameOfReferenceUid,
            PatientSpace = true,
            Points = [.. points.Select(p => p + plane.Normal * Vec3.Dot(vc.Model.View.Center - p, plane.Normal))],
            PlaneRight = plane.Right,
            PlaneDown = plane.Down,
        };
        var lesion = Make(AnnotationKind.Ellipse, center - plane.Right * 20 - plane.Down * 20, center + plane.Right * 20 + plane.Down * 20);
        var length = Make(AnnotationKind.Length, center - plane.Right * 16, center + plane.Right * 16 + plane.Down * 2);
        length.Points[0] -= plane.Down * 32;
        length.Points[1] -= plane.Down * 32;
        foreach (var a in new[] { lesion, length })
        {
            a.Label = window.Annotations.NextLabel(a.StudyInstanceUid, a.Kind);
            window.Annotations.Add(a);
        }
        window.OnAnnotationsChanged();
        window.OnViewChanged(vc, ViewChange.Slice);
    }
}
