using Microsoft.Win32;

namespace BetterDicomViewer.App.Services;

/// <summary>
/// Per-app "Graphics performance preference" (Settings › System › Display › Graphics). On hybrid laptops the OpenGL
/// driver honours it, which moves 3D rendering from the integrated GPU to the discrete one.
/// </summary>
public static class GpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>Current value for this executable: null (not set), or the raw "GpuPreference=n;" string.</summary>
    public static string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ExePath) as string;
    }

    public static bool IsHighPerformance => Current()?.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Requests the high-performance GPU for this executable unless the user already chose a preference.</summary>
    public static void EnsureDefault()
    {
        try
        {
            if (string.IsNullOrEmpty(ExePath) || Current() is not null) return;
            Set(highPerformance: true);
        }
        catch (Exception)
        {
        }
    }

    public static void Set(bool highPerformance)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ExePath, highPerformance ? "GpuPreference=2;" : "GpuPreference=1;", RegistryValueKind.String);
    }
}
