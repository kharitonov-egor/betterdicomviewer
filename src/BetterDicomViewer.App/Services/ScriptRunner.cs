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
                new Dialogs.DeidentifyWindow(window) { Owner = window }.Show();
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
