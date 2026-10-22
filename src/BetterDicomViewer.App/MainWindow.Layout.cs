using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BetterDicomViewer.App.Viewer;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Core.Viewing;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.App;

public enum LayoutKind
{
    Single,
    SideBySide,
    Grid2x2,
    Mpr,
    Mpr3D,
    PetCt,
}

public partial class MainWindow
{
    private LayoutKind _layout = LayoutKind.Single;
    private readonly Dictionary<(int, int), double> _linkOffsets = [];

    public void ApplyLayout(LayoutKind kind)
    {
        _layout = kind;
        _maximized = null;
        var g = ViewportGrid;
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        _visible.Clear();

        void Place(int index, int row, int col, int rowSpan = 1, int colSpan = 1)
        {
            var v = _pool[index];
            Grid.SetRow(v, row);
            Grid.SetColumn(v, col);
            Grid.SetRowSpan(v, rowSpan);
            Grid.SetColumnSpan(v, colSpan);
            v.Margin = new Thickness(1);
            g.Children.Add(v);
            _visible.Add(v);
        }

        void Define(int rows, int cols, double[]? colWeights = null)
        {
            for (var r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (var c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(colWeights?[c] ?? 1, GridUnitType.Star) });
        }

        switch (kind)
        {
            case LayoutKind.Single:
                Define(1, 1);
                Place(0, 0, 0);
                break;
            case LayoutKind.SideBySide:
                Define(1, 2);
                Place(0, 0, 0);
                Place(1, 0, 1);
                break;
            case LayoutKind.Mpr:
                Define(2, 2, [1.6, 1]);
                Place(0, 0, 0, 2);
                Place(1, 0, 1);
                Place(2, 1, 1);
                break;
            default:
                Define(2, 2);
                Place(0, 0, 0);
                Place(1, 0, 1);
                Place(2, 1, 0);
                Place(3, 1, 1);
                break;
        }

        foreach (var v in _pool.Except(_visible)) v.Model.CinePlaying = false;
        if (_active is null || !_visible.Contains(_active)) Activate(_visible[0]);
        RenderAll();
    }

    public void ToggleMaximize(ViewportControl viewport)
    {
        if (_maximized is not null)
        {
            ApplyLayout(_layout);
            Activate(viewport);
            return;
        }
        if (_visible.Count == 1) return;
        foreach (var v in _visible)
            if (!ReferenceEquals(v, viewport)) v.Visibility = Visibility.Collapsed;
        Grid.SetRow(viewport, 0);
        Grid.SetColumn(viewport, 0);
        Grid.SetRowSpan(viewport, Math.Max(1, ViewportGrid.RowDefinitions.Count));
        Grid.SetColumnSpan(viewport, Math.Max(1, ViewportGrid.ColumnDefinitions.Count));
        _maximized = viewport;
        foreach (var v in _visible) if (!ReferenceEquals(v, viewport)) ViewportGrid.Children.Remove(v);
        foreach (var v in _pool) v.Visibility = Visibility.Visible;
        _visible.Clear();
        _visible.Add(viewport);
        viewport.RequestRender();
    }

    private void NextViewport()
    {
        if (_visible.Count < 2 || _active is null) return;
        var i = _visible.IndexOf(_active);
        Activate(_visible[(i + 1) % _visible.Count]);
    }

