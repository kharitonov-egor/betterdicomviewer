using FellowOakDicom;
using FellowOakDicom.Media;

namespace BetterDicomViewer.Core.Dicom;

/// <summary>Reads a DICOMDIR into provisional instance records so the library can show studies before every header is parsed.</summary>
public static class DicomDirReader
{
    public static bool IsDicomDirFile(string path) =>
        string.Equals(Path.GetFileName(path), "DICOMDIR", StringComparison.OrdinalIgnoreCase);

    public static List<InstanceInfo> Read(string dicomDirPath)
    {
        DicomRuntime.EnsureInitialized();
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(dicomDirPath)) ?? ".";
        var dir = DicomDirectory.Open(dicomDirPath);
        var result = new List<InstanceInfo>();

        foreach (var patient in dir.RootDirectoryRecordCollection)
        {
            foreach (var study in Children(patient))
            {
                foreach (var series in Children(study))
                {
                    foreach (var leaf in Children(series))
                    {
                        var fileId = leaf.Strs(DicomTag.ReferencedFileID);
                        if (fileId.Length == 0) continue;
                        var path = Path.Combine([baseDir, .. fileId]);
                        result.Add(new InstanceInfo
                        {
                            FilePath = path,
                            IsProvisional = true,
                            TransferSyntaxUid = leaf.Str(DicomTag.ReferencedTransferSyntaxUIDInFile) ?? "",
                            SopClassUid = leaf.Str(DicomTag.ReferencedSOPClassUIDInFile) ?? "",
                            SopInstanceUid = leaf.Str(DicomTag.ReferencedSOPInstanceUIDInFile) ?? "",
                            StudyInstanceUid = study.Str(DicomTag.StudyInstanceUID) ?? "",
                            SeriesInstanceUid = series.Str(DicomTag.SeriesInstanceUID) ?? "",
                            PatientName = patient.Str(DicomTag.PatientName),
                            PatientId = patient.Str(DicomTag.PatientID),
                            PatientBirthDate = patient.Str(DicomTag.PatientBirthDate),
                            PatientSex = patient.Str(DicomTag.PatientSex),
                            StudyDate = study.Str(DicomTag.StudyDate),
                            StudyTime = study.Str(DicomTag.StudyTime),
                            StudyDescription = study.Str(DicomTag.StudyDescription),
                            AccessionNumber = study.Str(DicomTag.AccessionNumber),
                            StudyId = study.Str(DicomTag.StudyID),
                            Modality = series.Str(DicomTag.Modality),
                            SeriesNumber = series.Int(DicomTag.SeriesNumber),
                            SeriesDescription = series.Str(DicomTag.SeriesDescription),
                            InstanceNumber = leaf.Int(DicomTag.InstanceNumber),
                            Rows = leaf.Int(DicomTag.Rows) ?? 0,
                            Columns = leaf.Int(DicomTag.Columns) ?? 0,
                            NumberOfFrames = Math.Max(1, leaf.Int(DicomTag.NumberOfFrames) ?? 1),
                            HasPixelData = IsImageRecord(leaf),
                        });
                    }
                }
            }
        }
        return result;
    }

    private static bool IsImageRecord(DicomDirectoryRecord record)
    {
        var type = record.Str(DicomTag.DirectoryRecordType);
        return string.Equals(type, "IMAGE", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DicomDirectoryRecord> Children(DicomDirectoryRecord record) =>
        record.LowerLevelDirectoryRecordCollection ?? Enumerable.Empty<DicomDirectoryRecord>();
}
