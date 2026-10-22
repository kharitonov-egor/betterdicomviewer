using System.Globalization;
using BetterDicomViewer.Core.Dicom;

namespace BetterDicomViewer.Core.Pet;

/// <summary>
/// Result of SUV validation for one PET image set. When <see cref="IsAvailable"/> is false the viewer shows activity
/// concentration only and lists <see cref="Problems"/>; it never substitutes default doses, weights or times.
/// </summary>
public sealed class SuvInfo
{
    public bool IsAvailable => Problems.Count == 0 && (BodyWeightFactor is not null || PerSliceFactors is not null);
    public List<string> Problems { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>SUVbw = stored modality value * factor (g/mL). Null when per-slice factors apply.</summary>
    public double? BodyWeightFactor { get; set; }

    /// <summary>Per-slice SUVbw factors (decay correction NONE), indexed like the source slices.</summary>
    public double[]? PerSliceFactors { get; set; }

    /// <summary>SUL (Janmahasatian lean body mass) factor relative to SUVbw, when height and sex are known.</summary>
    public double? LeanBodyMassRatio { get; set; }

    public string Units { get; set; } = "";
    public string DecayCorrection { get; set; } = "";
    public double? WeightKg { get; set; }
    public double? HeightM { get; set; }
    public double? InjectedDoseBq { get; set; }
    public double? HalfLifeS { get; set; }
    public DateTime? InjectionTime { get; set; }
    public DateTime? ReferenceTime { get; set; }
    public string ReferenceTimeSource { get; set; } = "";
    public double? DecayTimeS { get; set; }
    public double? DecayedDoseBq { get; set; }
    public string Radionuclide { get; set; } = "";

    /// <summary>Unit of the stored values when SUV is not shown.</summary>
    public string ActivityUnit { get; set; } = "Bq/mL";

    public double? Factor(int slice)
    {
        if (!IsAvailable) return null;
        if (PerSliceFactors is { } f) return (uint)slice < (uint)f.Length ? f[slice] : null;
        return BodyWeightFactor;
    }

    public string Summary()
    {
        if (!IsAvailable) return "SUV unavailable: " + string.Join(" ", Problems);
        var ci = CultureInfo.InvariantCulture;
        return string.Create(ci,
            $"SUVbw from {Radionuclide} dose {InjectedDoseBq / 1e6:0.##} MBq, weight {WeightKg:0.#} kg, decay {DecayTimeS / 60:0.#} min ({ReferenceTimeSource}), decay correction {DecayCorrection}.");
    }
}

/// <summary>Implements the QIBA vendor-neutral SUV pseudo-code with stricter validation of each input.</summary>
public static class SuvCalculator
{
    private static readonly Dictionary<string, double> KnownHalfLives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["^18^Fluorine"] = 6586.2,
        ["Fluorine-18"] = 6586.2,
        ["18F"] = 6586.2,
        ["^11^Carbon"] = 1223.4,
        ["^68^Gallium"] = 4057.7,
        ["^13^Nitrogen"] = 597.9,
        ["^82^Rubidium"] = 75.45,
        ["^64^Copper"] = 45721.1,
        ["^89^Zirconium"] = 282276,
    };

