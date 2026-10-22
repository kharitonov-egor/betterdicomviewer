using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Pet;

namespace BetterDicomViewer.Tests;

public class SuvTests
{
    private static readonly DateTime Injection = new(2024, 3, 5, 9, 0, 0);

    private static InstanceInfo Pet(
        string? units = "BQML",
        string? decay = "START",
        string[]? corrected = null,
        double? weight = 75,
        double? dose = 370e6,
        double? halfLife = 6586.2,
        TimeSpan? series = null,
        TimeSpan? acquisition = null,
        bool startDateTime = true,
        double? decayFactor = null,
        double? frameDurationMs = null,
        double? height = null,
        string sex = "F")
    {
        var s = Injection + (series ?? TimeSpan.FromMinutes(60));
        var a = Injection + (acquisition ?? series ?? TimeSpan.FromMinutes(60));
        return new InstanceInfo
        {
            FilePath = "x",
            Modality = "PT",
            PatientWeightKg = weight,
            PatientSizeM = height,
            PatientSex = sex,
            SeriesDate = s.ToString("yyyyMMdd"),
            SeriesTime = s.ToString("HHmmss"),
            AcquisitionDate = a.ToString("yyyyMMdd"),
            AcquisitionTime = a.ToString("HHmmss.ffffff"),
            Pet = new PetInfo
            {
                Units = units,
                DecayCorrection = decay,
                CorrectedImage = corrected ?? ["ATTN", "DECY", "SCAT"],
                RadionuclideTotalDoseBq = dose,
                RadionuclideHalfLifeS = halfLife,
                Radionuclide = "^18^Fluorine",
                RadiopharmaceuticalStartTime = Injection.ToString("HHmmss"),
                RadiopharmaceuticalStartDateTime = startDateTime ? Injection.ToString("yyyyMMddHHmmss") : null,
                DecayFactor = decayFactor,
                ActualFrameDurationMs = frameDurationMs,
            },
        };
    }

    [Fact]
    public void StartDecayCorrectionUsesSeriesTime()
    {
        var info = SuvCalculator.Compute([Pet()]);
        Assert.True(info.IsAvailable, string.Join(" ", info.Problems));
        var decayed = 370e6 * Math.Pow(2, -3600 / 6586.2);
        Assert.Equal(75 * 1000 / decayed, info.BodyWeightFactor!.Value, 12);
        Assert.Equal(3600, info.DecayTimeS!.Value, 6);
        Assert.Equal("series time", info.ReferenceTimeSource);
        // 5000 Bq/mL at 60 min after 370 MBq in a 75 kg patient is SUVbw 1.48038.
        Assert.Equal(1.48038, 5000 * info.BodyWeightFactor.Value, 5);
    }

    [Fact]
    public void DerivedSeriesWithLaterSeriesTimeFallsBackToAcquisitionTime()
    {
        var info = SuvCalculator.Compute([Pet(series: TimeSpan.FromMinutes(95), acquisition: TimeSpan.FromMinutes(60))]);
        Assert.True(info.IsAvailable);
        Assert.Equal(3600, info.DecayTimeS!.Value, 6);
        Assert.Equal("earliest acquisition time", info.ReferenceTimeSource);
    }

    [Fact]
    public void DecayFactorPinsReferenceTime()
    {
        // Scanner corrected to 60 min; slice acquired at 62 min with a 120 s frame.
        var lambda = Math.Log(2) / 6586.2;
        const double dt = 120;
        var df = lambda * dt / (1 - Math.Exp(-lambda * dt)) * Math.Exp(lambda * 120);
        var info = SuvCalculator.Compute([Pet(series: TimeSpan.FromMinutes(60), acquisition: TimeSpan.FromMinutes(62), decayFactor: df, frameDurationMs: dt * 1000)]);
        Assert.True(info.IsAvailable);
        Assert.Equal("series time", info.ReferenceTimeSource);
        Assert.Contains(info.Notes, n => n.Contains("Decay Factor"));
    }

