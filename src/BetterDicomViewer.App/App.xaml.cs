using System.Windows;
using System.Windows.Threading;
using BetterDicomViewer.App.Services;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Imaging.Native;

namespace BetterDicomViewer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        if (GpuPreference.EnsureDefault() && Environment.GetEnvironmentVariable("BDV_RESTARTED") is null)
        {
            // The graphics preference only applies to new processes, so restart once before any window appears.
            var psi = new System.Diagnostics.ProcessStartInfo(GpuPreference.ExePath) { UseShellExecute = false };
            foreach (var a in e.Args) psi.ArgumentList.Add(a);
            psi.Environment["BDV_RESTARTED"] = "1";
            System.Diagnostics.Process? child = null;
            try
            {
                child = System.Diagnostics.Process.Start(psi);
            }
            catch (Exception)
            {
            }
            if (child is not null)
            {
                await child.WaitForExitAsync();
                Shutdown(child.ExitCode);
                return;
            }
        }
        DicomRuntime.EnsureInitialized();

        var window = new MainWindow();
        MainWindow = window;
        var script = ScriptRunner.Parse(e.Args, out var openPath);
        if (!script.Any(c => c.StartsWith("size ", StringComparison.OrdinalIgnoreCase))) window.WindowState = WindowState.Maximized;
        window.Show();

        if (!NativeImaging.IsAvailable)
        {
            Dialogs.MessageDialog.Show(window, "Imaging component missing",
                "bdv_vtk.dll could not be loaded, so images cannot be displayed. Reinstall the application or rebuild the native bridge with scripts\\build-native.ps1.",
                NativeImaging.LoadError);
        }

        if (openPath is not null) await window.OpenPathAsync(openPath);
        if (script.Count > 0) await new ScriptRunner(window).RunAsync(script);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Details hold the exception type and stack trace only; nothing is written to disk.
        e.Handled = true;
        Dialogs.MessageDialog.Show(MainWindow, "Something went wrong", e.Exception.Message, e.Exception.ToString());
    }
}
