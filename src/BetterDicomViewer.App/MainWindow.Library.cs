using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Core.Segmentation;
using BetterDicomViewer.Core.Viewing;
using Microsoft.Win32;

namespace BetterDicomViewer.App;

public partial class MainWindow
{
    private readonly Dictionary<SeriesKey, List<DisplaySet>> _displaySets = [];
    private readonly Dictionary<string, BitmapSource?> _thumbnails = [];
    private readonly SemaphoreSlim _thumbnailSlots = new(2);
    private CancellationTokenSource? _scanCts;
    private bool _libraryRefreshPending;
    private List<LibraryStudy> _studies = [];

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenFolderDialog();
    private void OnOpenDicomDirClick(object sender, RoutedEventArgs e) => OpenDicomDirDialog();
    private void OnOpenDemoClick(object sender, RoutedEventArgs e) => _ = OpenDemoAsync();
    private void OnCancelScanClick(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void OpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "Open a folder containing DICOM files", Multiselect = false };
        if (dlg.ShowDialog(this) == true) _ = OpenPathAsync(dlg.FolderName);
    }

    private void OpenDicomDirDialog()
    {
        var dlg = new OpenFileDialog { Title = "Open DICOMDIR", Filter = "DICOMDIR|DICOMDIR;DICOMDIR.*|All files|*.*" };
        if (dlg.ShowDialog(this) == true) _ = OpenPathAsync(dlg.FileName);
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            foreach (var p in paths) _ = OpenPathAsync(p);
            e.Handled = true;
        }
    }

    public async Task OpenPathAsync(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            SetStatus($"Not found: {path}");
            return;
        }
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        ScanBanner.Visibility = Visibility.Visible;
        ScanProgress.IsIndeterminate = true;
        ScanText.Text = "Looking for DICOM files…";
        var sw = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgress.IsIndeterminate = p.Total == 0;
            ScanProgress.Value = p.Total == 0 ? 0 : (double)p.Processed / p.Total;
            ScanText.Text = p.Total == 0
                ? p.Phase + "…"
                : string.Create(CultureInfo.InvariantCulture, $"{p.Phase}: {p.Processed:N0} of {p.Total:N0} files, {p.DicomFiles:N0} DICOM");
        });
        try
        {
            var report = await LibraryScanner.ScanAsync(Library, path, progress, new ScanOptions(), cts.Token);
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"Indexed {report.DicomFiles:N0} DICOM files ({report.ReusedFromCache:N0} from the local index, {report.NonDicomFiles:N0} other files skipped) in {sw.Elapsed.TotalSeconds:0.0} s. First studies listed after {report.FirstResultsAfter.TotalMilliseconds:0} ms.");
            if (report.Failures.Count > 0) summary += $" {report.Failures.Count} file(s) could not be read.";
            if (report.Cancelled) summary = "Indexing cancelled. " + summary;
            SetStatus(summary);
            ScanText.Text = report.Failures.Count > 0 ? string.Join("\n", report.Failures.Take(3)) : summary;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Indexing cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not open: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                await Task.Delay(1500);
                ScanBanner.Visibility = Visibility.Collapsed;
            }
            RefreshLibrary();
        }
    }

    private async Task OpenDemoAsync()
    {
        var folder = Path.Combine(Core.AppPaths.Root, "demo-study");
        SetStatus("Generating the synthetic demo study…");
        await Task.Run(() => Synthetic.DemoStudy.EnsureGenerated(folder));
        await OpenPathAsync(folder);
        var study = Library.Snapshot().FirstOrDefault(s => s.Series.Any(x => x.Instances.Any(i => i.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))));
        var ct = study?.Series.FirstOrDefault(s => s.Modality == "CT" && s.FrameCount > 20);
        if (ct is not null && _active is not null) await LoadSeriesAsync(_active, ct);
    }

    private void ScheduleLibraryRefresh()
    {
        if (_libraryRefreshPending) return;
        _libraryRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _libraryRefreshPending = false;
            RefreshLibrary();
        }, DispatcherPriority.Background);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        _studies = Library.Snapshot();
        var query = SearchBox.Text.Trim();
        var expanded = LibraryTree.Items.OfType<TreeViewItem>().Where(i => i.IsExpanded).Select(i => i.Tag as string).ToHashSet();
        LibraryTree.Items.Clear();
        foreach (var study in _studies)
        {
            var seriesMatches = study.Series.Where(s => Matches(query, study, s)).ToList();
            if (seriesMatches.Count == 0) continue;
            var item = new TreeViewItem
            {
                Header = StudyHeader(study),
                Tag = study.StudyInstanceUid,
                IsExpanded = expanded.Contains(study.StudyInstanceUid) || expanded.Count == 0 || query.Length > 0,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, _privacy ? "Study" : $"{study.PatientName} {study.StudyDescription()}");
            foreach (var s in seriesMatches) item.Items.Add(SeriesItem(s));
            LibraryTree.Items.Add(item);
        }
        LibraryEmpty.Visibility = _studies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPetCandidates();
    }

    private static bool Matches(string query, LibraryStudy study, LibrarySeries series)
    {
        if (query.Length == 0) return true;
        var hay = string.Join(" ", study.PatientName, study.PatientId, study.Description, study.StudyDate, DicomDates.FormatDate(study.StudyDate),
            study.AccessionNumber, series.Description, series.Modality, series.SeriesNumber?.ToString(CultureInfo.InvariantCulture));
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private FrameworkElement StudyHeader(LibraryStudy s)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var name = _privacy ? "Patient (hidden)" : (string.IsNullOrWhiteSpace(s.PatientName) ? "Unnamed patient" : s.PatientName!.Replace('^', ' '));
        panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var line = string.Join(" · ", new[] { _privacy ? null : DicomDates.FormatDate(s.StudyDate), s.Description, string.Join("/", s.Modalities) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        panel.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    private TreeViewItem SeriesItem(LibrarySeries s)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var thumbBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Black,
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };
        var image = new Image { Stretch = Stretch.Uniform };
        thumbBorder.Child = image;
        grid.Children.Add(thumbBorder);
        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        text.Children.Add(new TextBlock { Text = s.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
        var count = s.FrameCount > 0 ? $"{s.FrameCount} {(s.Instances.Count == 1 && s.FrameCount > 1 ? "frames" : "images")}" : $"{s.Instances.Count} objects";
        var detail = $"{s.Modality} · #{s.SeriesNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"} · {count}";
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") });
        if (!s.IsDisplayable)
        {
            text.Children.Add(new TextBlock { Text = s.UnsupportedReason, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 200, Foreground = (Brush)FindResource("WarningBrush") });
            thumbBorder.Child = new TextBlock { Text = "", Style = (Style)FindResource("GlyphText"), Foreground = (Brush)FindResource("WarningBrush") };
        }
        else if (s.IsProvisional)
        {
            text.Children.Add(new TextBlock { Text = "Reading headers…", FontSize = 11, Foreground = (Brush)FindResource("TextFillColorTertiaryBrush") });
        }
        var item = new TreeViewItem { Header = grid, Tag = s, ToolTip = s.IsDisplayable ? "Double-click to open, or drag to a viewport" : s.UnsupportedReason };
        System.Windows.Automation.AutomationProperties.SetName(item, $"{s.Modality} {s.DisplayName}, {count}");
        if (s.IsDisplayable && !s.IsProvisional) _ = LoadThumbnailAsync(s, image);
        return item;
    }

    private async Task LoadThumbnailAsync(LibrarySeries s, Image target)
    {
        var key = s.Key.SeriesInstanceUid;
        if (_thumbnails.TryGetValue(key, out var cached))
        {
            target.Source = cached;
            return;
        }
        await _thumbnailSlots.WaitAsync();
        try
        {
            if (_thumbnails.TryGetValue(key, out cached))
            {
                target.Source = cached;
                return;
            }
            var bmp = await Task.Run(() => Thumbnails.Create(s));
            _thumbnails[key] = bmp;
            target.Source = bmp;
        }
        finally
        {
            _thumbnailSlots.Release();
        }
    }

    private void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryTree.SelectedItem is TreeViewItem { Tag: LibrarySeries s } && s.IsDisplayable && _active is not null)
        {
            _ = LoadSeriesAsync(_active, s);
            e.Handled = true;
        }
    }

    private void OnLibraryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LibraryTree.SelectedItem is TreeViewItem { Tag: LibrarySeries s } && s.IsDisplayable && _active is not null)
        {
            _ = LoadSeriesAsync(_active, s);
            e.Handled = true;
        }
    }

    private Point _dragStart;

    private void OnLibraryMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStart = e.GetPosition(LibraryTree);
            return;
        }
        var delta = e.GetPosition(LibraryTree) - _dragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        if (LibraryTree.SelectedItem is TreeViewItem { Tag: LibrarySeries s } && s.IsDisplayable)
            DragDrop.DoDragDrop(LibraryTree, new DataObject(typeof(LibrarySeries), s), DragDropEffects.Copy);
    }

    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(LibrarySeries)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnViewportDrop(object sender, DragEventArgs e)
    {
        if (sender is ViewportControl vc && e.Data.GetData(typeof(LibrarySeries)) is LibrarySeries s)
        {
            Activate(vc);
            _ = LoadSeriesAsync(vc, s);
            e.Handled = true;
        }
    }

    public async Task<List<DisplaySet>> DisplaySetsFor(LibrarySeries series)
    {
        if (_displaySets.TryGetValue(series.Key, out var cached)) return cached;
        var instances = series.Instances;
        if (instances.Any(i => i.IsProvisional))
        {
            SetStatus("Reading headers for this series…");
            await LibraryScanner.ParseNowAsync(Library, instances.Where(i => i.IsProvisional).Select(i => i.FilePath).ToList(), CancellationToken.None);
            instances = Library.InstancesOf(series.Key);
        }
        var sets = await Task.Run(() => SeriesAnalyzer.Analyze(series.Key, instances));
        _displaySets[series.Key] = sets;
        return sets;
    }

    public LibrarySeries? FindSeries(SeriesKey key) =>
        _studies.SelectMany(s => s.Series).FirstOrDefault(s => s.Key == key) ?? Library.Snapshot().SelectMany(s => s.Series).FirstOrDefault(s => s.Key == key);

    public async Task<bool> LoadSeriesAsync(ViewportControl vc, LibrarySeries series, int setIndex = 0, PlaneKind plane = PlaneKind.Acquisition, bool quiet = false)
    {
        var sw = Stopwatch.StartNew();
        var sets = await DisplaySetsFor(series);
        if (sets.Count == 0)
        {
            SetStatus(series.UnsupportedReason ?? "This series has no images that can be displayed.");
            return false;
        }
        var set = sets[Math.Clamp(setIndex, 0, sets.Count - 1)];
        return LoadDisplaySet(vc, set, plane, sw, quiet);
    }

    public bool LoadDisplaySet(ViewportControl vc, DisplaySet set, PlaneKind plane, Stopwatch? sw = null, bool quiet = false)
    {
        sw ??= Stopwatch.StartNew();
        _cineTimer.Stop();
        ReportHover("");
        vc.ReleaseContent();
        var model = vc.Model;
        model.CinePlaying = false;
        if (set.IsVolume)
        {
            var mid = set.Geometry!.Nz / 2;
            var volume = Images.AcquireVolume(set, mid);
            model.ShowVolume(set, volume, set.IsColor ? PlaneKind.Acquisition : plane);
            if (File.Exists(SegmentationPaths.For(set))) model.Labels = Images.GetOrCreateLabels(volume);
            else model.Labels = Images.FindLabels(set.Id);
            HookVolumeProgress(volume);
            ApplyDefaultDisplay(model);
            model.Fit();
            if (!quiet) ReportFirstImage(vc, volume, mid, sw);
        }
        else
        {
            var frames = Images.AcquireFrames(set);
            var start = set.Frames.Count > 1 && set.Frames[0].Instance.IsMultiFrame ? 0 : set.Frames.Count / 2;
            model.ShowFrames(set, frames, start);
            model.CineFps = Math.Clamp(1000.0 / frames.FrameIntervalMs, 1, 60);
            frames.FrameReady += k =>
            {
                if (vc.Model.Frames == frames && k == vc.Model.FrameIndex) Dispatcher.BeginInvoke(() =>
                {
                    if (!_defaultsPending.Contains(vc)) vc.RequestRender();
                    else
                    {
                        _defaultsPending.Remove(vc);
                        ApplyDefaultDisplay(vc.Model);
                        vc.RequestRender();
                    }
                });
            };
            _defaultsPending.Add(vc);
            ApplyDefaultDisplay(model);
            model.Fit();
            if (!quiet) SetStatus(string.Create(CultureInfo.InvariantCulture, $"Opened {set.Label} ({set.Columns}×{set.Rows}){(set.Frames[0].Instance.IsMultiFrame ? ". Space plays the loop." : ".")}"));
        }
        Activate(vc);
        vc.RequestRender();
        OnViewChanged(vc, ViewChange.Content);
        RefreshInspector();
        InitializeLinkOffsets();
        vc.Focus();
        return true;
    }

    private readonly HashSet<ViewportControl> _defaultsPending = [];
    private readonly HashSet<ImageVolume> _hookedVolumes = [];

    private void HookVolumeProgress(ImageVolume volume)
    {
        if (!_hookedVolumes.Add(volume)) return;
        var pending = 0;
        volume.SliceLoaded += (_, _) =>
        {
            if (Interlocked.Exchange(ref pending, 1) == 1) return;
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref pending, 0);
                foreach (var v in _visible.Where(v => ReferenceEquals(v.Model.Volume, volume) || ReferenceEquals(v.Model.Fusion?.Pet, volume)))
                {
                    if (_defaultsPending.Remove(v)) ApplyDefaultDisplay(v.Model);
                    v.RequestRender();
                }
                if (volume.IsComplete) RefreshInspector();
            }, DispatcherPriority.Render);
        };
    }

    private void ReportFirstImage(ViewportControl vc, ImageVolume volume, int slice, Stopwatch sw)
    {
        void Check()
        {
            if (volume.IsSliceLoaded(slice))
            {
                var first = sw.Elapsed.TotalMilliseconds;
                SetStatus(string.Create(CultureInfo.InvariantCulture, $"First image in {first:0} ms; loading {volume.Nz} slices…"));
                volume.Completion.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                    SetStatus(string.Create(CultureInfo.InvariantCulture,
                        $"First image in {first:0} ms, full volume ({volume.Nx}×{volume.Ny}×{volume.Nz}, {volume.Data.ByteLength / 1048576.0:0} MB) in {sw.Elapsed.TotalMilliseconds:0} ms.{(volume.LoadError is null ? "" : " " + volume.LoadError)}"))));
                return;
            }
            Dispatcher.BeginInvoke(Check, DispatcherPriority.Background);
        }
        Check();
    }

    /// <summary>Default window: DICOM-provided values, HU soft tissue for CT, SUV 0–6 for PET with valid SUV, otherwise the data range.</summary>
    public void ApplyDefaultDisplay(ViewportModel model)
    {
        var set = model.Set;
        if (set is null || set.IsColor) return;
        var inst = set.Representative;
        var d = model.Display;
        d.Function = Voi.Parse(inst.VoiLutFunction);
        d.Colormap = Colormap.Grayscale;
        d.ValueFactor = 1;
        d.Invert = string.Equals(inst.PhotometricInterpretation, "MONOCHROME1", StringComparison.OrdinalIgnoreCase);
        model.Interpolation = Imaging.Interpolation.Linear;

        var windows = VolumeLoader.DicomWindowPresets(inst);
        if (model.Volume is { } v)
        {
            if (v.Suv is { IsAvailable: true } suv)
            {
                d.ValueFactor = suv.Factor(v.Nz / 2) ?? 1;
                d.Center = 3;
                d.Width = 6;
                d.Invert = true;
                return;
            }
            if (windows.Count > 0)
            {
                d.Center = windows[0].Center;
                d.Width = windows[0].Width;
                return;
            }
            if (v.ValueUnit == "HU")
            {
                d.Center = 40;
                d.Width = 400;
                return;
            }
            var (lo, hi) = v.ValueRange;
            if (string.Equals(inst.Modality, "PT", StringComparison.OrdinalIgnoreCase)) lo = 0;
            d.Center = (lo + hi) / 2;
            d.Width = Math.Max(hi - lo, 1);
            if (string.Equals(inst.Modality, "PT", StringComparison.OrdinalIgnoreCase))
            {
                d.Center = hi * 0.3;
                d.Width = hi * 0.6;
                d.Invert = true;
            }
            return;
        }

        if (windows.Count > 0)
        {
            d.Center = windows[0].Center;
            d.Width = windows[0].Width;
            return;
        }
        if (model.Frames?.TryGet(model.FrameIndex) is { } buffer)
        {
            double min = double.MaxValue, max = double.MinValue;
            var step = Math.Max(1, buffer.Nx * buffer.Ny / 200_000);
            for (var i = 0; i < buffer.Nx * buffer.Ny; i += step)
            {
                var val = buffer.GetValue(i % buffer.Nx, i / buffer.Nx, 0);
                if (val < min) min = val;
                if (val > max) max = val;
            }
            d.Center = (min + max) / 2;
            d.Width = Math.Max(max - min, 1);
        }
        else
        {
            var bits = Math.Max(8, inst.BitsStored);
            d.Center = (1 << (bits - 1)) * inst.RescaleSlope + inst.RescaleIntercept;
            d.Width = (1 << bits) * inst.RescaleSlope;
        }
    }
}