    /// <summary>Shows the active volume as axial, coronal and sagittal views (and 3D) linked by a shared cursor.</summary>
    private void ApplyMprLayout(bool with3D)
    {
        var source = _active?.Model.Volume is not null ? _active : _visible.FirstOrDefault(v => v.Model.Volume is not null);
        if (source?.Model.Set is not { IsVolume: true } set)
        {
            SetStatus("MPR needs a volume: open a series made of parallel slices first.");
            ApplyLayout(with3D ? LayoutKind.Mpr3D : LayoutKind.Mpr);
            return;
        }
        if (set.IsColor)
        {
            SetStatus("MPR is not available for colour images.");
            return;
        }
        var display = source.Model.Display.Clone();
        var fusion = source.Model.Fusion;
        ApplyLayout(with3D ? LayoutKind.Mpr3D : LayoutKind.Mpr);
        var planes = new[] { PlaneKind.Axial, PlaneKind.Coronal, PlaneKind.Sagittal };
        for (var i = 0; i < 3; i++)
        {
            LoadDisplaySet(_pool[i], set, planes[i], quiet: true);
            _pool[i].Model.Display = display.Clone();
            _pool[i].Model.Fusion = fusion is null ? null : CloneFusion(fusion);
        }
        if (with3D)
        {
            LoadDisplaySet(_pool[3], set, PlaneKind.Coronal, quiet: true);
            _pool[3].Model.Display = display.Clone();
            _pool[3].Model.Is3D = true;
            _pool[3].Model.Vr.ResetCamera(new Vec3(0, 1, 0), Vec3.UnitZ);
            if (set.Modality == "PT")
            {
                _pool[3].Model.Vr.Mode = VrMode.Mip;
                _pool[3].Model.Vr.InvertProjection = true;
            }
        }
        var center = set.Geometry!.Center;
        foreach (var v in _visible) v.CursorWorld = center;
        Activate(_pool[0]);
        RenderAll();
        RefreshInspector();
        SetStatus("MPR: scroll each view independently; the crosshair tool (C) moves all views to a point and its line handles tilt planes for oblique views.");
    }

    private static FusionState CloneFusion(FusionState f)
    {
        f.Pet.Acquire();
        return CloneFusionState(f);
    }

    private static FusionState CloneFusionState(FusionState f) => new()
    {
        Pet = f.Pet,
        Opacity = f.Opacity,
        Colormap = f.Colormap,
        Lower = f.Lower,
        Upper = f.Upper,
        Visible = f.Visible,
    };

    private async Task ApplyPetCtLayoutAsync()
    {
        var studyUid = _active?.Model.Set?.Series.StudyInstanceUid;
        var studies = Library.Snapshot();
        var study = studies.FirstOrDefault(s => s.StudyInstanceUid == studyUid && s.Series.Any(x => x.Modality == "PT"))
                    ?? studies.FirstOrDefault(s => s.Series.Any(x => x.Modality == "PT") && s.Series.Any(x => x.Modality == "CT"));
        if (study is null)
        {
            SetStatus("PET/CT layout needs a study containing both CT and PET series.");
            return;
        }
        SetStatus("Preparing PET/CT…");
        var (ct, pet) = await PickPetCtPair(study);
        if (ct is null || pet is null)
        {
            SetStatus("Could not find a CT volume and an attenuation-corrected PET volume in this study.");
            return;
        }
        ApplyLayout(LayoutKind.PetCt);
        LoadDisplaySet(_pool[0], ct, PlaneKind.Axial, quiet: true);
        LoadDisplaySet(_pool[1], pet, PlaneKind.Axial, quiet: true);
        LoadDisplaySet(_pool[2], ct, PlaneKind.Axial, quiet: true);
        var petVolume = _pool[1].Model.Volume!;
        _pool[2].Model.Fusion = NewFusion(petVolume);
        LoadDisplaySet(_pool[3], pet, PlaneKind.Coronal, quiet: true);
        var mip = _pool[3].Model;
        mip.Is3D = true;
        mip.Vr.Mode = VrMode.Mip;
        mip.Vr.InvertProjection = true;
        mip.Vr.ResetCamera(new Vec3(0, 1, 0), Vec3.UnitZ);
        foreach (var v in _visible) v.CursorWorld = ct.Geometry!.Center;
        Activate(_pool[2]);
        CaptureLinkOffsets();
        RenderAll();
        RefreshInspector();
        FusionSection.IsExpanded = true;
        var suv = petVolume.Suv;
        SetStatus(suv is { IsAvailable: true }
            ? "PET/CT: CT, PET (SUV), fused and rotating-ready PET MIP. Scrolling is linked; double-click the MIP to jump to a hot spot."
            : "PET/CT loaded. SUV is unavailable for this PET series: " + string.Join(" ", suv?.Problems ?? []));
    }

