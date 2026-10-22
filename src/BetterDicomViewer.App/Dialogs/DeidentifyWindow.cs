using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDicomViewer.Core.Deidentification;
using Microsoft.Win32;

namespace BetterDicomViewer.App.Dialogs;

/// <summary>Export dialog for de-identified copies. The scope, options and limits are stated before anything is written.</summary>
public sealed class DeidentifyWindow : Window
{
    private readonly MainWindow _main;
    private readonly ComboBox _scope = new();
    private readonly TextBox _name = new() { Text = "ANONYMOUS" };
    private readonly TextBox _id = new() { Text = "ANON0001" };
    private readonly ComboBox _dates = new();
    private readonly TextBox _shift = new() { Text = "-365", Width = 80 };
    private readonly CheckBox _patientChars = new() { Content = "Keep age, sex, height and weight (needed for SUV)" };
    private readonly CheckBox _device = new() { Content = "Keep device identity (station, serial numbers)" };
    private readonly CheckBox _institution = new() { Content = "Keep institution name and address" };
    private readonly CheckBox _descriptions = new() { Content = "Keep study and series descriptions (not part of the profile; review them)" };
    private readonly ComboBox _burnedIn = new();
    private readonly TextBox _folder = new() { IsReadOnly = true };
    private readonly TextBlock _scopeText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ProgressBar _progress = new() { Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _export = new() { Content = "Export", Padding = new Thickness(16, 5, 16, 5) };
    private readonly Button _open = new() { Content = "Open folder", Padding = new Thickness(12, 5, 12, 5), Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0) };
    private CancellationTokenSource? _cts;

    public DeidentifyWindow(MainWindow main, string? outputFolder = null)
    {
        _main = main;
        Title = "Export de-identified copy";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _scope.Items.Add("Active series");
        _scope.Items.Add("Active study");
        _scope.Items.Add("All open studies");
        _scope.SelectedIndex = 1;
        _scope.SelectionChanged += (_, _) => UpdateScope();
        _dates.Items.Add("Remove dates and times (Basic Profile)");
        _dates.Items.Add("Shift all dates by a fixed number of days");
        _dates.Items.Add("Keep full dates");
        _dates.SelectedIndex = 0;
        _shift.IsEnabled = false;
        _dates.SelectionChanged += (_, _) => _shift.IsEnabled = _dates.SelectedIndex == 1;
        System.Windows.Automation.AutomationProperties.SetName(_shift, "Days to shift dates");
        System.Windows.Automation.AutomationProperties.SetName(_name, "Replacement patient name");
        System.Windows.Automation.AutomationProperties.SetName(_id, "Replacement patient ID");
        _burnedIn.Items.Add("Exclude images that may contain burned-in text (safest)");
        _burnedIn.Items.Add("Export them unchanged and list them in the report");
        _burnedIn.Items.Add("Black out the redaction rectangles I drew, then export");
        _burnedIn.SelectedIndex = main.Redactions.Any(r => r.Value.Count > 0) ? 2 : 0;
        _patientChars.IsChecked = main.InstancesForScope(1).Any(i => i.Modality == "PT");
        _folder.Text = outputFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "De-identified DICOM", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Creates new files with the DICOM PS3.15 Basic Application Level Confidentiality Profile applied. Original scans are read only.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Label("What to export"));
        panel.Children.Add(_scope);
        panel.Children.Add(_scopeText);
        panel.Children.Add(Label("Replacement patient name and ID"));
        var ids = new Grid();
        ids.ColumnDefinitions.Add(new ColumnDefinition());
        ids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ids.ColumnDefinitions.Add(new ColumnDefinition());
        ids.Children.Add(_name);
        Grid.SetColumn(_id, 2);
        ids.Children.Add(_id);
        panel.Children.Add(ids);
        panel.Children.Add(Label("Dates"));
        var dates = new DockPanel();
        DockPanel.SetDock(_shift, Dock.Right);
        _shift.Margin = new Thickness(8, 0, 0, 0);
        dates.Children.Add(_shift);
        dates.Children.Add(_dates);
        panel.Children.Add(dates);
        panel.Children.Add(Label("Options"));
        foreach (var c in new[] { _patientChars, _device, _institution, _descriptions })
        {
            c.Margin = new Thickness(0, 2, 0, 2);
            panel.Children.Add(c);
        }
        panel.Children.Add(Label("Burned-in text in pixel data"));
        panel.Children.Add(_burnedIn);
        var regions = main.Redactions.Sum(r => r.Value.Count);
        panel.Children.Add(Hint(regions == 0
            ? "No redaction rectangles are defined. Use \"Draw redaction region\" in the Export panel on an ultrasound or screen-capture image."
            : $"{regions} redaction rectangle(s) defined for {main.Redactions.Count} image size(s)."));
        panel.Children.Add(Label("Output folder (must be empty and outside the scan folders)"));
        var folder = new DockPanel();
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose an empty export folder" };
            if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        folder.Children.Add(browse);
        folder.Children.Add(_folder);
        panel.Children.Add(folder);
        panel.Children.Add(Hint("Limits: removing attributes does not guarantee anonymity. Burned-in text is only removed where you drew rectangles, and faces in 3D head reconstructions are not removed. Review the report and the images before sharing."));
        panel.Children.Add(_progress);
        panel.Children.Add(_result);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _export.Style = (Style)FindResource("AccentButtonStyle");
        _export.Click += async (_, _) => await RunAsync();
        _open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder.Text}\"") { UseShellExecute = true });
        var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) =>
        {
            _cts?.Cancel();
            Close();
        };
        buttons.Children.Add(_export);
        buttons.Children.Add(_open);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UpdateScope();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0),
        Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
    };

    private void UpdateScope()
    {
        var items = _main.InstancesForScope(_scope.SelectedIndex);
        var images = items.Count(i => i.HasPixelData);
        var suspect = items.Count(i => i.HasPixelData && BurnedInAssessment.Assess(i).Suspect);
        _scopeText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{items.Count:N0} object(s) in {items.Select(i => i.SeriesInstanceUid).Distinct().Count()} series: {images:N0} image(s), {items.Count - images:N0} non-image object(s) (not exported), {suspect:N0} image(s) that may contain burned-in text.");
    }

    private async Task RunAsync()
    {
        var options = new DeidentificationOptions
        {
            PatientName = string.IsNullOrWhiteSpace(_name.Text) ? "ANONYMOUS" : _name.Text.Trim(),
            PatientId = string.IsNullOrWhiteSpace(_id.Text) ? "ANON0001" : _id.Text.Trim(),
            Dates = (DateHandling)_dates.SelectedIndex,
            DateShiftDays = int.TryParse(_shift.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : -365,
            RetainPatientCharacteristics = _patientChars.IsChecked == true,
            RetainDeviceIdentity = _device.IsChecked == true,
            RetainInstitutionIdentity = _institution.IsChecked == true,
            RetainDescriptions = _descriptions.IsChecked == true,
            BurnedIn = (BurnedInPolicy)_burnedIn.SelectedIndex,
            Redactions = _main.Redactions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        };
        var instances = _main.InstancesForScope(_scope.SelectedIndex);
        _export.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _result.Text = "Exporting…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<DeidProgress>(p =>
        {
            _progress.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            _result.Text = $"Exporting {p.Done:N0} of {p.Total:N0}…";
        });
        try
        {
            var report = await Task.Run(() => DeidentificationExporter.ExportAsync(instances, _main.SourceRoots, _folder.Text, options, progress, _cts.Token));
            _result.Text = string.Create(CultureInfo.InvariantCulture,
                $"Exported {report.Exported:N0} of {report.SourceObjects:N0} objects. Excluded {report.ExcludedNonImage} non-image objects and {report.ExcludedBurnedIn} image(s) with possible burned-in text; {report.FlaggedBurnedIn} exported with possible text; {report.Redacted} redacted; {report.Failed} failed. {report.UidsRemapped:N0} UIDs remapped. See deidentification-report.txt.");
            _open.Visibility = Visibility.Visible;
            _main.ShowStatus($"De-identified copy written to {_folder.Text}");
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Export cancelled. Delete the partial output folder before reusing it.";
        }
        catch (Exception ex)
        {
            _result.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            _export.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }
}