    public static SuvInfo Compute(IReadOnlyList<InstanceInfo> slices)
    {
        var info = new SuvInfo();
        if (slices.Count == 0)
        {
            info.Problems.Add("No PET images.");
            return info;
        }

        var first = slices[0];
        var pet = first.Pet;
        if (pet is null)
        {
            info.Problems.Add("PET module attributes are missing.");
            return info;
        }

        info.Units = pet.Units ?? "";
        info.DecayCorrection = pet.DecayCorrection ?? "";
        info.Radionuclide = pet.Radionuclide ?? pet.Radiopharmaceutical ?? "unknown radionuclide";
        info.WeightKg = first.PatientWeightKg;
        info.HeightM = first.PatientSizeM;
        info.InjectedDoseBq = pet.RadionuclideTotalDoseBq;
        info.HalfLifeS = pet.RadionuclideHalfLifeS;

        var units = info.Units.ToUpperInvariant();
        info.ActivityUnit = units switch
        {
            "BQML" => "Bq/mL",
            "GML" => "g/mL",
            "CNTS" => "counts",
            "" => "unitless",
            _ => info.Units,
        };

        if (units == "GML")
        {
            info.BodyWeightFactor = 1.0;
            info.Notes.Add("Values are already stored as SUVbw (Units = GML).");
            return info;
        }

        var corrected = new HashSet<string>(pet.CorrectedImage, StringComparer.OrdinalIgnoreCase);
        if (!corrected.Contains("ATTN"))
            info.Problems.Add("Images are not attenuation corrected (Corrected Image lacks ATTN).");

        if (units == "CNTS")
        {
            if (pet.PhilipsSuvScaleFactor is > 0 and var philips)
            {
                if (info.Problems.Count > 0) return info;
                info.BodyWeightFactor = philips;
                info.Notes.Add("Using the Philips private SUV scale factor (7053,1000) for Units = CNTS.");
                return info;
            }
            info.Problems.Add("Units are CNTS and no vendor activity scale factor is present.");
            return info;
        }

        if (units != "BQML")
        {
            info.Problems.Add(string.IsNullOrEmpty(info.Units)
                ? "Units (0054,1001) are missing."
                : $"Units '{info.Units}' cannot be converted to activity concentration.");
            return info;
        }

        if (info.WeightKg is not { } weight || weight <= 0)
            info.Problems.Add("Patient weight is missing.");
        else if (weight is < 1 or > 400)
            info.Problems.Add(string.Create(CultureInfo.InvariantCulture, $"Patient weight {weight:0.#} kg is implausible."));

        if (info.InjectedDoseBq is not { } dose || dose <= 0)
            info.Problems.Add("Injected dose (Radionuclide Total Dose) is missing.");
        else if (dose < 1e5)
            info.Problems.Add(string.Create(CultureInfo.InvariantCulture, $"Injected dose {dose:0.###} Bq is implausibly small (possibly recorded in MBq)."));

        if (info.HalfLifeS is not { } halfLife || halfLife <= 0)
        {
            info.Problems.Add("Radionuclide half-life is missing.");
        }
        else if (KnownHalfLives.TryGetValue(info.Radionuclide, out var expected) && Math.Abs(halfLife - expected) / expected > 0.02)
        {
            info.Notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Recorded half-life {halfLife:0.#} s differs from the reference {expected:0.#} s for {info.Radionuclide}; the recorded value is used."));
        }

        var decay = info.DecayCorrection.ToUpperInvariant();
        if (decay is not ("START" or "ADMIN" or "NONE"))
            info.Problems.Add(string.IsNullOrEmpty(decay) ? "Decay Correction (0054,1102) is missing." : $"Decay correction '{info.DecayCorrection}' is not supported.");
        else if (decay != "NONE" && !corrected.Contains("DECY"))
            info.Problems.Add("Decay Correction says the images are decay corrected, but Corrected Image lacks DECY.");

        info.InjectionTime = InjectionTime(first);
        if (info.InjectionTime is null)
            info.Problems.Add("Injection time (Radiopharmaceutical Start Date Time or Start Time) is missing.");

        if (info.Problems.Count > 0) return info;

        var injected = info.InjectedDoseBq!.Value;
        var t12 = info.HalfLifeS!.Value;

        if (decay == "ADMIN")
        {
            info.ReferenceTime = info.InjectionTime;
            info.ReferenceTimeSource = "administration time";
            info.DecayTimeS = 0;
            info.DecayedDoseBq = injected;
            info.BodyWeightFactor = info.WeightKg!.Value * 1000.0 / injected;
            AddLeanBodyMass(info, first);
            return info;
        }

        if (decay == "START")
        {
            var series = DicomDateTime.Combine(first.SeriesDate, first.SeriesTime);
            var acquisitions = slices
                .Select(s => DicomDateTime.ParseDateTime(s.AcquisitionDateTime) ?? DicomDateTime.Combine(s.AcquisitionDate ?? s.SeriesDate, s.AcquisitionTime))
                .Where(t => t is not null)
                .Select(t => t!.Value)
                .ToList();
            DateTime? earliestAcq = acquisitions.Count > 0 ? acquisitions.Min() : null;
            var implied = ImpliedReferenceFromDecayFactor(slices, t12);

            DateTime reference;
            if (implied is { } imp)
            {
                // The scanner's own Decay Factor pins down the reference time it used; prefer the matching DICOM time.
                var candidates = new List<(DateTime Time, string Source)>();
                if (series is { } st) candidates.Add((st, "series time"));
                if (earliestAcq is { } ea) candidates.Add((ea, "earliest acquisition time"));
                var best = candidates.OrderBy(c => Math.Abs((c.Time - imp).TotalSeconds)).FirstOrDefault();
                if (best.Source is not null && Math.Abs((best.Time - imp).TotalSeconds) <= 120)
                {
                    reference = best.Time;
                    info.ReferenceTimeSource = best.Source;
                    info.Notes.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Decay reference time agrees with the per-slice Decay Factor to within {Math.Abs((best.Time - imp).TotalSeconds):0.#} s."));
                }
                else
                {
                    reference = imp;
                    info.ReferenceTimeSource = "reference time implied by Decay Factor";
                    info.Notes.Add("Series and acquisition times disagree with the Decay Factor; the reference time implied by Decay Factor is used.");
                }
            }
            else if (series is { } st && (earliestAcq is null || st <= earliestAcq.Value.AddSeconds(2)))
            {
                reference = st;
                info.ReferenceTimeSource = "series time";
            }
            else if (earliestAcq is { } ea)
            {
                reference = ea;
                info.ReferenceTimeSource = "earliest acquisition time";
                info.Notes.Add("Series time is later than the earliest acquisition time (common for derived series); the acquisition time is used as in the QIBA reference method.");
            }
            else
            {
                info.Problems.Add("Series time and acquisition times are missing, so the decay reference time is unknown.");
                return info;
            }

            var decaySeconds = (reference - info.InjectionTime!.Value).TotalSeconds;
            if (!ValidateDecayTime(info, decaySeconds)) return info;
            info.ReferenceTime = reference;
            info.DecayTimeS = decaySeconds;
            info.DecayedDoseBq = injected * Math.Pow(2, -decaySeconds / t12);
            info.BodyWeightFactor = info.WeightKg!.Value * 1000.0 / info.DecayedDoseBq;
            AddLeanBodyMass(info, first);
            return info;
        }

        // NONE: values are activity at each slice's acquisition time; correct each slice back to injection.
        var factors = new double[slices.Count];
        for (var i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            var acq = DicomDateTime.ParseDateTime(s.AcquisitionDateTime) ?? DicomDateTime.Combine(s.AcquisitionDate ?? s.SeriesDate, s.AcquisitionTime);
            if (acq is null)
            {
                info.Problems.Add("Decay correction is NONE and a slice lacks Acquisition Time, so per-slice decay cannot be applied.");
                return info;
            }
            var dt = (acq.Value - info.InjectionTime!.Value).TotalSeconds;
            if (!ValidateDecayTime(info, dt)) return info;
            factors[i] = info.WeightKg!.Value * 1000.0 / (injected * Math.Pow(2, -dt / t12));
        }
        info.PerSliceFactors = factors;
        info.ReferenceTimeSource = "each slice's acquisition time";
        info.Notes.Add("Images are not decay corrected; SUV uses per-slice decay from injection to acquisition start.");
        AddLeanBodyMass(info, first);
        return info;
    }

    /// <summary>
    /// Recovers the decay-correction reference time from Decay Factor (0054,1321), Acquisition Time and Actual Frame Duration:
    /// DF = (lambda*dt / (1 - exp(-lambda*dt))) * exp(lambda * (t_acq - t_ref)). Returns null when inputs are missing or inconsistent.
    /// </summary>
    internal static DateTime? ImpliedReferenceFromDecayFactor(IReadOnlyList<InstanceInfo> slices, double halfLifeS)
    {
        var lambda = Math.Log(2) / halfLifeS;
        var withFrame = new List<double>();
        var withoutFrame = new List<double>();
        DateTime? origin = null;
        foreach (var s in slices)
        {
            if (s.Pet?.DecayFactor is not > 0 || s.Pet.ActualFrameDurationMs is not > 0) continue;
            var acq = DicomDateTime.ParseDateTime(s.AcquisitionDateTime) ?? DicomDateTime.Combine(s.AcquisitionDate ?? s.SeriesDate, s.AcquisitionTime);
            if (acq is null) continue;
            origin ??= acq;
            var ta = (acq.Value - origin.Value).TotalSeconds;
            var dt = s.Pet.ActualFrameDurationMs.Value / 1000.0;
            var frameTerm = lambda * dt / (1 - Math.Exp(-lambda * dt));
            withFrame.Add(ta - Math.Log(s.Pet.DecayFactor.Value / frameTerm) / lambda);
            withoutFrame.Add(ta - Math.Log(s.Pet.DecayFactor.Value) / lambda);
        }
        if (origin is null || withFrame.Count < 1) return null;
        foreach (var set in new[] { withFrame, withoutFrame })
        {
            var spread = set.Max() - set.Min();
            if (spread <= 30)
            {
                var sorted = set.OrderBy(v => v).ToList();
                return origin.Value.AddSeconds(sorted[sorted.Count / 2]);
            }
        }
        return null;
    }

    private static bool ValidateDecayTime(SuvInfo info, double seconds)
    {
        if (seconds < 0)
        {
            info.Problems.Add("Injection time is after the scan reference time (check dates; times crossing midnight need Radiopharmaceutical Start Date Time).");
            return false;
        }
        if (seconds > 24 * 3600)
        {
            info.Problems.Add("More than 24 hours between injection and scan; timing metadata looks inconsistent.");
            return false;
        }
        return true;
    }

    private static DateTime? InjectionTime(InstanceInfo first)
    {
        var pet = first.Pet!;
        if (DicomDateTime.ParseDateTime(pet.RadiopharmaceuticalStartDateTime) is { } dt) return dt;
        var date = first.SeriesDate ?? first.StudyDate ?? first.AcquisitionDate;
        return DicomDateTime.Combine(date, pet.RadiopharmaceuticalStartTime);
    }

    private static void AddLeanBodyMass(SuvInfo info, InstanceInfo first)
    {
        if (info.WeightKg is not { } w || info.HeightM is not { } h || h is < 0.5 or > 2.6) return;
        var sex = first.PatientSex?.Trim().ToUpperInvariant();
        if (sex is not ("M" or "F")) return;
        var bmi = w / (h * h);
        var lbm = sex == "M" ? 9270 * w / (6680 + 216 * bmi) : 9270 * w / (8780 + 244 * bmi);
        info.LeanBodyMassRatio = lbm / w;
    }
}
