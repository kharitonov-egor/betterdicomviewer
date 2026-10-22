# Implementation brief

## Product requirements

Build Better DICOM Viewer as a native Windows 11 desktop application. Performance and an understandable, modern dark-only UI are the primary priorities. Use native Windows UI, not Electron or a browser wrapper. Choose a maintained native imaging stack and document the architecture decision, dependency licenses, and deployment requirements before implementation. Prefer established decoding, geometry, and rendering libraries over a homemade imaging engine.

The application must run fully offline with no accounts, telemetry, cloud uploads, remote fonts, or runtime downloads. Development dependency installation is separate from offline application use. Reference source files in place without duplicating entire studies. Keep caches bounded, removable, and outside the source directories. Never modify source DICOM files.

## Required tools

- Local library with studies, series, search, thumbnails, DICOMDIR import, and recursive folder discovery, including extensionless DICOM files.
- CT, MR, radiography, ultrasound still images and cine, and PET support. Clearly identify unsupported transfer syntaxes or object types instead of silently skipping them.
- Slice scrolling, pan, zoom, rotation, flips, window/level presets and manual adjustment, orientation labels, and reset.
- Linked axial/coronal/sagittal reconstructions, crosshairs, oblique reslicing, and 3D volume rotation/rendering with useful opacity controls.
- Distance and angle measurements, region statistics, editable annotations, undo/redo, and side-by-side comparison.
- PET/CT fusion, adjustable blending, and SUV measurements. Validate units, decay correction, dose, timing, and weight metadata; report unavailable values rather than guessing.
- Segmentation with manual drawing/painting, erase, thresholding, editing across slices, undo/redo, and local persistence/export. Automatic segmentation is optional and must not require cloud services.
- Screenshot export, measurement export in a documented format, and DICOM de-identification export to new files.

## UI and performance

Design a restrained Windows 11 interface with legible typography, clear active-tool states, tooltips, keyboard navigation, accessible focus states, and a discoverable shortcut reference. Dark mode only. Keep the image area prominent. Test display scaling and window resizing.

Index and decode off the UI thread. Support cancellation, bounded parallelism, lazy loading, neighboring-slice prefetch, and bounded caches. Use GPU rendering where justified. Measure cold open, cached reopen, first image latency, scrolling frame time, and peak memory on the local machine. Set realistic targets after inspecting hardware and study sizes; report measurements, not unsupported performance claims.

## Correctness and privacy

Use DICOM patient coordinates, orientation, position, spacing, rescale information, photometric interpretation, and frame metadata correctly. Do not sort slices by filename alone. Handle nonuniform spacing, localizers, and mixed series deliberately. Measurements must use physical units and identify missing calibration. Validate geometry with synthetic fixtures of known dimensions and compare representative local studies with an established viewer when available.

De-identification must use a documented DICOM confidentiality profile, coherent UID remapping, private-tag handling, and nested-sequence processing. Detect or flag burned-in text and unsupported objects. Never claim complete anonymity from tag removal alone. Export must preserve originals and make its scope clear.

Real medical records are private local test inputs only. Never commit or upload images, patient metadata, screenshots containing identifiers, local database files, or logs with personal information. Use synthetic data for public tests and screenshots. File extensions alone are not a sufficient privacy check because DICOM files can be extensionless.

## Delivery

Implement working functionality, not mock controls. Maintain a capability checklist that separates implemented, validated, unsupported, and remaining work. Build and run the application on Windows, test the actual UI, and fix failures. Provide a reproducible build, a usable local executable or package, and concise user instructions covering opening scans, viewing modes, hotkeys, measurements, segmentation, exports, and limitations. Do not call the complete scope finished while required features remain placeholders.

The repository is public. Review every staged file before publishing. Keep personal test inputs and machine-specific paths out of public documentation. Commit tested source changes in logical units and keep the README accurate.
