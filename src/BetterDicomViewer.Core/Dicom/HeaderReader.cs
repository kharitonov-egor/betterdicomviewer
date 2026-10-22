using System.Text;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Dicom;

public enum ProbeResult
{
    NotDicom,
    Part10,
    RawDataset,
}

public static class HeaderReader
{
    public const string DicomDirSopClassUid = "1.2.840.10008.1.3.10";

    private static readonly DicomTag RadionuclideHalfLife = new(0x0018, 0x1075);

    /// <summary>Cheap content sniffing. File extensions are ignored because DICOM media files are often extensionless.</summary>
    public static ProbeResult Probe(string path)
    {
        Span<byte> buffer = stackalloc byte[132];
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception)
        {
            return ProbeResult.NotDicom;
        }

        if (read >= 132 && buffer[128] == 'D' && buffer[129] == 'I' && buffer[130] == 'C' && buffer[131] == 'M')
            return ProbeResult.Part10;

        // Raw datasets without a preamble start with a little-endian group 0x0008 (or 0x0002) tag.
        if (read >= 8 && (buffer[0] == 0x08 || buffer[0] == 0x02) && buffer[1] == 0x00 && buffer[3] == 0x00)
            return ProbeResult.RawDataset;

        return ProbeResult.NotDicom;
    }

    public static DicomFile OpenHeader(string path, out bool hasPixelData)
    {
        DicomRuntime.EnsureInitialized();
        var sawPixels = false;
        var file = DicomFile.Open(path, Encoding.UTF8, ps =>
        {
            if (ps.SequenceDepth == 0 && ps.Tag.Group == 0x7FE0)
            {
                sawPixels = true;
                return true;
            }
            return false;
        }, FileReadOption.ReadAll, 0);
        hasPixelData = sawPixels;
        return file;
    }

    public static InstanceInfo? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static InstanceInfo Read(string path)
    {
        var fi = new FileInfo(path);
        var file = OpenHeader(path, out var hasPixels);
        return FromDataset(file.Dataset, file.FileMetaInfo, path, fi.Length, fi.LastWriteTimeUtc, hasPixels);
    }

    public static InstanceInfo FromDataset(DicomDataset ds, DicomFileMetaInformation? meta, string path, long size, DateTime lastWriteUtc, bool hasPixels)
    {
        var tsUid = "";
        try
        {
            tsUid = meta?.TransferSyntax?.UID?.UID ?? ds.InternalTransferSyntax?.UID?.UID ?? "";
        }
        catch (Exception)
        {
        }

        var sopClass = ds.Str(DicomTag.SOPClassUID) ?? meta?.MediaStorageSOPClassUID?.UID ?? "";
        var modality = ds.Str(DicomTag.Modality);

        return new InstanceInfo
        {
            FilePath = path,
            FileSize = size,
            LastWriteUtc = lastWriteUtc,
            TransferSyntaxUid = tsUid,
            SopClassUid = sopClass,
            SopInstanceUid = ds.Str(DicomTag.SOPInstanceUID) ?? meta?.MediaStorageSOPInstanceUID?.UID ?? "",
            StudyInstanceUid = ds.Str(DicomTag.StudyInstanceUID) ?? "",
            SeriesInstanceUid = ds.Str(DicomTag.SeriesInstanceUID) ?? "",
            FrameOfReferenceUid = ds.Str(DicomTag.FrameOfReferenceUID),

            PatientName = ds.Str(DicomTag.PatientName),
            PatientId = ds.Str(DicomTag.PatientID),
            PatientBirthDate = ds.Str(DicomTag.PatientBirthDate),
            PatientSex = ds.Str(DicomTag.PatientSex),
            PatientAge = ds.Str(DicomTag.PatientAge),
            PatientWeightKg = ds.Dbl(DicomTag.PatientWeight),
            PatientSizeM = ds.Dbl(DicomTag.PatientSize),

            StudyDate = ds.Str(DicomTag.StudyDate),
            StudyTime = ds.Str(DicomTag.StudyTime),
            StudyDescription = ds.Str(DicomTag.StudyDescription),
            AccessionNumber = ds.Str(DicomTag.AccessionNumber),
            StudyId = ds.Str(DicomTag.StudyID),

            Modality = modality,
            SeriesNumber = ds.Int(DicomTag.SeriesNumber),
            SeriesDescription = ds.Str(DicomTag.SeriesDescription),
            SeriesDate = ds.Str(DicomTag.SeriesDate),
            SeriesTime = ds.Str(DicomTag.SeriesTime),
            BodyPartExamined = ds.Str(DicomTag.BodyPartExamined),
            Manufacturer = ds.Str(DicomTag.Manufacturer),
            ManufacturerModelName = ds.Str(DicomTag.ManufacturerModelName),

            InstanceNumber = ds.Int(DicomTag.InstanceNumber),
            AcquisitionNumber = ds.Int(DicomTag.AcquisitionNumber),
            AcquisitionDate = ds.Str(DicomTag.AcquisitionDate),
            AcquisitionTime = ds.Str(DicomTag.AcquisitionTime),
            AcquisitionDateTime = ds.Str(DicomTag.AcquisitionDateTime),
            ContentTime = ds.Str(DicomTag.ContentTime),
            ImageType = ds.Strs(DicomTag.ImageType),

            Rows = ds.Int(DicomTag.Rows) ?? 0,
            Columns = ds.Int(DicomTag.Columns) ?? 0,
            NumberOfFrames = Math.Max(1, ds.Int(DicomTag.NumberOfFrames) ?? 1),
            BitsAllocated = ds.Int(DicomTag.BitsAllocated) ?? 0,
            BitsStored = ds.Int(DicomTag.BitsStored) ?? 0,
            HighBit = ds.Int(DicomTag.HighBit) ?? 0,
            PixelRepresentation = ds.Int(DicomTag.PixelRepresentation) ?? 0,
            SamplesPerPixel = ds.Int(DicomTag.SamplesPerPixel) ?? 1,
            PhotometricInterpretation = ds.Str(DicomTag.PhotometricInterpretation),
            PlanarConfiguration = ds.Int(DicomTag.PlanarConfiguration) ?? 0,
            HasPixelData = hasPixels,

            PixelSpacing = ValidPair(ds.Dbls(DicomTag.PixelSpacing)),
            ImagerPixelSpacing = ValidPair(ds.Dbls(DicomTag.ImagerPixelSpacing)),
            EstimatedRadiographicMagnificationFactor = ds.Dbl(DicomTag.EstimatedRadiographicMagnificationFactor),
            PixelSpacingCalibrationType = ds.Str(DicomTag.PixelSpacingCalibrationType),
            ImagePositionPatient = Exactly(ds.Dbls(DicomTag.ImagePositionPatient), 3),
            ImageOrientationPatient = Exactly(ds.Dbls(DicomTag.ImageOrientationPatient), 6),
            SliceThickness = ds.Dbl(DicomTag.SliceThickness),
            SpacingBetweenSlices = ds.Dbl(DicomTag.SpacingBetweenSlices),
            SliceLocation = ds.Dbl(DicomTag.SliceLocation),
            PatientOrientation = ds.Str(DicomTag.PatientOrientation),

            RescaleSlope = NonZero(ds.Dbl(DicomTag.RescaleSlope)) ?? 1.0,
            RescaleIntercept = ds.Dbl(DicomTag.RescaleIntercept) ?? 0.0,
            RescaleType = ds.Str(DicomTag.RescaleType),
            HasModalityLutSequence = ds.Contains(DicomTag.ModalityLUTSequence),
            WindowCenter = ds.Dbls(DicomTag.WindowCenter),
            WindowWidth = ds.Dbls(DicomTag.WindowWidth),
            WindowExplanation = ds.Contains(DicomTag.WindowCenterWidthExplanation) ? ds.Strs(DicomTag.WindowCenterWidthExplanation) : null,
            VoiLutFunction = ds.Str(DicomTag.VOILUTFunction),
            PresentationLutShape = ds.Str(DicomTag.PresentationLUTShape),

            BurnedInAnnotation = ds.Str(DicomTag.BurnedInAnnotation),
            LossyImageCompression = ds.Str(DicomTag.LossyImageCompression),
            FrameTimeMs = ds.Dbl(DicomTag.FrameTime),
            RecommendedDisplayFrameRate = ds.Dbl(DicomTag.RecommendedDisplayFrameRate),
            CineRate = ds.Dbl(DicomTag.CineRate),

            Pet = string.Equals(modality, "PT", StringComparison.OrdinalIgnoreCase) || ds.Contains(DicomTag.RadiopharmaceuticalInformationSequence)
                ? ReadPet(ds)
                : null,
            UltrasoundRegions = ReadUltrasoundRegions(ds),
            PerFrame = ReadPerFrame(ds),
        };
    }

    private static double? NonZero(double? v) => v is { } x && Math.Abs(x) > 1e-12 ? x : null;

    private static double[]? ValidPair(double[]? v) => v is { Length: >= 2 } && v[0] > 0 && v[1] > 0 ? [v[0], v[1]] : null;

    private static double[]? Exactly(double[]? v, int n) => v is not null && v.Length >= n ? v[..n] : null;

    private static PetInfo ReadPet(DicomDataset ds)
    {
        var rp = ds.FirstItem(DicomTag.RadiopharmaceuticalInformationSequence);
        string? nuclide = null;
        if (rp is not null)
        {
            var code = rp.FirstItem(DicomTag.RadionuclideCodeSequence);
            nuclide = code?.Str(DicomTag.CodeMeaning) ?? code?.Str(DicomTag.CodeValue);
        }

        return new PetInfo
        {
            Units = ds.Str(DicomTag.Units),
            DecayCorrection = ds.Str(DicomTag.DecayCorrection),
            CorrectedImage = ds.Strs(DicomTag.CorrectedImage),
            DecayFactor = ds.Dbl(DicomTag.DecayFactor),
            FrameReferenceTimeMs = ds.Dbl(DicomTag.FrameReferenceTime),
            ActualFrameDurationMs = ds.Dbl(DicomTag.ActualFrameDuration),
            SeriesType = ds.Str(DicomTag.SeriesType),
            Radiopharmaceutical = rp?.Str(DicomTag.Radiopharmaceutical),
            Radionuclide = nuclide,
            RadionuclideTotalDoseBq = rp?.Dbl(DicomTag.RadionuclideTotalDose),
            RadionuclideHalfLifeS = rp?.Dbl(RadionuclideHalfLife),
            RadiopharmaceuticalStartTime = rp?.Str(DicomTag.RadiopharmaceuticalStartTime),
            RadiopharmaceuticalStartDateTime = rp?.Str(DicomTag.RadiopharmaceuticalStartDateTime),
            PhilipsSuvScaleFactor = ds.PrivateDouble(0x7053, "Philips PET Private Group", 0x00),
            PhilipsActivityConcentrationScaleFactor = ds.PrivateDouble(0x7053, "Philips PET Private Group", 0x09),
        };
    }

    private static UltrasoundRegion[]? ReadUltrasoundRegions(DicomDataset ds)
    {
        var items = ds.Items(DicomTag.SequenceOfUltrasoundRegions);
        if (items.Count == 0) return null;
        return items.Select(r => new UltrasoundRegion
        {
            MinX0 = r.Int(DicomTag.RegionLocationMinX0) ?? 0,
            MinY0 = r.Int(DicomTag.RegionLocationMinY0) ?? 0,
            MaxX1 = r.Int(DicomTag.RegionLocationMaxX1) ?? 0,
            MaxY1 = r.Int(DicomTag.RegionLocationMaxY1) ?? 0,
            SpatialFormat = r.Int(DicomTag.RegionSpatialFormat) ?? 0,
            DataType = r.Int(DicomTag.RegionDataType) ?? 0,
            PhysicalUnitsX = r.Int(DicomTag.PhysicalUnitsXDirection) ?? 0,
            PhysicalUnitsY = r.Int(DicomTag.PhysicalUnitsYDirection) ?? 0,
            PhysicalDeltaX = r.Dbl(DicomTag.PhysicalDeltaX) ?? 0,
            PhysicalDeltaY = r.Dbl(DicomTag.PhysicalDeltaY) ?? 0,
        }).ToArray();
    }

    private static FrameGeometry[]? ReadPerFrame(DicomDataset ds)
    {
        var perFrame = ds.Items(DicomTag.PerFrameFunctionalGroupsSequence);
        if (perFrame.Count == 0) return null;
        var shared = ds.FirstItem(DicomTag.SharedFunctionalGroupsSequence);
        var result = new FrameGeometry[perFrame.Count];
        for (var i = 0; i < perFrame.Count; i++)
        {
            var f = perFrame[i];
            result[i] = new FrameGeometry
            {
                ImagePositionPatient = Exactly(Group(f, shared, DicomTag.PlanePositionSequence)?.Dbls(DicomTag.ImagePositionPatient), 3),
                ImageOrientationPatient = Exactly(Group(f, shared, DicomTag.PlaneOrientationSequence)?.Dbls(DicomTag.ImageOrientationPatient), 6),
                PixelSpacing = ValidPair(Group(f, shared, DicomTag.PixelMeasuresSequence)?.Dbls(DicomTag.PixelSpacing)),
                SliceThickness = Group(f, shared, DicomTag.PixelMeasuresSequence)?.Dbl(DicomTag.SliceThickness),
                RescaleSlope = NonZero(Group(f, shared, DicomTag.PixelValueTransformationSequence)?.Dbl(DicomTag.RescaleSlope)),
                RescaleIntercept = Group(f, shared, DicomTag.PixelValueTransformationSequence)?.Dbl(DicomTag.RescaleIntercept),
                WindowCenter = Group(f, shared, DicomTag.FrameVOILUTSequence)?.Dbl(DicomTag.WindowCenter),
                WindowWidth = Group(f, shared, DicomTag.FrameVOILUTSequence)?.Dbl(DicomTag.WindowWidth),
                FrameReferenceTimeMs = Group(f, shared, DicomTag.FrameContentSequence)?.Dbl(DicomTag.FrameReferenceTime),
                FrameAcquisitionDateTime = Group(f, shared, DicomTag.FrameContentSequence)?.Str(DicomTag.FrameAcquisitionDateTime),
            };
        }
        return result;
    }

    private static DicomDataset? Group(DicomDataset perFrame, DicomDataset? shared, DicomTag macro) =>
        perFrame.FirstItem(macro) ?? shared?.FirstItem(macro);
}