    private async Task<(DisplaySet? Ct, DisplaySet? Pet)> PickPetCtPair(LibraryStudy study)
    {
        var petSets = new List<DisplaySet>();
        var ctSets = new List<DisplaySet>();
        foreach (var s in study.Series.Where(s => s.IsDisplayable && s.Modality is "PT" or "CT"))
        {
            var sets = await DisplaySetsFor(s);
            foreach (var d in sets.Where(d => d.IsVolume && !d.IsColor))
            {
                if (s.Modality == "PT") petSets.Add(d);
                else ctSets.Add(d);
            }
        }
        var pet = petSets
            .OrderByDescending(d => d.Representative.Pet?.Units == "BQML")
            .ThenByDescending(d => d.Representative.Pet?.CorrectedImage.Contains("ATTN") == true)
            .ThenByDescending(d => d.Representative.ImageType.Contains("ORIGINAL"))
            .ThenBy(d => d.Geometry!.Normal.Z < 0.9)
            .ThenByDescending(d => d.SliceCount)
            .FirstOrDefault();
        if (pet is null) return (null, null);
        var ct = ctSets
            .OrderByDescending(d => d.FrameOfReferenceUid == pet.FrameOfReferenceUid)
            .ThenBy(d => Math.Abs(d.Geometry!.Normal.Z) < 0.9)
            .ThenByDescending(d => d.SliceCount)
            .FirstOrDefault();
        return (ct, pet);
    }

    /// <summary>Creates a fusion layer that owns one lease on the PET volume (released with the viewport content).</summary>
    public FusionState NewFusion(ImageVolume pet)
    {
        pet.Acquire();
        return NewFusionState(pet);
    }

    private static FusionState NewFusionState(ImageVolume pet) => new()
    {
        Pet = pet,
        Opacity = 0.5,
        Colormap = Colormap.HotIron,
        Lower = pet.Suv is { IsAvailable: true } ? 1.5 : pet.ValueRange.Max * 0.1,
        Upper = pet.Suv is { IsAvailable: true } ? 8 : pet.ValueRange.Max * 0.7,
    };

    private void SetPlane(PlaneKind plane)
    {
        if (_active?.Model is not { } m || m.Volume is null)
        {
            SetStatus("Reformatting needs a volume.");
            return;
        }
        if (m.Set!.IsColor && plane != PlaneKind.Acquisition) return;
        m.Is3D = false;
        var point = m.View.Center;
        m.ConfigurePlane(plane);
        m.MovePlaneThrough(point);
        m.Fit();
        _active.RequestRender();
        OnViewChanged(_active, ViewChange.Content);
    }

    private void Toggle3D()
    {
        if (_active?.Model is not { } m || m.Volume is null)
        {
            SetStatus("3D rendering needs a volume.");
            return;
        }
        if (m.Set!.IsColor)
        {
            SetStatus("3D rendering is not available for colour images.");
            return;
        }
        m.Is3D = !m.Is3D;
        if (m.Is3D)
        {
            m.Vr.ResetCamera(new Vec3(0, 1, 0), Vec3.UnitZ);
            if (m.Set.Modality == "PT")
            {
                m.Vr.Mode = VrMode.Mip;
                m.Vr.InvertProjection = true;
            }
            else if (m.Vr.Mode == VrMode.Preset)
            {
                m.Vr.PresetName = VrPresets.CtSoftTissue.Name;
            }
            VrSection.IsExpanded = true;
        }
        _active.RequestRender();
        OnViewChanged(_active, ViewChange.Content);
    }

