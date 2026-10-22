using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Segmentation;
using BetterDicomViewer.Core.Viewing;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.App;

public partial class MainWindow
{
    private bool _syncing;
    private readonly DispatcherTimer _segSaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<Slider> _cropSliders = [];

    private sealed record DisplaySetItem(DisplaySet Set)
    {
        public override string ToString() => Set.Label;
    }

    private sealed record PetItem(DisplaySet? Set, Core.Library.LibrarySeries Series, string Text)
    {
        public override string ToString() => Text;
    }

    private void InitializeInspector()
    {
        foreach (var c in Colormap.All)
        {
            ColormapCombo.Items.Add(c);
            FusionColormapCombo.Items.Add(c);
        }
        foreach (var p in VrPresets.All) VrPresetCombo.Items.Add(p.Name);
        _segSaveTimer.Tick += (_, _) =>
        {
            _segSaveTimer.Stop();
            SaveDirtySegmentations();
        };
        string[] cropNames = ["Left–right start", "Left–right end", "Front–back start", "Front–back end", "Feet–head start", "Feet–head end"];
        for (var i = 0; i < 6; i++)
        {
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = i % 2 == 0 ? 0 : 1, Margin = new Thickness(0, 2, 0, 2), SmallChange = 0.01, LargeChange = 0.05, Tag = i };
            System.Windows.Automation.AutomationProperties.SetName(slider, "Crop " + cropNames[i]);
            slider.ToolTip = cropNames[i];
            slider.ValueChanged += OnCropChanged;
            _cropSliders.Add(slider);
            CropPanel.Children.Add(slider);
        }
        BrushText.Text = "6 mm";
        CineSpeedText.Text = "25 fps";
    }

    public void RefreshInspector()
    {
        _syncing = true;
        try
        {
            var m = _active?.Model;
            var set = m?.Set;
            ImageSection.IsEnabled = set is not null;
            if (set is null)
            {
                ImageTitle.Text = "No image";
                ImageSubtitle.Text = "Open a series from the library.";
                DisplaySetCombo.Items.Clear();
                ImageWarnings.Text = "";
                RefreshMeasurementList();
                RefreshSegmentList();
                RefreshFusionPanel();
                return;
            }
            var inst = set.Representative;
            ImageTitle.Text = $"{inst.Modality} · {(string.IsNullOrWhiteSpace(inst.SeriesDescription) ? "Series " + inst.SeriesNumber : inst.SeriesDescription)}";
            ImageSubtitle.Text = set.IsVolume
                ? string.Create(CultureInfo.InvariantCulture, $"{set.Geometry!.Nx}×{set.Geometry.Ny}×{set.Geometry.Nz} voxels, {set.Geometry.Spacing.X:0.###}×{set.Geometry.Spacing.Y:0.###}×{set.Geometry.Spacing.Z:0.###} mm · {Core.Dicom.DicomRuntime.DescribeTransferSyntax(inst.TransferSyntaxUid)}")
                : string.Create(CultureInfo.InvariantCulture, $"{set.Columns}×{set.Rows}, {set.SliceCount} frame(s) · {(m!.Frames?.Calibration.Source ?? "")} · {Core.Dicom.DicomRuntime.DescribeTransferSyntax(inst.TransferSyntaxUid)}");

            DisplaySetCombo.Items.Clear();
            if (_displaySets.TryGetValue(set.Series, out var sets))
            {
                foreach (var s in sets) DisplaySetCombo.Items.Add(new DisplaySetItem(s));
                DisplaySetCombo.SelectedIndex = sets.FindIndex(s => s.Id == set.Id);
            }
            DisplaySetCombo.IsEnabled = DisplaySetCombo.Items.Count > 1;

            PlaneCombo.IsEnabled = m!.Volume is not null && !set.IsColor;
            PlaneCombo.SelectedIndex = m.Is3D ? 4 : (int)m.Plane;

            var gray = !set.IsColor;
            PresetCombo.IsEnabled = LevelBox.IsEnabled = WidthBox.IsEnabled = ColormapCombo.IsEnabled = InvertCheck.IsEnabled = gray;
            PresetCombo.Items.Clear();
            foreach (var p in PresetsFor(m)) PresetCombo.Items.Add(p);
            PresetCombo.SelectedIndex = -1;
            RefreshWindowBoxes();
            ColormapCombo.SelectedItem = m.Display.Colormap;
            InterpolationCombo.SelectedIndex = (int)m.Interpolation;
            InvertCheck.IsChecked = m.Display.Invert;
            SlabCombo.SelectedIndex = (int)m.View.SlabMode;
            SlabCombo.IsEnabled = m.Volume is not null;
            SlabSlider.Value = m.View.SlabThicknessMm <= 0 ? 10 : Math.Min(100, m.View.SlabThicknessMm);
            CinePanel.Visibility = set.SliceCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            CineGlyph.Text = m.CinePlaying ? "" : "";
            CineSpeed.Value = m.CineFps;
            var warnings = (m.Volume?.Warnings ?? set.Warnings).Concat(m.Frames is { Calibration.Caveat: { } c } ? [c] : []).Distinct().ToList();
            ImageWarnings.Text = string.Join("\n", warnings.Select(w => "⚠ " + w));

            VrModeCombo.SelectedIndex = (int)m.Vr.Mode;
            VrPresetCombo.SelectedItem = m.Vr.PresetName;
            VrShiftSlider.Value = m.Vr.OpacityShift;
            VrScaleSlider.Value = m.Vr.OpacityScale;
            VrShadeCheck.IsChecked = m.Vr.Shade;
            VrInvertCheck.IsChecked = m.Vr.InvertProjection;
            VrLabelsCheck.IsChecked = m.Vr.ShowLabels;
            for (var i = 0; i < 6; i++) _cropSliders[i].Value = i % 2 == 0 ? m.Vr.CropMin[i / 2] : m.Vr.CropMax[i / 2];
            VrInfoText.Text = string.IsNullOrEmpty(m.Vr.RendererInfo) ? "" : ShortRendererInfo(m.Vr.RendererInfo);
            GpuCheck.IsChecked = Services.GpuPreference.IsHighPerformance;
        }
        finally
        {
            _syncing = false;
        }
        RefreshMeasurementList();
        RefreshSegmentList();
        RefreshFusionPanel();
        UpdateCacheText();
    }

    private static string ShortRendererInfo(string info)
    {
        var lines = info.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("OpenGL renderer", StringComparison.OrdinalIgnoreCase) || l.StartsWith("OpenGL version", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", lines);
    }

    private void RefreshWindowBoxes()
    {
        if (_active?.Model is not { } m) return;
        var wasSyncing = _syncing;
        _syncing = true;
        var fmt = m.Display.Width >= 20 ? "0.#" : "0.###";
        LevelBox.Text = m.Set?.IsColor == true ? "" : m.Display.Center.ToString(fmt, CultureInfo.InvariantCulture);
        WidthBox.Text = m.Set?.IsColor == true ? "" : m.Display.Width.ToString(fmt, CultureInfo.InvariantCulture);
        _syncing = wasSyncing;
    }

    private void RefreshSliceInfo()
    {
    }

    private void OnDisplaySetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active is null || DisplaySetCombo.SelectedItem is not DisplaySetItem item) return;
        if (item.Set.Id == _active.Model.Set?.Id) return;
        LoadDisplaySet(_active, item.Set, PlaneKind.Acquisition);
    }

    private void OnPlaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || PlaneCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag == "Volume3D")
        {
            if (_active?.Model.Is3D == false) Toggle3D();
            return;
        }
        SetPlane(Enum.Parse<PlaneKind>(tag));
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m || PresetCombo.SelectedItem is not PresetEntry p) return;
        ApplyPreset(m, p);
        PropagateWindow(_active);
        RefreshWindowBoxes();
    }

    private void OnWindowBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnWindowBoxCommitted(sender, e);
            e.Handled = true;
        }
    }

    private void OnWindowBoxCommitted(object sender, RoutedEventArgs e)
    {
        if (_syncing || _active?.Model is not { } m) return;
        if (double.TryParse(LevelBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) m.Display.Center = c;
        if (double.TryParse(WidthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0) m.Display.Width = w;
        PropagateWindow(_active);
    }

    private void OnColormapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m || ColormapCombo.SelectedItem is not Colormap c) return;
        m.Display.Colormap = c;
        PropagateWindow(_active);
    }

    private void OnInterpolationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m) return;
        m.Interpolation = (Interpolation)Math.Max(0, InterpolationCombo.SelectedIndex);
        _active.RequestRender();
    }

    private void OnInvertClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model is not { } m) return;
        m.Display.Invert = InvertCheck.IsChecked == true;
        PropagateWindow(_active);
    }

    private void OnSlabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m) return;
        m.View.SlabMode = (SlabMode)Math.Max(0, SlabCombo.SelectedIndex);
        if (m.View.SlabThicknessMm <= 0) m.View.SlabThicknessMm = SlabSlider.Value;
        _active.RequestRender();
    }

    private void OnSlabThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m) return;
        m.View.SlabThicknessMm = SlabSlider.Value;
        SlabSlider.ToolTip = string.Create(CultureInfo.InvariantCulture, $"Slab thickness {SlabSlider.Value:0} mm");
        _active.RequestRender();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e) => ResetActiveView();
    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => _commands.Get("image.rotL").Execute();
    private void OnRotateRightClick(object sender, RoutedEventArgs e) => _commands.Get("image.rotR").Execute();
    private void OnFlipHClick(object sender, RoutedEventArgs e) => _commands.Get("image.flipH").Execute();
    private void OnFlipVClick(object sender, RoutedEventArgs e) => _commands.Get("image.flipV").Execute();
    private void OnCineClick(object sender, RoutedEventArgs e) => ToggleCine();

    private void OnCineSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        CineSpeedText.Text = string.Create(CultureInfo.InvariantCulture, $"{CineSpeed.Value:0} fps");
        if (_syncing || _active?.Model is not { } m) return;
        m.CineFps = CineSpeed.Value;
        if (_cineTimer.IsEnabled) _cineTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, m.CineFps));
    }

    // ---------- Measurements ----------

    private sealed record MeasureItem(Annotation Annotation, string Text)
    {
        public override string ToString() => Text;
    }

    private void RefreshMeasurementList()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            MeasureList.Items.Clear();
            if (_active?.Model.Set is not { } set) return;
            foreach (var a in Annotations.ForStudy(set.Series.StudyInstanceUid).OrderBy(a => a.CreatedUtc))
            {
                var result = a.DisplaySetId == set.Id || a.FrameOfReferenceUid == _active.Model.FrameOfReferenceUid ? _active.ResultFor(a) : null;
                var summary = result is null ? "" : string.Join(" · ", result.Lines().Take(2));
                var where = a.FrameIndex is { } f ? $"frame {f + 1}" : "";
                MeasureList.Items.Add(new MeasureItem(a, $"{a.Label}  {Annotation.KindName(a.Kind)}  {summary} {where}".Trim()));
            }
            SyncMeasureSelection();
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void SyncMeasureSelection()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        MeasureList.SelectedItem = MeasureList.Items.OfType<MeasureItem>().FirstOrDefault(i => i.Annotation.Id == _selected?.Id);
        _syncing = wasSyncing;
    }

    private void OnMeasureSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing) return;
        _selected = (MeasureList.SelectedItem as MeasureItem)?.Annotation;
        RenderAll();
    }

    private void OnMeasureDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MeasureList.SelectedItem is not MeasureItem { Annotation: var a } || _active is null) return;
        var m = _active.Model;
        if (a.FrameIndex is { } f && m.Frames is not null) m.FrameIndex = f;
        else if (a.PatientSpace && m.Volume is not null)
        {
            if (Math.Abs(Core.Geometry.Vec3.Dot(a.PlaneNormal, m.View.Normal)) < 0.99)
            {
                m.View.SetPlane(a.PlaneRight, a.PlaneDown);
            }
            m.MovePlaneThrough(a.Points[0]);
        }
        _selected = a;
        OnViewChanged(_active, ViewChange.Slice);
    }

    private void DeleteSelectedAnnotation()
    {
        if (_selected is not { } a) return;
        Annotations.Remove(a);
        Undo.Push(new DelegateAction($"Delete {a.Label}", () => Annotations.Add(a), () => Annotations.Remove(a)));
        _selected = null;
        OnAnnotationsChanged();
    }

    private void OnDeleteMeasureClick(object sender, RoutedEventArgs e) => DeleteSelectedAnnotation();

    private async void OnRenameMeasureClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } a) return;
        var name = await PromptTextAsync("Measurement label", a.Label);
        if (string.IsNullOrWhiteSpace(name)) return;
        var before = a.Label;
        a.Label = name.Trim();
        Annotations.Replace(a);
        Undo.Push(new DelegateAction("Rename measurement", () => { a.Label = before; Annotations.Replace(a); }, () => { a.Label = name.Trim(); Annotations.Replace(a); }));
        OnAnnotationsChanged();
    }

    private void OnExportMeasurementsCsvClick(object sender, RoutedEventArgs e) => ExportMeasurements(csv: true);
    private void OnExportMeasurementsJsonClick(object sender, RoutedEventArgs e) => ExportMeasurements(csv: false);

    // ---------- Segmentation ----------

    private sealed record SegmentItem(Segment Segment, string Text)
    {
        public override string ToString() => Text;
    }

    private LabelMap? ActiveLabels()
    {
        if (_active?.Model is not { Volume: { } v } m) return null;
        return m.Labels ??= Images.GetOrCreateLabels(v);
    }

    private void RefreshSegmentList()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            SegmentList.Items.Clear();
            var hasVolume = _active?.Model.Volume is not null;
            SegmentHint.Text = hasVolume
                ? "Brush (B), Eraser (X) and Region grow (G) edit the active segment. Shift+drag erases with the brush; Shift+wheel resizes it. Segments save automatically to local app data."
                : "Segmentation needs a volume. Open a CT, MR or PET series made of parallel slices.";
            if (_active?.Model.Labels is not { } map) return;
            foreach (var s in map.Segments)
            {
                var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb((byte)(s.Color >> 16), (byte)(s.Color >> 8), (byte)s.Color)), Margin = new Thickness(0, 0, 8, 0) };
                var text = new TextBlock { Text = s.Name + (s.Visible ? "" : "  (hidden)") };
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(swatch);
                panel.Children.Add(text);
                var item = new ListBoxItem { Content = panel, Tag = s };
                SegmentList.Items.Add(item);
                if (s.Label == map.ActiveLabel) SegmentList.SelectedItem = item;
            }
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void OnSegmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model.Labels is not { } map) return;
        if (SegmentList.SelectedItem is ListBoxItem { Tag: Segment s }) map.ActiveLabel = s.Label;
    }

    private void AddSegment()
    {
        if (ActiveLabels() is not { } map)
        {
            SetStatus("Open a volume to segment.");
            return;
        }
        var seg = map.AddSegment();
        _active!.Model.ShowLabels = true;
        Undo.Push(new DelegateAction("Add segment", () => { map.Segments.Remove(seg); RefreshSegmentList(); }, () => { map.Segments.Add(seg); RefreshSegmentList(); }));
        RefreshSegmentList();
        SegmentSection.IsExpanded = true;
        SetStatus($"Added {seg.Name}. Paint with the brush (B) or click a seed with region grow (G).");
    }

    private void OnAddSegmentClick(object sender, RoutedEventArgs e) => AddSegment();

    private async void OnRenameSegmentClick(object sender, RoutedEventArgs e)
    {
        if (SegmentList.SelectedItem is not ListBoxItem { Tag: Segment s } || _active?.Model.Labels is not { } map) return;
        var name = await PromptTextAsync("Segment name", s.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        s.Name = name.Trim();
        map.IsDirty = true;
        ScheduleSegmentationSave();
        RefreshSegmentList();
    }

    private void OnToggleSegmentClick(object sender, RoutedEventArgs e)
    {
        if (SegmentList.SelectedItem is not ListBoxItem { Tag: Segment s } || _active is null) return;
        s.Visible = !s.Visible;
        RefreshSegmentList();
        OnSegmentationChanged(_active);
    }

    private void OnClearSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model.Labels is not { } map || map.ActiveLabel == 0) return;
        var edit = map.ClearSegment(map.ActiveLabel);
        if (edit is null) return;
        Undo.Push(new DelegateAction("Clear segment", () => { map.Apply(edit, true); OnSegmentationChanged(_active); }, () => { map.Apply(edit, false); OnSegmentationChanged(_active); }));
        OnSegmentationChanged(_active);
    }

    private void OnBrushSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        if (BrushText is null) return;
        Segmentation.BrushRadiusMm = BrushSlider.Value;
        BrushText.Text = string.Create(CultureInfo.InvariantCulture, $"{BrushSlider.Value:0.#} mm");
    }

    private void OnSegOptionClick(object sender, RoutedEventArgs e)
    {
        Segmentation.Sphere = SphereCheck.IsChecked == true;
        Segmentation.UseThreshold = ThresholdCheck.IsChecked == true;
        Segmentation.GrowIn3D = Grow3DCheck.IsChecked == true;
        OnThresholdCommitted(sender, e);
    }

    private void OnThresholdCommitted(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(ThresholdLowBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) Segmentation.ThresholdLow = lo;
        if (double.TryParse(ThresholdHighBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)) Segmentation.ThresholdHigh = hi;
        ThresholdLowBox.Text = Segmentation.ThresholdLow.ToString("0.##", CultureInfo.InvariantCulture);
        ThresholdHighBox.Text = Segmentation.ThresholdHigh.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void OnThresholdFromWindowClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model is not { } m) return;
        Segmentation.ThresholdLow = m.Display.Center - m.Display.Width / 2;
        Segmentation.ThresholdHigh = m.Display.Center + m.Display.Width / 2;
        ThresholdLowBox.Text = Segmentation.ThresholdLow.ToString("0.##", CultureInfo.InvariantCulture);
        ThresholdHighBox.Text = Segmentation.ThresholdHigh.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private async void OnThresholdFillClick(object sender, RoutedEventArgs e)
    {
        if (ActiveLabels() is not { } map || _active?.Model.Volume is not { } v) return;
        OnThresholdCommitted(sender, e);
        var f = Math.Max(_active.Model.Display.ValueFactor, 1e-12);
        var lo = Segmentation.ThresholdLow / f;
        var hi = Segmentation.ThresholdHigh / f;
        var label = map.ActiveLabel;
        SetStatus("Filling intensity range…");
        var edit = await Task.Run(() => map.ThresholdFill(lo, hi, v.Data, label, onlyEmpty: true));
        if (edit is null)
        {
            SetStatus("No unlabelled voxels in that range.");
            return;
        }
        var vc = _active;
        Undo.Push(new DelegateAction("Threshold fill", () => { map.Apply(edit, true); OnSegmentationChanged(vc); }, () => { map.Apply(edit, false); OnSegmentationChanged(vc); }));
        _active.Model.ShowLabels = true;
        OnSegmentationChanged(_active);
        SetStatus(string.Create(CultureInfo.InvariantCulture, $"Added {edit.Count:N0} voxels ({edit.Count * map.VoxelVolumeMm3 / 1000:0.##} mL)."));
    }

    private async void OnInterpolateClick(object sender, RoutedEventArgs e)
    {
        if (ActiveLabels() is not { } map || _active is null) return;
        var label = map.ActiveLabel;
        var edit = await Task.Run(() => map.InterpolateSlices(label));
        if (edit is null)
        {
            SetStatus("Draw the segment on at least two separated slices first.");
            return;
        }
        var vc = _active;
        Undo.Push(new DelegateAction("Fill between slices", () => { map.Apply(edit, true); OnSegmentationChanged(vc); }, () => { map.Apply(edit, false); OnSegmentationChanged(vc); }));
        OnSegmentationChanged(_active);
        SetStatus(string.Create(CultureInfo.InvariantCulture, $"Interpolated {edit.Count:N0} voxels between drawn slices."));
    }

    private void OnLabelOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        foreach (var v in _visible)
        {
            v.Model.LabelOpacity = LabelOpacitySlider.Value;
            v.RequestRender();
        }
    }

    private async void OnSegmentStatsClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model is not { Labels: { } map, Volume: { } v } m) return;
        var pet = m.Fusion?.Pet;
        var stats = await Task.Run(() => SegmentStatistics.Compute(map, v, pet));
        SegmentStatsText.Text = string.Join("\n", stats.Select(s => s.VoxelCount == 0
            ? $"{s.Name}: empty"
            : string.Create(CultureInfo.InvariantCulture,
                $"{s.Name}: {s.VolumeMl:0.##} mL, mean {s.Mean:0.#} ± {s.StdDev:0.#} {s.Unit}{(s.PetSuvMax is { } sm ? $", SUVmax {sm:0.00}, SUVmean {s.PetSuvMean:0.00}" : "")}")));
    }

    private void ScheduleSegmentationSave()
    {
        _segSaveTimer.Stop();
        _segSaveTimer.Start();
    }

    private void SaveDirtySegmentations()
    {
        foreach (var map in Images.LabelMaps.Where(m => m.IsDirty).ToList())
        {
            var volume = Images.FindVolume(map.DisplaySetId);
            if (volume is null) continue;
            try
            {
                LabelMapIo.SaveNrrd(map, SegmentationPaths.For(volume.Source));
                map.IsDirty = false;
            }
            catch (IOException ex)
            {
                SetStatus("Could not save segmentation: " + ex.Message);
            }
        }
    }

    // ---------- Fusion ----------

    private void RefreshPetCandidates()
    {
        var current = (PetCombo.SelectedItem as PetItem)?.Set?.Id;
        PetCombo.Items.Clear();
        var study = _active?.Model.Set?.Series.StudyInstanceUid;
        foreach (var s in Library.Snapshot().Where(st => study is null || st.StudyInstanceUid == study).SelectMany(st => st.Series).Where(s => s.Modality == "PT" && s.IsDisplayable))
        {
            if (!_displaySets.TryGetValue(s.Key, out var sets))
            {
                PetCombo.Items.Add(new PetItem(null, s, $"{s.DisplayName} ({s.FrameCount} images)"));
                continue;
            }
            foreach (var d in sets.Where(d => d.IsVolume)) PetCombo.Items.Add(new PetItem(d, s, $"{s.DisplayName} · {d.Label}"));
        }
        PetCombo.SelectedItem = PetCombo.Items.OfType<PetItem>().FirstOrDefault(i => i.Set?.Id == current) ?? PetCombo.Items.OfType<PetItem>().FirstOrDefault();
    }

    private void RefreshFusionPanel()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            if (PetCombo.Items.Count == 0 || (_active?.Model.Set?.Series.StudyInstanceUid is { } uid && PetCombo.Items.OfType<PetItem>().All(i => i.Set is null || i.Set.Series.StudyInstanceUid != uid)))
                RefreshPetCandidates();
            var f = _active?.Model.Fusion;
            var pet = f?.Pet ?? (_active?.Model.Volume is { Suv: not null } vol ? vol : null);
            if (f is not null)
            {
                FusionOpacitySlider.Value = f.Opacity;
                FusionColormapCombo.SelectedItem = f.Colormap;
                FusionLowBox.Text = f.Lower.ToString("0.##", CultureInfo.InvariantCulture);
                FusionHighBox.Text = f.Upper.ToString("0.##", CultureInfo.InvariantCulture);
                FusionRangeLabel.Text = $"Range ({f.Unit})";
            }
            SuvText.Text = pet?.Suv is { } suv ? SuvDetails(suv) : "Open or fuse a PET series to see SUV validation.";
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private static string SuvDetails(Core.Pet.SuvInfo suv)
    {
        var ci = CultureInfo.InvariantCulture;
        if (!suv.IsAvailable) return "SUV unavailable. " + string.Join(" ", suv.Problems) + " Values are shown in " + suv.ActivityUnit + ".";
        var lines = new List<string>
        {
            "SUVbw available.",
            string.Create(ci, $"Injected dose {suv.InjectedDoseBq / 1e6:0.##} MBq of {suv.Radionuclide}, half-life {suv.HalfLifeS / 60:0.##} min."),
            string.Create(ci, $"Decay {suv.DecayTimeS / 60:0.#} min to the {suv.ReferenceTimeSource}; decay correction {suv.DecayCorrection}."),
        };
        if (suv.LeanBodyMassRatio is { } r) lines.Add(string.Create(ci, $"SUL (lean body mass) = SUVbw × {r:0.###}."));
        else lines.Add("SUL needs patient height and sex.");
        lines.AddRange(suv.Notes);
        return string.Join("\n", lines);
    }

    private async void OnFuseClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model is not { Volume: not null } m)
        {
            SetStatus("Fusion overlays PET on a volume in the active viewport.");
            return;
        }
        if (PetCombo.SelectedItem is not PetItem item) return;
        var set = item.Set;
        if (set is null)
        {
            var sets = await DisplaySetsFor(item.Series);
            set = sets.FirstOrDefault(d => d.IsVolume);
            RefreshPetCandidates();
            if (set is null)
            {
                SetStatus("That PET series is not a volume.");
                return;
            }
        }
        if (set.FrameOfReferenceUid != m.FrameOfReferenceUid)
            SetStatus("Warning: the PET series uses a different frame of reference; alignment relies on scanner coordinates and may be off.");
        var pet = Images.AcquireVolume(set, set.Geometry!.Nz / 2);
        HookVolumeProgress(pet);
        m.Fusion = NewFusion(pet);
        FusionSection.IsExpanded = true;
        _active.RequestRender();
        RefreshFusionPanel();
    }

    private void OnUnfuseClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model.Fusion is not { } f) return;
        Images.Release(f.Pet);
        _active.Model.Fusion = null;
        _active.RequestRender();
        RefreshFusionPanel();
    }

    private void OnPetCtLayoutClick(object sender, RoutedEventArgs e) => _ = ApplyPetCtLayoutAsync();

    private void OnFusionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model.Fusion is not { } f) return;
        f.Opacity = FusionOpacitySlider.Value;
        _active.RequestRender();
    }

    private void OnFusionColormapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model.Fusion is not { } f || FusionColormapCombo.SelectedItem is not Colormap c) return;
        f.Colormap = c;
        _active.RequestRender();
    }

    private void OnFusionRangeCommitted(object sender, RoutedEventArgs e)
    {
        if (_syncing || _active?.Model.Fusion is not { } f) return;
        if (double.TryParse(FusionLowBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) f.Lower = lo;
        if (double.TryParse(FusionHighBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) && hi > f.Lower) f.Upper = hi;
        _active.RequestRender();
    }

    // ---------- 3D ----------

    private void OnVrModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m) return;
        m.Vr.Mode = (VrMode)Math.Max(0, VrModeCombo.SelectedIndex);
        _active.RequestRender();
    }

    private void OnVrPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m || VrPresetCombo.SelectedItem is not string name) return;
        m.Vr.PresetName = name;
        m.Vr.Mode = VrMode.Preset;
        VrModeCombo.SelectedIndex = 0;
        _active.RequestRender();
    }

    private void OnVrSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m) return;
        m.Vr.OpacityShift = VrShiftSlider.Value * (m.Volume?.Suv is { IsAvailable: true } ? 0.01 : 1);
        m.Vr.OpacityScale = VrScaleSlider.Value;
        _active.RequestRender();
    }

    private void OnVrOptionClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model is not { } m) return;
        m.Vr.Shade = VrShadeCheck.IsChecked == true;
        m.Vr.InvertProjection = VrInvertCheck.IsChecked == true;
        m.Vr.ShowLabels = VrLabelsCheck.IsChecked == true;
        _active.RequestRender();
    }

    private void OnCropChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        if (_syncing || _active?.Model is not { } m || sender is not Slider { Tag: int i } s) return;
        if (i % 2 == 0) m.Vr.CropMin[i / 2] = Math.Min(s.Value, m.Vr.CropMax[i / 2] - 0.02);
        else m.Vr.CropMax[i / 2] = Math.Max(s.Value, m.Vr.CropMin[i / 2] + 0.02);
        _active.RequestRender();
    }

    private void OnGpuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.GpuPreference.Set(GpuCheck.IsChecked == true);
            SetStatus("GPU preference saved. Restart Better DICOM Viewer for 3D rendering to switch GPUs (Windows Settings › Display › Graphics shows the same setting).");
        }
        catch (Exception ex)
        {
            SetStatus("Could not change the GPU preference: " + ex.Message);
        }
    }

    private void OnVrViewClick(object sender, RoutedEventArgs e)
    {
        if (_active?.Model is not { } m || sender is not Button { Tag: string tag }) return;
        var (dir, up) = tag switch
        {
            "A" => (new Core.Geometry.Vec3(0, 1, 0), Core.Geometry.Vec3.UnitZ),
            "P" => (new Core.Geometry.Vec3(0, -1, 0), Core.Geometry.Vec3.UnitZ),
            "L" => (new Core.Geometry.Vec3(-1, 0, 0), Core.Geometry.Vec3.UnitZ),
            "R" => (new Core.Geometry.Vec3(1, 0, 0), Core.Geometry.Vec3.UnitZ),
            "S" => (new Core.Geometry.Vec3(0, 0, -1), new Core.Geometry.Vec3(0, -1, 0)),
            _ => (new Core.Geometry.Vec3(0, 0, 1), new Core.Geometry.Vec3(0, -1, 0)),
        };
        m.Vr.ResetCamera(dir, up);
        if (!m.Is3D) Toggle3D();
        _active.RequestRender();
    }
}