/// <summary>Read-only dump of every attribute in one file, including nested sequences.</summary>
public sealed class TagsWindow : Window
{
    private readonly List<(string Text, bool Private)> _lines = [];
    private readonly TextBox _search = new() { Padding = new Thickness(8, 5, 8, 5) };
    private readonly TextBox _view = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
        AcceptsReturn = true,
    };

    public TagsWindow(string path, bool privacy)
    {
        Title = "DICOM attributes";
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Filter attributes");
        var root = new DockPanel { Margin = new Thickness(12) };
        var info = new TextBlock
        {
            Text = privacy ? "Patient details are hidden (privacy mode). Group 0010 and person names are masked." : "Filter by tag, keyword or value.",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
        };
        DockPanel.SetDock(info, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(info);
        root.Children.Add(_search);
        _view.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_view);
        Content = root;
        try
        {
            var file = FellowOakDicom.DicomFile.Open(path, FellowOakDicom.FileReadOption.SkipLargeTags, 4096);
            Dump(file.FileMetaInfo, 0, privacy);
            Dump(file.Dataset, 0, privacy);
        }
        catch (Exception ex)
        {
            _lines.Add(("Could not read: " + ex.Message, false));
        }
        _search.TextChanged += (_, _) => Fill();
        Fill();
    }

    private void Dump(FellowOakDicom.DicomDataset ds, int depth, bool privacy)
    {
        var indent = new string(' ', depth * 2);
        foreach (var item in ds)
        {
            var name = item.Tag.DictionaryEntry?.Name ?? "Unknown";
            if (item is FellowOakDicom.DicomSequence seq)
            {
                _lines.Add(($"{indent}{item.Tag} {item.ValueRepresentation.Code} {name} ({seq.Items.Count} item(s))", item.Tag.IsPrivate));
                var n = 0;
                foreach (var child in seq.Items)
                {
                    _lines.Add(($"{indent}  > item {++n}", false));
                    Dump(child, depth + 2, privacy);
                }
                continue;
            }
            string value;
            try
            {
                value = item is FellowOakDicom.DicomElement e && e.Buffer.Size > 1024 ? $"<{e.Buffer.Size:N0} bytes>" : ds.GetString(item.Tag);
            }
            catch (Exception)
            {
                value = "<binary>";
            }
            if (privacy && (item.Tag.Group == 0x0010 || item.ValueRepresentation == FellowOakDicom.DicomVR.PN)) value = "•••";
            if (value.Length > 200) value = value[..200] + "…";
            _lines.Add(($"{indent}{item.Tag} {item.ValueRepresentation.Code} {name} = {value}", item.Tag.IsPrivate));
        }
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        _view.Text = string.Join("\n", _lines.Where(l => q.Length == 0 || l.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(l => l.Text));
    }
}
