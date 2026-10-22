# Capability checklist

Status key:

- **Validated**: covered by automated tests on synthetic data, or cross-checked against an independent reader on the local test scans.
- **Implemented**: works in the app and was exercised through the real UI (scripted runs on synthetic and local data), but has no automated assertion.
- **Partial**: works with a stated limitation.
- **Unsupported**: identified and reported to the user, not silently ignored.
- **Not done**: required or desirable work that does not exist yet.

## Library and loading

| Capability | Status | Notes |
| --- | --- | --- |
| Recursive folder discovery, extensionless files | Validated | Content sniffing (`DICM` preamble or raw dataset); `LibraryTests` |
| DICOMDIR import | Validated | Studies listed from DICOMDIR immediately, then headers parsed |
| Studies, series, search, thumbnails | Implemented | Search matches name, ID, date, accession, descriptions, modality |
| Local header index with change detection | Validated | Size and modification time per file; vanished files removed |
| Cancellable scanning with bounded parallelism | Validated | `ScanCanBeCancelled`; parallelism capped at 2–8 workers |
| Progressive volume loading (slice in view first, then outward) | Implemented | First image at 96 ms for a 437-slice CT |
| Bounded memory | Implemented | Volume budget 45% of RAM (1–12 GB); 2D frame cache 512 MB per set; LRU eviction of unleased data |
| Non-image objects and unsupported syntaxes listed with a reason | Validated | Presentation states, SR, video (MPEG2 tested) |

## Image formats

| Capability | Status | Notes |
| --- | --- | --- |
| CT, PET volumes | Validated | 38 local volumes matched SimpleITK/GDCM geometry and voxel values |
| MR | Partial | Same code path as CT, tested on synthetic MR only; no MR in the local test data |
| Radiography (CR, DX) | Implemented | Imager Pixel Spacing and magnification rules tested; pyramid levels for large images |
| Ultrasound stills and cine (RGB, YBR, monochrome) | Implemented | YBR_FULL_422 JPEG decodes to neutral RGB (checked on local data); region calibration tested |
| Enhanced multi-frame (per-frame functional groups) | Validated | Synthetic Enhanced CT with per-frame positions |
| Transfer syntaxes: implicit/explicit VR, big endian, RLE, JPEG baseline, extended, lossless, JPEG-LS, JPEG 2000, HTJ2K | Validated (six lossless syntaxes round-tripped) | Decoding through fo-dicom.Codecs |
| Video transfer syntaxes (MPEG2, H.264, HEVC) | Unsupported | Listed with the reason |
| Float or double pixel data, segmented palette LUTs | Unsupported | Reported per object |
| Modality LUT Sequence | Partial | Not applied; a warning is shown and Rescale Slope/Intercept are used |
| Presentation states (GSPS) | Unsupported | Listed, not applied |
| NM multi-frame | Partial | Shown as 2D frames without NM detector geometry |

## Viewing

| Capability | Status | Notes |
| --- | --- | --- |
| Slice scrolling, pan, zoom, in-plane rotation, flips, reset, fit | Implemented | |
| Window/level drag, numeric entry, DICOM, CT and SUV presets, VOI LUT functions | Validated (VOI math) | LINEAR, LINEAR_EXACT, SIGMOID per PS3.3 |
| Orientation labels | Validated | From patient vectors; Patient Orientation for projection images |
| Slice ordering by position along the normal, localizer separation, gantry tilt, uneven spacing | Validated | Uneven spacing is resampled and flagged; native slices stay available |
| Linked axial, coronal, sagittal MPR with reference lines | Implemented | |
| Crosshair and oblique reslicing | Implemented | Geometry of arbitrary planes validated in `ResliceTests` |
| Thick slab mean, MIP, MinIP | Validated | Centred on the plane for axis-aligned and oblique planes |
| 3D GPU volume rendering, presets, MIP/MinIP/average, opacity shift and strength, cropping | Implemented | Waits for the full volume before uploading |
| Cine playback | Implemented | Frame Time or Recommended Display Frame Rate |
| Side-by-side comparison with linked scrolling | Implemented | Same frame of reference: same patient position. Other studies: offset linking, aligned manually. No image registration |

## Measurements and annotations

