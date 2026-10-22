using BetterDicomViewer.Core.Deidentification;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Synthetic;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace BetterDicomViewer.Tests;

public class DeidentificationTests
{
    private static DicomDataset Sample(string sopUid, string? referencedSop = null)
    {
        var ds = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, "1.2.826.0.1.3680043.2.1125.1.1" },
            { DicomTag.SeriesInstanceUID, "1.2.826.0.1.3680043.2.1125.1.2" },
            { DicomTag.FrameOfReferenceUID, "1.2.826.0.1.3680043.2.1125.1.3" },
            { DicomTag.PatientName, "DOE^JANE" },
            { DicomTag.PatientID, "MRN12345" },
            { DicomTag.PatientBirthDate, "19800214" },
            { DicomTag.PatientSex, "F" },
            { DicomTag.PatientWeight, "72.5" },
            { DicomTag.PatientAddress, "1 Main Street" },
            { DicomTag.StudyDate, "20240310" },
            { DicomTag.StudyTime, "101500" },
            { DicomTag.AccessionNumber, "ACC777" },
            { DicomTag.InstitutionName, "General Hospital" },
            { DicomTag.ReferringPhysicianName, "SMITH^JOHN" },
            { DicomTag.StationName, "STATIONX9" },
            { DicomTag.SeriesDescription, "CHEST JANE DOE" },
            { DicomTag.Modality, "CT" },
            { DicomTag.ImageType, "ORIGINAL", "PRIMARY", "AXIAL" },
        };
        ds.Add(new DicomSequence(DicomTag.OtherPatientIDsSequence, new DicomDataset { { DicomTag.PatientID, "OTHER-ID" } }));
        ds.Add(new DicomSequence(DicomTag.RequestAttributesSequence, new DicomDataset
        {
            { DicomTag.RequestedProcedureID, "RP1" },
            { DicomTag.ScheduledProcedureStepID, "SPS1" },
        }));
        var nested = new DicomDataset { { DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage } };
        if (referencedSop is not null) nested.Add(DicomTag.ReferencedSOPInstanceUID, referencedSop);
        nested.Add(new DicomSequence(DicomTag.ReferencedPatientSequence, new DicomDataset { { DicomTag.PatientName, "NESTED^NAME" } }));
        ds.Add(new DicomSequence(DicomTag.SourceImageSequence, nested));
        var privateTag = ds.GetPrivateTag(new DicomTag(0x0029, 0x0010, "ACME 1.0"));
        ds.Add(new DicomLongString(new DicomTag(0x0029, 0x1010, "ACME 1.0"), "PRIVATE PATIENT NOTE"));
        ds.Add(new DicomOtherByte(new DicomTag(0x6000, 0x3000), new byte[8]));
        ds.Add(new DicomLongString(new DicomTag(0x6000, 0x4000), "overlay comment"));
        _ = privateTag;
        return ds;
    }

    private static IEnumerable<string> AllStrings(DicomDataset ds)
    {
        foreach (var item in ds)
        {
            if (item is DicomSequence seq)
            {
                foreach (var child in seq.Items)
                foreach (var s in AllStrings(child))
                    yield return s;
            }
            else if (item is DicomStringElement e)
            {
                foreach (var v in e.Get<string[]>()) yield return v;
            }
        }
    }

    [Fact]
    public void BasicProfileRemovesIdentifiersIncludingNestedAndPrivate()
    {
        var deid = new Deidentifier(new DeidentificationOptions { PatientName = "SUBJECT01", PatientId = "S01" });
        var source = Sample("1.2.826.0.1.3680043.2.1125.1.10");
        var output = deid.Process(source);

        var text = string.Join("|", AllStrings(output));
        foreach (var secret in new[] { "DOE", "JANE", "MRN12345", "19800214", "1 Main Street", "ACC777", "General Hospital", "SMITH", "STATIONX9", "OTHER-ID", "NESTED", "PRIVATE PATIENT NOTE", "overlay comment", "20240310", "RP1", "SPS1" })
            Assert.DoesNotContain(secret, text);

        Assert.Equal("SUBJECT01", output.GetString(DicomTag.PatientName));
        Assert.Equal("S01", output.GetString(DicomTag.PatientID));
        Assert.Equal("YES", output.GetString(DicomTag.PatientIdentityRemoved));
        Assert.False(output.Contains(DicomTag.PatientWeight));
        Assert.False(output.Contains(new DicomTag(0x6000, 0x3000)));
        Assert.DoesNotContain(output, i => i.Tag.IsPrivate);
        Assert.Equal("113100", output.GetSequence(DicomTag.DeidentificationMethodCodeSequence).Items[0].GetString(DicomTag.CodeValue));
        Assert.Equal("CT", output.GetString(DicomTag.Modality));
        Assert.Equal("DOE^JANE", source.GetString(DicomTag.PatientName));
    }

    [Fact]
    public void UidsAreRemappedConsistentlyAcrossObjects()
    {
        var deid = new Deidentifier(new DeidentificationOptions());
        var a = deid.Process(Sample("1.2.826.0.1.3680043.2.1125.1.10"));
        var b = deid.Process(Sample("1.2.826.0.1.3680043.2.1125.1.11", referencedSop: "1.2.826.0.1.3680043.2.1125.1.10"));

        Assert.Equal(a.GetString(DicomTag.StudyInstanceUID), b.GetString(DicomTag.StudyInstanceUID));
        Assert.Equal(a.GetString(DicomTag.FrameOfReferenceUID), b.GetString(DicomTag.FrameOfReferenceUID));
        Assert.NotEqual(a.GetString(DicomTag.SOPInstanceUID), b.GetString(DicomTag.SOPInstanceUID));
        Assert.StartsWith("2.25.", a.GetString(DicomTag.SOPInstanceUID));
        Assert.NotEqual("1.2.826.0.1.3680043.2.1125.1.1", a.GetString(DicomTag.StudyInstanceUID));
        var reference = b.GetSequence(DicomTag.SourceImageSequence).Items[0].GetString(DicomTag.ReferencedSOPInstanceUID);
        Assert.Equal(a.GetString(DicomTag.SOPInstanceUID), reference);
        Assert.Equal(DicomUID.CTImageStorage.UID, a.GetString(DicomTag.SOPClassUID));
        Assert.True(a.GetString(DicomTag.SOPInstanceUID).Length <= 64);
    }

    [Fact]
    public void DateShiftAndPatientCharacteristicsOptions()
    {
        var deid = new Deidentifier(new DeidentificationOptions { Dates = DateHandling.Shift, DateShiftDays = -10, RetainPatientCharacteristics = true });
        var output = deid.Process(Sample("1.2.3.4.5"));
        Assert.Equal("20240229", output.GetString(DicomTag.StudyDate));
        Assert.Equal("72.5", output.GetString(DicomTag.PatientWeight));
        Assert.Equal("F", output.GetString(DicomTag.PatientSex));
        var codes = output.GetSequence(DicomTag.DeidentificationMethodCodeSequence).Items.Select(i => i.GetString(DicomTag.CodeValue)).ToList();
        Assert.Contains("113107", codes);
        Assert.Contains("113108", codes);
    }

    [Fact]
    public void ProfileTableCoversTheStandard()
    {
        var profile = new ConfidentialityProfile(new DeidentificationOptions());
        Assert.True(profile.EntryCount > 600);
        Assert.Equal(ProfileAction.Remove, profile.Resolve(new DicomTag(0x6002, 0x3000)));
        Assert.Equal(ProfileAction.Keep, profile.Resolve(new DicomTag(0x6001, 0x3000)));
        Assert.Equal(ProfileAction.ReplaceUid, profile.Resolve(DicomTag.SOPInstanceUID));
        Assert.Equal(ProfileAction.Keep, profile.Resolve(DicomTag.Rows));
        Assert.Equal(ProfileAction.ZeroLength, profile.Resolve(DicomTag.StudyDate));
    }

    private static string WriteUltrasound(string dir, string sopClass, string? burnedIn)
    {
        Directory.CreateDirectory(dir);
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, sopClass },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, "1.2.3" },
            { DicomTag.SeriesInstanceUID, "1.2.3.4" },
            { DicomTag.Modality, "US" },
            { DicomTag.PatientName, "DOE^JANE" },
            { DicomTag.Rows, (ushort)20 },
            { DicomTag.Columns, (ushort)30 },
            { DicomTag.BitsAllocated, (ushort)8 },
            { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)3 },
            { DicomTag.PlanarConfiguration, (ushort)0 },
            { DicomTag.PhotometricInterpretation, "RGB" },
        };
        if (burnedIn is not null) ds.Add(DicomTag.BurnedInAnnotation, burnedIn);
        var pd = DicomPixelData.Create(ds, true);
        pd.AddFrame(new MemoryByteBuffer(Enumerable.Repeat((byte)200, 20 * 30 * 3).ToArray()));
        var path = Path.Combine(dir, "US" + Guid.NewGuid().ToString("N")[..6]);
        new DicomFile(ds).Save(path);
        return path;
    }

    [Fact]
    public async Task ExportExcludesSuspectedBurnedInTextAndWritesReport()
    {
        using var tmp = new TempDir();
        var src = tmp.Sub("src");
        SyntheticDicom.WriteSeries(Path.Combine(src, "ct"), new SeriesOptions { Slices = 3, Columns = 16, Rows = 16 });
        WriteUltrasound(Path.Combine(src, "us"), DicomUID.UltrasoundImageStorage.UID, null);
        var lib = new DicomLibrary();
        await LibraryScanner.ScanAsync(lib, src, null, new ScanOptions { UseCache = false }, TestContext.Current.CancellationToken);
        var instances = lib.Snapshot().SelectMany(s => s.Series).SelectMany(s => s.Instances).ToList();

        var output = Path.Combine(tmp.Path, "out");
        var report = await DeidentificationExporter.ExportAsync(instances, [src], output, new DeidentificationOptions(), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Exported);
        Assert.Equal(1, report.ExcludedBurnedIn);
        Assert.True(File.Exists(Path.Combine(output, "DICOMDIR")));
        Assert.Contains("does not guarantee anonymity", File.ReadAllText(Path.Combine(output, "deidentification-report.txt")));

        var exported = Directory.EnumerateFiles(Path.Combine(output, "DICOM"), "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(3, exported.Count);
        foreach (var f in exported)
        {
            var ds = DicomFile.Open(f).Dataset;
            Assert.Equal("ANONYMOUS", ds.GetString(DicomTag.PatientName));
            Assert.Equal("YES", ds.GetString(DicomTag.PatientIdentityRemoved));
        }

        // A second export into a source folder is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeidentificationExporter.ExportAsync(instances, [src], Path.Combine(src, "export"), new DeidentificationOptions(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RedactionBlacksOutRegionsInPixelData()
    {
        using var tmp = new TempDir();
        var src = tmp.Sub("src");
        WriteUltrasound(src, DicomUID.UltrasoundImageStorage.UID, "YES");
        var info = HeaderReader.Read(Directory.EnumerateFiles(src).Single());
        var options = new DeidentificationOptions { BurnedIn = BurnedInPolicy.Redact };
        options.Redactions[DeidentificationOptions.SizeKey(30, 20)] = [new RedactionRegion(0, 0, 30, 5)];

        var output = Path.Combine(tmp.Path, "out");
        var report = await DeidentificationExporter.ExportAsync([info], [src], output, options, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Redacted);

        var file = Directory.EnumerateFiles(Path.Combine(output, "DICOM"), "*", SearchOption.AllDirectories).Single();
        var ds = DicomFile.Open(file).Dataset;
        var frame = DicomPixelData.Create(ds).GetFrame(0).Data;
        Assert.Equal(0, frame[0]);
        Assert.Equal(0, frame[(4 * 30 + 29) * 3]);
        Assert.Equal(200, frame[(5 * 30) * 3]);
    }
}