    private void ResetActiveView()
    {
        if (_active?.Model is not { HasContent: true } m) return;
        if (m.Is3D)
        {
            m.Vr.ResetCamera(new Vec3(0, 1, 0), Vec3.UnitZ);
        }
        else
        {
            if (m.Volume is not null) m.ConfigurePlane(m.Plane);
            else m.View.SetPlane(Vec3.UnitX, Vec3.UnitY);
            m.View.SlabMode = SlabMode.None;
            ApplyDefaultDisplay(m);
            m.Fit();
        }
        _active.RequestRender();
        OnViewChanged(_active, ViewChange.Window);
        RefreshInspector();
    }

    /// <summary>
    /// Initial alignment for viewports in different frames of reference: the same patient position when the stacks
    /// overlap along the view normal (typical for prior and current scans of one patient), otherwise their centres.
    /// </summary>
    private void InitializeLinkOffsets()
    {
        _linkOffsets.Clear();
        for (var i = 0; i < _visible.Count; i++)
        for (var j = 0; j < _visible.Count; j++)
        {
            if (i == j) continue;
            var a = _visible[i].Model;
            var b = _visible[j].Model;
            if (a.Volume is null || b.Volume is null) continue;
            if (Math.Abs(Vec3.Dot(a.View.Normal, b.View.Normal)) < 0.99) continue;
            var n = a.View.Normal;
            var ra = a.Volume.Source.Geometry!.Corners().Select(c => Vec3.Dot(c, n)).ToList();
            var rb = b.Volume.Source.Geometry!.Corners().Select(c => Vec3.Dot(c, n)).ToList();
            var overlap = Math.Min(ra.Max(), rb.Max()) - Math.Max(ra.Min(), rb.Min());
            _linkOffsets[(a.Index, b.Index)] = overlap > 0 ? 0 : Vec3.Dot(b.View.Center, n) - Vec3.Dot(a.View.Center, n);
        }
        if (_active is not null && _active.Model.Volume is not null) PropagateSlice(_active);
    }

    private void CaptureLinkOffsets()
    {
        _linkOffsets.Clear();
        for (var i = 0; i < _visible.Count; i++)
        for (var j = 0; j < _visible.Count; j++)
        {
            if (i == j) continue;
            var a = _visible[i].Model;
            var b = _visible[j].Model;
            if (a.Volume is null || b.Volume is null) continue;
            if (Math.Abs(Vec3.Dot(a.View.Normal, b.View.Normal)) < 0.99) continue;
            var n = a.View.Normal;
            _linkOffsets[(a.Index, b.Index)] = Vec3.Dot(b.View.Center, n) - Vec3.Dot(a.View.Center, n);
        }
    }

    /// <summary>
    /// Keeps parallel views on the same anatomical level: views in the same frame of reference pass through the same
    /// patient point; views from other studies keep the offset they had when linking was enabled or content changed.
    /// </summary>
    private void PropagateSlice(ViewportControl source)
    {
        var s = source.Model;
        if (s.Volume is null)
        {
            if (_link && s.Frames is not null)
            {
                foreach (var v in _visible.Where(v => !ReferenceEquals(v, source) && v.Model.Frames is not null && v.Model.Set?.Frames.Count == s.Set?.Frames.Count))
                {
                    v.Model.FrameIndex = s.FrameIndex;
                    v.RequestRender();
                }
            }
            return;
        }
        foreach (var v in _visible)
        {
            if (ReferenceEquals(v, source) || v.Model.Volume is null || v.Model.Is3D) continue;
            var t = v.Model;
            var parallel = Math.Abs(Vec3.Dot(t.View.Normal, s.View.Normal)) > 0.99;
            var sameFrame = t.FrameOfReferenceUid is not null && t.FrameOfReferenceUid == s.FrameOfReferenceUid;
            if (sameFrame && parallel)
            {
                t.MovePlaneThrough(s.View.Center);
            }
            else if (_link && parallel && _linkOffsets.TryGetValue((s.Index, t.Index), out var offset))
            {
                var n = s.View.Normal;
                var target = Vec3.Dot(s.View.Center, n) + offset;
                t.View.Center += n * (target - Vec3.Dot(t.View.Center, n));
                t.SnapToVoxelPlane();
            }
            else if (sameFrame)
            {
                v.CursorWorld = s.View.Center;
            }
            v.RequestRender();
        }
        if (ReferenceEquals(source, _active)) RefreshSliceInfo();
    }

