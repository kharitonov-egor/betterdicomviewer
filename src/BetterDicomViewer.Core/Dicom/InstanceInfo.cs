namespace BetterDicomViewer.Core.Dicom;

/// <summary>Header metadata for one DICOM file, read without pixel data. Persisted in the local index cache.</summary>
public sealed class InstanceInfo
{
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public DateTime LastWriteUtc { get; init; }

    /// <summary>True for entries built from DICOMDIR records whose file header has not been parsed yet.</summary>
    public bool IsProvisional { get; init; }

    public string TransferSyntaxUid { get; init; } = "";
    public string SopClassUid { get; init; } = "";
    public string SopInstanceUid { get; init; } = "";
    public string StudyInstanceUid { get; init; } = "";
    public string SeriesInstanceUid { get; init; } = "";
    public string? FrameOfReferenceUid { get; init; }

    public string? PatientName { get; init; }
    public string? PatientId { get; init; }
    public string? PatientBirthDate { get; init; }
    public string? PatientSex { get; init; }
    public string? PatientAge { get; init; }
    public double? PatientWeightKg { get; init; }
    public double? PatientSizeM { get; init; }

    public string? StudyDate { get; init; }
    public string? StudyTime { get; init; }
    public string? StudyDescription { get; init; }
    public string? AccessionNumber { get; init; }
    public string? StudyId { get; init; }

    public string? Modality { get; init; }
    public int? SeriesNumber { get; init; }
    public string? SeriesDescription { get; init; }
    public string? SeriesDate { get; init; }
    public string? SeriesTime { get; init; }
    public string? BodyPartExamined { get; init; }
    public string? Manufacturer { get; init; }
    public string? ManufacturerModelName { get; init; }

    public int? InstanceNumber { get; init; }
    public int? AcquisitionNumber { get; init; }
    public string? AcquisitionDate { get; init; }
    public string? AcquisitionTime { get; init; }
    public string? AcquisitionDateTime { get; init; }
    public string? ContentTime { get; init; }
    public string[] ImageType { get; init; } = [];

    public int Rows { get; init; }
    public int Columns { get; init; }
    public int NumberOfFrames { get; init; } = 1;
    public int BitsAllocated { get; init; }
    public int BitsStored { get; init; }
    public int HighBit { get; init; }
    public int PixelRepresentation { get; init; }
    public int SamplesPerPixel { get; init; } = 1;
    public string? PhotometricInterpretation { get; init; }
    public int PlanarConfiguration { get; init; }
    public bool HasPixelData { get; init; }

    public double[]? PixelSpacing { get; init; }
    public double[]? ImagerPixelSpacing { get; init; }
    public double? EstimatedRadiographicMagnificationFactor { get; init; }
    public string? PixelSpacingCalibrationType { get; init; }
    public double[]? ImagePositionPatient { get; init; }
    public double[]? ImageOrientationPatient { get; init; }
    public double? SliceThickness { get; init; }
    public double? SpacingBetweenSlices { get; init; }
    public double? SliceLocation { get; init; }
    public string? PatientOrientation { get; init; }

    public double RescaleSlope { get; init; } = 1;
    public double RescaleIntercept { get; init; }
    public string? RescaleType { get; init; }
    public bool HasModalityLutSequence { get; init; }
    public double[]? WindowCenter { get; init; }
    public double[]? WindowWidth { get; init; }
    public string[]? WindowExplanation { get; init; }
    public string? VoiLutFunction { get; init; }
    public string? PresentationLutShape { get; init; }

    public string? BurnedInAnnotation { get; init; }
    public string? LossyImageCompression { get; init; }
    public double? FrameTimeMs { get; init; }
    public double? RecommendedDisplayFrameRate { get; init; }
    public double? CineRate { get; init; }

    public PetInfo? Pet { get; init; }
    public UltrasoundRegion[]? UltrasoundRegions { get; init; }
    public FrameGeometry[]? PerFrame { get; init; }

    public bool IsLocalizer => ImageType.Any(t => string.Equals(t, "LOCALIZER", StringComparison.OrdinalIgnoreCase));
    public bool IsColor => SamplesPerPixel >= 3;
    public bool IsMultiFrame => NumberOfFrames > 1;
}

/// <summary>Per-frame geometry for enhanced multi-frame objects.</summary>
public sealed class FrameGeometry
{
    public double[]? ImagePositionPatient { get; init; }
    public double[]? ImageOrientationPatient { get; init; }
    public double[]? PixelSpacing { get; init; }
    public double? SliceThickness { get; init; }
    public double? RescaleSlope { get; init; }
    public double? RescaleIntercept { get; init; }
    public double? WindowCenter { get; init; }
    public double? WindowWidth { get; init; }
    public double? FrameReferenceTimeMs { get; init; }
    public string? FrameAcquisitionDateTime { get; init; }
}

public sealed class PetInfo
{
    public string? Units { get; init; }
    public string? DecayCorrection { get; init; }
    public string[] CorrectedImage { get; init; } = [];
    public double? DecayFactor { get; init; }
    public double? FrameReferenceTimeMs { get; init; }
    public double? ActualFrameDurationMs { get; init; }
    public string? SeriesType { get; init; }
    public string? Radiopharmaceutical { get; init; }
    public string? Radionuclide { get; init; }
    public double? RadionuclideTotalDoseBq { get; init; }
    public double? RadionuclideHalfLifeS { get; init; }
    public string? RadiopharmaceuticalStartTime { get; init; }
    public string? RadiopharmaceuticalStartDateTime { get; init; }
    public double? PhilipsSuvScaleFactor { get; init; }
    public double? PhilipsActivityConcentrationScaleFactor { get; init; }
}

/// <summary>Sequence of Ultrasound Regions item (0018,6011) used for calibrated ultrasound measurements.</summary>
public sealed class UltrasoundRegion
{
    public int MinX0 { get; init; }
    public int MinY0 { get; init; }
    public int MaxX1 { get; init; }
    public int MaxY1 { get; init; }
    public int SpatialFormat { get; init; }
    public int DataType { get; init; }
    public int PhysicalUnitsX { get; init; }
    public int PhysicalUnitsY { get; init; }
    public double PhysicalDeltaX { get; init; }
    public double PhysicalDeltaY { get; init; }

    public bool Contains(double x, double y) => x >= MinX0 && x <= MaxX1 + 1 && y >= MinY0 && y <= MaxY1 + 1;

    /// <summary>Physical units code 3 is centimetres (PS3.3 C.8.5.5.1.15).</summary>
    public bool IsSpatialCentimetres => PhysicalUnitsX == 3 && PhysicalUnitsY == 3 && PhysicalDeltaX > 0 && PhysicalDeltaY > 0;
}
