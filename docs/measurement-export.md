# Measurement export format

**Export CSV…** and **Export JSON…** in the Measurements panel (or Ctrl+Shift+M) write every measurement of the active study. Values are recalculated at export time from the loaded image data. If a series is not loaded, lengths and angles in patient space are still computed from their coordinates, but ROI statistics are left empty.

Patient name and ID columns exist but stay empty by default.

## CSV

UTF-8 with a byte order mark, comma separated, one header row, values quoted when they contain commas, quotes or line breaks. Numbers use `.` as the decimal separator. Empty cells mean "not applicable" or "not available".

| Column | Meaning |
| --- | --- |
| `Label` | Measurement label shown on the image (for example `L1`, `R2`) |
| `Type` | `Length`, `Angle`, `Ellipse ROI`, `Rectangle ROI`, `Freehand ROI`, `Point`, `Arrow` or `Text` |
| `Modality` | Modality of the series |
| `SeriesNumber`, `SeriesDescription` | Series identification |
| `ImageSet` | Image set within the series, for example `Axial, 229 images` |
| `Frame` | Zero-based frame index for 2D image sets; empty for volumes |
| `Length`, `LengthUnit` | Distance; unit `mm`, or `px` when the image has no calibration |
| `AngleDegrees` | Angle at the vertex, in degrees |
| `Area`, `AreaUnit` | ROI area in `mm²` or `px²` |
| `VoxelCount` | Distinct voxels or pixels inside the ROI used for statistics |
| `Mean`, `StdDev`, `Min`, `Max`, `ValueUnit` | ROI statistics in modality units (`HU`, `Bq/mL`, or empty when the unit is not defined) |
| `SuvMax`, `SuvMean`, `SuvPeak` | Body-weight SUV of the measured PET series (empty if SUV is unavailable) |
| `PetSuvMax`, `PetSuvMean`, `PetSuvPeak` | SUV of the fused PET under an ROI drawn on CT |
| `PointValue`, `PointSuv` | Value (and SUV) at a point measurement |
| `Text` | Annotation text |
| `CalibrationNote` | Calibration caveat, for example detector-plane spacing or uncalibrated pixels |
| `CoordinateSystem` | `DICOM patient (LPS), mm` for volumes, or `image pixels (column, row), scaled for pixel aspect` for 2D images |
| `Points` | Defining points as `x;y;z` separated by ` \| ` in the coordinate system above |
| `CreatedUtc` | Creation time, ISO 8601 UTC |
| `PatientName`, `PatientId` | Empty unless explicitly enabled in a future version |

Definitions:

- Standard deviation is the sample standard deviation (n − 1).
- ROI statistics use every voxel whose centre falls inside the ROI on the plane it was drawn on. On oblique planes the plane is sampled at half the in-plane voxel spacing and each voxel is counted once.
- SUVpeak is the highest mean SUV in a 1 cm³ sphere (radius 6.2 mm) centred on one of the 25 hottest voxels in the ROI.
- SUV is body-weight SUV in g/mL, computed as described in [the README](../README.md#using-the-app) and only when the metadata validates.

## JSON

```json
{
  "format": "better-dicom-viewer.measurements",
  "version": 1,
  "exportedUtc": "2026-10-21T22:30:00Z",
  "measurements": [
    {
      "Label": "R1",
      "Type": "Ellipse ROI",
      "Modality": "CT",
      "SeriesNumber": 2,
      "SeriesDescription": "CT 3 mm (synthetic phantom)",
      "ImageSet": "Axial, 110 images",
      "Area": 1256.6,
      "AreaUnit": "mm²",
      "VoxelCount": 1551,
      "Mean": 34.9,
      "StdDev": 22.5,
      "Min": 6,
      "Max": 74,
      "ValueUnit": "HU",
      "PetSuvMax": 9.12,
      "PetSuvMean": 6.21,
      "PetSuvPeak": 9.02,
      "CoordinateSystem": "DICOM patient (LPS), mm",
      "Points": "-95;-30;-201 | -55;10;-201",
      "CreatedUtc": "2026-10-21T22:29:41Z"
    }
  ]
}
```

Property names match the CSV columns; null values are omitted.
