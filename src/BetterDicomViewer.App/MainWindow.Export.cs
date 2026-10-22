using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core;
using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Export;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Core.Segmentation;
using BetterDicomViewer.Imaging;
using Microsoft.Win32;

namespace BetterDicomViewer.App;

public partial class MainWindow
{
    private void OnScreenshotClick(object sender, RoutedEventArgs e) => SaveScreenshot();
    private void OnCopyImageClick(object sender, RoutedEventArgs e) => CopyViewportImage();
    private void OnDeidentifyClick(object sender, RoutedEventArgs e) => ShowDeidentifyDialog();
    private void OnExportSegmentationClick(object sender, RoutedEventArgs e) => ExportSegmentation();

    private BitmapSource CaptureViewports(bool allViewports, bool includeOverlay, bool hidePatient)
    {
        var restorePrivacy = _privacy;
        _privacy = hidePatient || _privacy;
        try
        {
            foreach (var v in _visible) v.RenderNow();
            if (!allViewports || _visible.Count == 1)
                return (_active ?? _visible[0]).Snapshot(includeOverlay);

            var dpi = VisualTreeHelper.GetDpi(ViewportGrid);
            var w = (int)Math.Round(ViewportGrid.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Round(ViewportGrid.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ViewportGrid.ActualWidth, ViewportGrid.ActualHeight));
                foreach (var v in _visible)
                {
                    var origin = v.TranslatePoint(new Point(0, 0), ViewportGrid);
                    dc.DrawImage(v.Snapshot(includeOverlay), new Rect(origin.X, origin.Y, v.ActualWidth, v.ActualHeight));
                }
            }
            rtb.Render(visual);
            return rtb;
        }
        finally
        {
            _privacy = restorePrivacy;
            foreach (var v in _visible) v.RenderNow();
        }
    }

    private void SaveScreenshot()
    {
        if (_active?.Model.HasContent != true)
        {
            SetStatus("Nothing to capture yet.");
            return;
        }
        var options = new Dialogs.ScreenshotWindow { Owner = this };
        if (options.ShowDialog() != true) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image|*.png|JPEG image|*.jpg",
            FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (dlg.ShowDialog(this) != true) return;
        var image = CaptureViewports(options.AllViewports, options.IncludeOverlay, options.HidePatient);
        BitmapEncoder encoder = dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var fs = File.Create(dlg.FileName)) encoder.Save(fs);
        SetStatus($"Screenshot saved to {dlg.FileName}{(options.HidePatient ? " (patient details hidden)" : "")}.");
    }

    private void CopyViewportImage()
    {
        if (_active?.Model.HasContent != true) return;
        Clipboard.SetImage(CaptureViewports(false, true, _privacy));
        SetStatus("Viewport image copied to the clipboard.");
    }

    private void ExportMeasurements(bool csv)
    {
        var study = _active?.Model.Set?.Series.StudyInstanceUid;
        if (study is null)
        {
            SetStatus("Open a study first.");
            return;
        }
        var annotations = Annotations.ForStudy(study);
        if (annotations.Count == 0)
        {
            SetStatus("There are no measurements in this study.");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Export measurements",
            Filter = csv ? "CSV (comma separated)|*.csv|JSON|*.json" : "JSON|*.json|CSV (comma separated)|*.csv",
            FileName = $"measurements-{DateTime.Now:yyyyMMdd-HHmmss}{(csv ? ".csv" : ".json")}",
        };
        if (dlg.ShowDialog(this) != true) return;
        var rows = annotations.Select(BuildRow).ToList();
        if (dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) MeasurementExport.WriteJson(dlg.FileName, rows);
        else MeasurementExport.WriteCsv(dlg.FileName, rows);
        SetStatus($"Exported {rows.Count} measurement(s) to {dlg.FileName}.");
    }

    private MeasurementRow BuildRow(Annotation a)
    {
        var series = FindSeries(new SeriesKey(a.StudyInstanceUid, a.SeriesInstanceUid));
        var first = series?.Instances.FirstOrDefault();
        MeasurementResult? result = null;
        var viewer = _visible.FirstOrDefault(v => v.Model.Set?.Id == a.DisplaySetId);
        if (viewer is not null) result = viewer.ResultFor(a);
        else if (Images.FindVolume(a.DisplaySetId) is { } volume)
        {
            result = MeasurementCalculator.Compute(a, new MeasurementContext
            {
                Data = volume.Data,
                IndexToWorld = volume.IndexToWorld,
                PatientSpace = true,
                ValueUnit = volume.ValueUnit,
                Suv = volume.Suv,
                IsSliceLoaded = volume.IsSliceLoaded,
            });
        }
        else if (a.PatientSpace)
        {
            result = new MeasurementResult();
            if (a.Kind == AnnotationKind.Length && a.Points.Count >= 2) result.Length = Vec3.Distance(a.Points[0], a.Points[1]);
            if (a.Kind == AnnotationKind.Angle && a.Points.Count >= 3) result.AngleDegrees = Vec3.AngleBetween(a.Points[0] - a.Points[1], a.Points[2] - a.Points[1]) * 180 / Math.PI;
        }
        var setLabel = _displaySets.Values.SelectMany(s => s).FirstOrDefault(s => s.Id == a.DisplaySetId)?.Label ?? "";
        return MeasurementRow.From(a, result, first?.Modality ?? "", first?.SeriesNumber, first?.SeriesDescription ?? "", setLabel);
    }

    private void ShowDeidentifyDialog()
    {
        if (Library.InstanceCount == 0)
        {
            SetStatus("Open scans before exporting.");
            return;
        }
        var dlg = new Dialogs.DeidentifyWindow(this) { Owner = this };
        dlg.ShowDialog();
    }

    public IReadOnlyList<Core.Dicom.InstanceInfo> InstancesForScope(int scope)
    {
        var set = _active?.Model.Set;
        var all = Library.Snapshot();
        return scope switch
        {
            0 when set is not null => Library.InstancesOf(set.Series),
            1 when set is not null => all.Where(s => s.StudyInstanceUid == set.Series.StudyInstanceUid).SelectMany(s => s.Series).SelectMany(s => s.Instances).ToList(),
            _ => all.SelectMany(s => s.Series).SelectMany(s => s.Instances).ToList(),
        };
    }

    public IReadOnlyCollection<string> SourceRoots => Library.Roots;

    private void ExportSegmentation()
    {
        if (_active?.Model is not { Labels: { } map, Volume: { } volume })
        {
            SetStatus("Create a segmentation on a volume first.");
            return;
        }
        var active = map.Segments.FirstOrDefault(s => s.Label == map.ActiveLabel);
        var dlg = new SaveFileDialog
        {
            Title = "Export segmentation",
            Filter = "Label map, NRRD (LPS)|*.seg.nrrd|Label map, NIfTI (RAS)|*.nii.gz|Surface of active segment, STL (LPS mm)|*.stl|Surface of active segment, OBJ (LPS mm)|*.obj|Segment statistics, CSV|*.csv",
            FileName = "segmentation.seg.nrrd",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            switch (dlg.FilterIndex)
            {
                case 1:
                    LabelMapIo.SaveNrrd(map, dlg.FileName);
                    break;
                case 2:
                    LabelMapIo.SaveNifti(map, dlg.FileName);
                    break;
                case 3:
                case 4:
                    if (active is null) throw new InvalidOperationException("Select a segment first.");
                    SurfaceExporter.Export(map.Labels, map.IndexToPatient, active.Label, dlg.FileName, obj: dlg.FilterIndex == 4);
                    break;
                default:
                    var stats = SegmentStatistics.Compute(map, volume, _active.Model.Fusion?.Pet);
                    var ci = CultureInfo.InvariantCulture;
                    var lines = new List<string> { "Segment,Label,Voxels,VolumeMl,Mean,StdDev,Min,Max,Unit,PetSuvMax,PetSuvMean" };
                    lines.AddRange(stats.Select(s => string.Join(",",
                        "\"" + s.Name.Replace("\"", "\"\"") + "\"", s.Label.ToString(ci), s.VoxelCount.ToString(ci), s.VolumeMl.ToString("0.###", ci),
                        s.Mean?.ToString("0.##", ci) ?? "", s.StdDev?.ToString("0.##", ci) ?? "", s.Min?.ToString("0.##", ci) ?? "", s.Max?.ToString("0.##", ci) ?? "",
                        s.Unit, s.PetSuvMax?.ToString("0.###", ci) ?? "", s.PetSuvMean?.ToString("0.###", ci) ?? "")));
                    File.WriteAllLines(dlg.FileName, lines);
                    break;
            }
            SetStatus($"Exported to {dlg.FileName}.");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
        }
    }

    private void OnShowTagsClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model.Set is not { } set) return;
        var frame = set.Frames[Math.Clamp(_active.Model.Volume is null ? _active.Model.FrameIndex : set.Frames.Count / 2, 0, set.Frames.Count - 1)];
        var w = new Dialogs.TagsWindow(frame.Instance.FilePath, _privacy) { Owner = this };
        w.Show();
    }

    private void OnRedactToolClick(object sender, RoutedEventArgs e) => SetTool(RedactToggle.IsChecked == true ? ToolKind.Redact : ToolKind.Scroll);

    private void OnClearRedactionsClick(object sender, RoutedEventArgs e)
    {
        Redactions.Clear();
        RenderAll();
        SetStatus("Redaction regions cleared.");
    }

    private void OnPrivacyClick(object sender, RoutedEventArgs e)
    {
        if ((PrivacyCheck.IsChecked == true) != _privacy) TogglePrivacy();
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.MessageDialog.Confirm(this, "Clear local cache",
                "Delete the local header index cache? Scans are not affected; the next open re-reads headers. Saved measurements and segmentations are kept.",
                "Delete cache")) return;
        foreach (var f in Directory.EnumerateFiles(AppPaths.IndexDir)) File.Delete(f);
        _thumbnails.Clear();
        UpdateCacheText();
        SetStatus("Local index cache cleared.");
    }

    private void UpdateCacheText()
    {
        static long Size(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        try
        {
            CacheText.Text = string.Create(CultureInfo.InvariantCulture,
                $"Local data in {AppPaths.Root}: index {Size(AppPaths.IndexDir) / 1048576.0:0.0} MB, measurements {Size(AppPaths.AnnotationsDir) / 1024.0:0} KB, segmentations {Size(AppPaths.SegmentationsDir) / 1048576.0:0.0} MB. Decoded images are held in memory only (budget {Images.BudgetBytes / 1073741824.0:0.#} GB).");
        }
        catch (IOException)
        {
        }
    }
}