    private void PropagateWindow(ViewportControl source)
    {
        var s = source.Model;
        foreach (var v in _visible)
        {
            if (ReferenceEquals(v, source) || v.Model.Set is null || s.Set is null) continue;
            if (v.Model.Set.Id != s.Set.Id) continue;
            v.Model.Display = s.Display.Clone();
            v.RequestRender();
        }
        source.RequestRender();
    }

    private void StepSeries(int delta)
    {
        if (_active?.Model.Set is not { } current) return;
        var series = FindSeries(current.Series);
        if (series is null) return;
        if (_displaySets.TryGetValue(current.Series, out var sets) && sets.Count > 1)
        {
            var i = sets.FindIndex(s => s.Id == current.Id) + delta;
            if (i >= 0 && i < sets.Count)
            {
                LoadDisplaySet(_active, sets[i], _active.Model.Plane);
                return;
            }
        }
        var all = _studies.SelectMany(s => s.Series).Where(s => s.IsDisplayable).ToList();
        var idx = all.FindIndex(s => s.Key == current.Series);
        var next = idx + delta;
        if (next < 0 || next >= all.Count) return;
        _ = LoadSeriesAsync(_active, all[next]);
    }

    private void ToggleCine()
    {
        if (_active?.Model is not { HasContent: true } m || m.Is3D) return;
        m.CinePlaying = !m.CinePlaying;
        if (m.CinePlaying)
        {
            _cineTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, m.CineFps));
            _cineTimer.Start();
        }
        else if (!_visible.Any(v => v.Model.CinePlaying))
        {
            _cineTimer.Stop();
        }
        CineGlyph.Text = m.CinePlaying ? "" : "";
        _active.RequestRender();
    }

    private void OnCineTick(object? sender, EventArgs e)
    {
        var any = false;
        foreach (var v in _visible.Where(v => v.Model.CinePlaying))
        {
            any = true;
            var m = v.Model;
            var (k, count) = m.SliceIndex();
            if (m.Frames is { } frames && frames.TryGet((k + 1) % count) is null) continue;
            m.GoToSlice((k + 1) % Math.Max(count, 1));
            v.RequestRender();
            if (ReferenceEquals(v, _active)) RefreshSliceInfo();
        }
        if (!any) _cineTimer.Stop();
    }

    private void CycleInterpolation()
    {
        WithActive(v =>
        {
            v.Model.Interpolation = v.Model.Interpolation switch
            {
                Interpolation.Nearest => Interpolation.Linear,
                Interpolation.Linear => Interpolation.Cubic,
                _ => Interpolation.Nearest,
            };
            SetStatus("Interpolation: " + v.Model.Interpolation.ToString().ToLowerInvariant());
        });
        RefreshInspector();
    }

    private void CycleSlab()
    {
        WithActive(v =>
        {
            if (v.Model.Volume is null) return;
            var view = v.Model.View;
            view.SlabMode = view.SlabMode switch
            {
                SlabMode.None => SlabMode.Mean,
                SlabMode.Mean => SlabMode.Max,
                SlabMode.Max => SlabMode.Min,
                _ => SlabMode.None,
            };
            if (view.SlabThicknessMm <= 0) view.SlabThicknessMm = 10;
            SetStatus(view.SlabMode == SlabMode.None ? "Thick slab off." : string.Create(CultureInfo.InvariantCulture, $"Thick slab: {view.SlabMode} over {view.SlabThicknessMm:0.#} mm."));
        });
        RefreshInspector();
    }

    private void AdjustSlab(double factor)
    {
        WithActive(v =>
        {
            var view = v.Model.View;
            if (view.SlabMode == SlabMode.None) view.SlabMode = SlabMode.Max;
            view.SlabThicknessMm = Math.Clamp((view.SlabThicknessMm <= 0 ? 10 : view.SlabThicknessMm) * factor, 1, 300);
            SetStatus(string.Create(CultureInfo.InvariantCulture, $"Slab {view.SlabThicknessMm:0.#} mm."));
        });
        RefreshInspector();
    }

    private void ToggleFusionVisibility()
    {
        if (_active?.Model.Fusion is { } f)
        {
            f.Visible = !f.Visible;
            _active.RequestRender();
            return;
        }
        if (_active?.Model.Volume is not null && PetCombo.Items.Count > 0)
        {
            OnFuseClick(this, new RoutedEventArgs());
            return;
        }
        SetStatus("Open a CT volume and a PET series from the same study, then use PET/CT fusion in the inspector or Ctrl+6.");
    }

    private void AdjustFusion(double delta)
    {
        if (_active?.Model.Fusion is not { } f) return;
        f.Opacity = Math.Clamp(f.Opacity + delta, 0, 1);
        FusionOpacitySlider.Value = f.Opacity;
        _active.RequestRender();
    }

    private void ResizeBrush(double factor)
    {
        Segmentation.BrushRadiusMm = Math.Clamp(Segmentation.BrushRadiusMm * factor, 0.5, 100);
        BrushSlider.Value = Math.Min(BrushSlider.Maximum, Segmentation.BrushRadiusMm);
        SetStatus(string.Create(CultureInfo.InvariantCulture, $"Brush radius {Segmentation.BrushRadiusMm:0.#} mm"));
    }

    public sealed record PresetEntry(string Name, double Center, double Width, double? Factor = null)
    {
        public override string ToString() => Name;
    }

    public List<PresetEntry> PresetsFor(ViewportModel m)
    {
        var list = new List<PresetEntry>();
        if (m.Set is null || m.Set.IsColor) return list;
        var inst = m.Set.Representative;
        foreach (var w in VolumeLoader.DicomWindowPresets(inst)) list.Add(new PresetEntry(w.Name, w.Center, w.Width));
        if (m.Volume?.Suv is { IsAvailable: true } suv)
        {
            var f = suv.Factor(m.Volume.Nz / 2) ?? 1;
            list.Clear();
            foreach (var hi in new[] { 6.0, 3, 5, 8, 10, 15, 20 })
                list.Add(new PresetEntry(string.Create(CultureInfo.InvariantCulture, $"SUV 0–{hi:0}"), hi / 2, hi, f));
        }
        else if (m.ValueUnit == "HU")
        {
            list.Add(new PresetEntry("Soft tissue", 40, 400));
            list.Add(new PresetEntry("Lung", -600, 1500));
            list.Add(new PresetEntry("Bone", 400, 1800));
            list.Add(new PresetEntry("Brain", 40, 80));
            list.Add(new PresetEntry("Liver", 60, 160));
            list.Add(new PresetEntry("Mediastinum", 50, 350));
            list.Add(new PresetEntry("Angio", 300, 600));
        }
        list.Add(new PresetEntry("Full range", double.NaN, double.NaN));
        return list.DistinctBy(p => p.Name).Take(9).ToList();
    }

    private void ApplyWindowPreset(int number)
    {
        if (_active?.Model is not { HasContent: true } m) return;
        var presets = PresetsFor(m);
        if (number > presets.Count) return;
        ApplyPreset(m, presets[number - 1]);
        PropagateWindow(_active);
        RefreshDisplayControls();
        SetStatus($"Window preset {number}: {presets[number - 1].Name}");
    }

    private void ApplyPreset(ViewportModel m, PresetEntry p)
    {
        if (double.IsNaN(p.Center))
        {
            var (lo, hi) = m.Volume?.ValueRange ?? (0, 255);
            var f = m.Display.ValueFactor;
            m.Display.Center = (lo + hi) / 2 * f;
            m.Display.Width = Math.Max((hi - lo) * f, 1e-3);
            return;
        }
        m.Display.Center = p.Center;
        m.Display.Width = p.Width;
        if (p.Factor is { } factor) m.Display.ValueFactor = factor;
    }
}