internal static class LibraryStudyExtensions
{
    public static string StudyDescription(this LibraryStudy s) => s.Description ?? "";
}

/// <summary>Builds small grey or colour previews from the middle image of a series. Kept in memory only.</summary>
internal static class Thumbnails
{
    public static BitmapSource? Create(LibrarySeries series)
    {
        try
        {
            var images = series.Instances.Where(i => i.HasPixelData && SupportPolicy.UnsupportedReason(i) is null).OrderBy(i => i.InstanceNumber ?? 0).ToList();
            if (images.Count == 0) return null;
            var inst = images[images.Count / 2];
            var ds = FrameDecoder.LoadDataset(inst.FilePath);
            var raw = FrameDecoder.Decode(ds, inst.NumberOfFrames / 2);
            const int size = 64;
            var scale = Math.Max(raw.Columns, raw.Rows) / (double)size;
            var w = Math.Max(1, (int)(raw.Columns / scale));
            var h = Math.Max(1, (int)(raw.Rows / scale));
            var pixels = new byte[w * h * 4];
            var color = raw.SamplesPerPixel >= 3 || raw.Photometric.StartsWith("PALETTE", StringComparison.OrdinalIgnoreCase);
            byte[]? rgb = null;
            float[]? gray = null;
            if (color)
            {
                rgb = new byte[raw.PixelCount * 3];
                FrameDecoder.ToRgb24(raw, rgb);
            }
            else
            {
                gray = new float[raw.PixelCount];
                FrameDecoder.ToFloat(raw, inst.RescaleSlope, inst.RescaleIntercept, gray);
            }

            double lo, hi;
            if (inst.WindowCenter is { Length: > 0 } c && inst.WindowWidth is { Length: > 0 } ww && ww[0] > 0)
            {
                lo = c[0] - ww[0] / 2;
                hi = c[0] + ww[0] / 2;
            }
            else if (string.Equals(inst.Modality, "CT", StringComparison.OrdinalIgnoreCase))
            {
                lo = -160;
                hi = 240;
            }
            else if (gray is not null)
            {
                var sorted = gray.Where((_, i) => i % 7 == 0).OrderBy(v => v).ToArray();
                lo = sorted[(int)(sorted.Length * 0.01)];
                hi = sorted[(int)(sorted.Length * 0.995)];
            }
            else
            {
                lo = 0;
                hi = 255;
            }
            var invert = string.Equals(raw.Photometric, "MONOCHROME1", StringComparison.OrdinalIgnoreCase);

            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Min(raw.Columns - 1, (int)((x + 0.5) * scale));
                var sy = Math.Min(raw.Rows - 1, (int)((y + 0.5) * scale));
                var si = sy * raw.Columns + sx;
                var o = (y * w + x) * 4;
                if (rgb is not null)
                {
                    pixels[o] = rgb[si * 3 + 2];
                    pixels[o + 1] = rgb[si * 3 + 1];
                    pixels[o + 2] = rgb[si * 3];
                }
                else
                {
                    var t = Math.Clamp((gray![si] - lo) / Math.Max(hi - lo, 1e-6), 0, 1);
                    if (invert) t = 1 - t;
                    var b = (byte)(t * 255);
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = b;
                }
                pixels[o + 3] = 255;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
