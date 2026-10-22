using System.Windows.Media;

namespace BetterDicomViewer.App.Viewer;

/// <summary>Coalesces render requests to at most one render per viewport per display frame.</summary>
public static class RenderScheduler
{
    private static readonly HashSet<ViewportControl> Pending = [];
    private static bool _subscribed;

    public static double LastFrameMs { get; private set; }

    public static void Enqueue(ViewportControl viewport)
    {
        Pending.Add(viewport);
        if (_subscribed) return;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (Pending.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
            return;
        }
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var batch = Pending.ToArray();
        Pending.Clear();
        foreach (var v in batch) v.RenderNow();
        LastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