    [Fact]
    public void AdminDecayCorrectionUsesUndecayedDose()
    {
        var info = SuvCalculator.Compute([Pet(decay: "ADMIN")]);
        Assert.True(info.IsAvailable);
        Assert.Equal(75 * 1000 / 370e6, info.BodyWeightFactor!.Value, 15);
    }

    [Fact]
    public void NoDecayCorrectionUsesPerSliceAcquisitionTimes()
    {
        var slices = new[] { Pet(decay: "NONE", corrected: ["ATTN"], acquisition: TimeSpan.FromMinutes(60)), Pet(decay: "NONE", corrected: ["ATTN"], acquisition: TimeSpan.FromMinutes(70)) };
        var info = SuvCalculator.Compute(slices);
        Assert.True(info.IsAvailable, string.Join(" ", info.Problems));
        Assert.Null(info.BodyWeightFactor);
        Assert.Equal(75 * 1000 / (370e6 * Math.Pow(2, -3600 / 6586.2)), info.Factor(0)!.Value, 12);
        Assert.Equal(75 * 1000 / (370e6 * Math.Pow(2, -4200 / 6586.2)), info.Factor(1)!.Value, 12);
    }

    [Theory]
    [InlineData(null, "weight is missing")]
    [InlineData(0.0, "weight is missing")]
    [InlineData(900.0, "implausible")]
    public void MissingOrImplausibleWeightBlocksSuv(double? weight, string message)
    {
        var info = SuvCalculator.Compute([Pet(weight: weight)]);
        Assert.False(info.IsAvailable);
        Assert.Contains(info.Problems, p => p.Contains(message));
        Assert.Null(info.Factor(0));
    }

    [Fact]
    public void MissingDoseHalfLifeAndTimesAreReported()
    {
        var info = SuvCalculator.Compute([Pet(dose: null, halfLife: null)]);
        Assert.False(info.IsAvailable);
        Assert.Contains(info.Problems, p => p.Contains("dose"));
        Assert.Contains(info.Problems, p => p.Contains("half-life"));
    }

    [Fact]
    public void DoseRecordedInMegabecquerelIsRejected()
    {
        var info = SuvCalculator.Compute([Pet(dose: 370)]);
        Assert.False(info.IsAvailable);
        Assert.Contains(info.Problems, p => p.Contains("MBq"));
    }

    [Theory]
    [InlineData("PROPCPS")]
    [InlineData("CNTS")]
    [InlineData(null)]
    public void UnconvertibleUnitsAreRejected(string? units)
    {
        var info = SuvCalculator.Compute([Pet(units: units)]);
        Assert.False(info.IsAvailable);
    }

    [Fact]
    public void NonAttenuationCorrectedImagesAreRejected()
    {
        var info = SuvCalculator.Compute([Pet(corrected: ["DECY"])]);
        Assert.False(info.IsAvailable);
        Assert.Contains(info.Problems, p => p.Contains("attenuation"));
    }

    [Fact]
    public void InjectionAfterScanIsRejected()
    {
        var info = SuvCalculator.Compute([Pet(series: TimeSpan.FromMinutes(-5))]);
        Assert.False(info.IsAvailable);
    }

    [Fact]
    public void GmlUnitsAreAlreadySuv()
    {
        var info = SuvCalculator.Compute([Pet(units: "GML", weight: null, dose: null)]);
        Assert.True(info.IsAvailable);
        Assert.Equal(1.0, info.BodyWeightFactor);
    }

    [Fact]
    public void LeanBodyMassNeedsHeightAndSex()
    {
        Assert.Null(SuvCalculator.Compute([Pet()]).LeanBodyMassRatio);
        var info = SuvCalculator.Compute([Pet(height: 1.70, sex: "F")]);
        var bmi = 75 / (1.7 * 1.7);
        Assert.Equal(9270 * 75 / (8780 + 244 * bmi) / 75, info.LeanBodyMassRatio!.Value, 9);
    }
}