| Capability | Status | Notes |
| --- | --- | --- |
| Length, angle in patient millimetres | Validated | |
| Ellipse, rectangle, freehand ROI: area, mean, SD, min, max | Validated | Per-voxel statistics with deduplication |
| Calibration rules for 2D images (Pixel Spacing, Imager Pixel Spacing, magnification, ultrasound regions, pixels) | Validated | Caveats shown in labels |
| Point value, arrow and text annotations | Implemented | |
| Editing (handles, move), delete, undo/redo | Implemented | |
| Persistence per study, outside the scan folders | Validated | |
| CSV and JSON export | Validated | Format in [measurement-export.md](measurement-export.md) |

## PET/CT

| Capability | Status | Notes |
| --- | --- | --- |
| Fusion with opacity, colour map and range | Implemented | Alignment uses patient coordinates; a different frame of reference produces a warning |
| SUVbw (START, ADMIN, NONE decay correction; GML; Philips CNTS factor) | Validated | QIBA reference method plus Decay Factor cross-check |
| Refusal when units, dose, half-life, times or weight are missing or implausible | Validated | Problems listed in the fusion panel and ROI labels |
| SUVmax, SUVmean, SUVpeak (1 cm³ sphere) in ROIs | Validated | Synthetic lesion programmed at SUV 9.0 measured 9.12 max and 9.02 peak |
| SUL (lean body mass) | Partial | Ratio shown when height and sex exist; not used in ROI statistics |
| PET/CT layout with 3D MIP | Implemented | |

## Segmentation

| Capability | Status | Notes |
| --- | --- | --- |
| Brush, eraser, 3D sphere brush, intensity-limited painting | Validated | |
| Region growing (2D or 3D) and threshold fill | Validated | |
| Shape-based interpolation between slices | Validated | |
| Undo/redo of every edit | Validated | |
| Multiple named, coloured segments | Implemented | |
| Statistics (volume, mean, SD; PET SUV when fused) | Validated | |
| Local persistence (NRRD in LPS) | Validated | Restored only onto the same voxel grid |
| Export NRRD, NIfTI, STL, OBJ, CSV | Validated (NRRD, NIfTI, CSV); Implemented (STL, OBJ) | |
| Segment surfaces in 3D | Implemented | Re-extraction is rate-limited during edits |
| Automatic segmentation | Not done | Optional in the brief |
| DICOM SEG export | Not done | NRRD and NIfTI are offered instead |

## Export and privacy

| Capability | Status | Notes |
| --- | --- | --- |
| Screenshots (active or all viewports, overlays optional, patient details hidden by default) | Implemented | |
| De-identification: PS3.15 Basic Profile from the 2026d table, options, nested sequences, private tags, coherent UID remapping | Validated | See [deidentification.md](deidentification.md) |
| Burned-in text: flagging, exclusion, redaction rectangles | Validated | Redaction verified with pydicom on exported files |
| Export written to a new empty folder outside the scan folders, with DICOMDIR and report | Validated | |
| Clean Descriptors, Clean Structured Content, Clean Graphics options | Unsupported | Descriptors are removed (or kept verbatim on request, with a warning) |
| OCR detection of burned-in text | Not done | Flags rely on Burned In Annotation, SOP class, modality and image type |
| Structured reports, presentation states, PDFs in de-identified export | Unsupported | Excluded and listed in the report |

## Platform, UI and performance

| Capability | Status | Notes |
| --- | --- | --- |
| Native Windows UI (WPF Fluent, dark only), no browser engine | Implemented | |
| Tooltips with keys, shortcut reference (F1), command palette (Ctrl+K) | Implemented | Shortcuts generated from one registry |
| Keyboard navigation and focus visuals | Partial | Standard WPF focus handling in panels; viewports take focus on click; no full keyboard-only audit |
| Display scaling and resizing | Partial | Tested at 125% on a 2560×1440 display and several window sizes; per-monitor DPI changes handled in code but not tested on a second monitor |
| Offline operation, no telemetry | Implemented | No network code in the app |
| GPU selection on hybrid laptops | Implemented | Sets the Windows per-app preference unless the user has chosen one |
| Benchmarks of indexing, first image, scrolling, slabs, 3D, memory | Implemented | [benchmarks.md](benchmarks.md) |
| Truly cold-cache timing (after flushing the OS file cache) | Not done | Requires administrator rights; timings may include OS caching |
| Installer (MSIX) and code signing | Not done | Release is a self-contained zip |
| Automated UI tests with assertions | Partial | One end-to-end smoke test drives the real app (`tests/BetterDicomViewer.UiTests`); other UI behaviour was checked through scripted runs and screenshots |
| Localisation | Not done | English only |
