# Better DICOM Viewer

A native Windows 11 DICOM viewer focused on fast interaction and a clear, dark-only interface.

## Status

Project setup only. There is no runnable application yet. The implementation brief is in [docs/implementation-brief.md](docs/implementation-brief.md). Features below are planned, not implemented.

## Planned capabilities

- Open local DICOM folders and DICOMDIR archives without copying the source scans.
- Browse studies and series, scroll slices, pan, zoom, rotate, and adjust window and level.
- Linked axial, coronal, sagittal, and oblique views, plus 3D volume rendering.
- Distance, angle, and region measurements, annotations, and scan comparisons.
- PET/CT fusion and SUV measurements when the required metadata supports them.
- Segmentation tools for outlining structures, with editable local results.
- Export screenshots, measurements, and de-identified DICOM copies.
- Fully offline operation. No accounts, telemetry, or cloud uploads.

## Using the app

Once a build is available, the intended workflow is:

1. Open a local scan folder or its DICOMDIR file.
2. Select a study and series from the library.
3. Scroll through slices or choose a multiplanar or 3D view.
4. Choose a measurement or segmentation tool. The shortcuts panel will list keyboard controls.
5. Export results to a separate folder. Original scan files remain unchanged.

The implementation must replace these provisional instructions with tested installation steps, launch commands, and the actual shortcut list.

## Data handling

Source scans stay in their original locations. The app may store a local index, thumbnails, and user annotations outside the source folders. Scan data, patient information, exports, and local caches must never enter this public repository.

This project is not validated for clinical diagnosis. DICOM de-identification must address both metadata and identifiers embedded in images; removing patient-name tags alone is insufficient.
