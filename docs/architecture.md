# Architecture and stack decision

## Decision

Better DICOM Viewer is a C# (.NET 10) WPF application using the Windows 11 Fluent theme in dark mode. DICOM parsing, DICOMDIR reading and pixel decoding use fo-dicom with the fo-dicom.Codecs native codec package. Reslicing, volume rendering and surface extraction use VTK 9.7.1 through a small native C++ bridge, `bdv_vtk.dll`, called with P/Invoke.

| Layer | Choice | License |
| --- | --- | --- |
| UI | WPF on .NET 10, `ThemeMode="Dark"` (Fluent) | MIT |
| MVVM helpers | CommunityToolkit.Mvvm 8.4 | MIT |
| DICOM parsing, DICOMDIR | fo-dicom 5.2.6 | MS-PL |
| Decoding of compressed transfer syntaxes | fo-dicom.Codecs 5.16.7 (libijg, CharLS, OpenJPEG, OpenJPH) | MS-PL, with IJG, BSD-3, BSD-2 components |
| Reslicing, MPR, slabs, GPU volume rendering, surfaces | VTK 9.7.1, statically linked into `bdv_vtk.dll` | BSD-3-Clause, third-party notices in `licenses/VTK` |
| Tests | xUnit v3 | Apache-2.0 (development only) |

### Why this combination

- **Native Windows 11 UI.** WPF is Microsoft's desktop UI framework and, since .NET 9, ships a Fluent theme that matches Windows 11 controls, typography, dark mode and Mica. It supports per-monitor DPI, UI Automation, keyboard navigation and native file dialogs. There is no browser engine anywhere in the process.
- **Established imaging engine.** VTK is the reference open-source engine for medical visualization (3D Slicer, ParaView, MITK). `vtkImageReslice` handles arbitrary oblique planes, thick slabs (MIP, MinIP, mean) and interpolation. `vtkGPUVolumeRayCastMapper` handles ray casting. `vtkDiscreteFlyingEdges3D` extracts segmentation surfaces. VTK's BSD license permits static linking.
- **Established DICOM toolkit.** fo-dicom is the most widely used .NET DICOM library. fo-dicom.Codecs adds native decoders for JPEG Baseline, Extended, Lossless, JPEG-LS, JPEG 2000 and HTJ2K under MS-PL.
- **One language for application logic.** Library indexing, geometry, measurements, SUV, segmentation and de-identification are C# and unit tested. The native bridge only resamples and renders memory it is handed.

### Alternatives considered

- **Qt + VTK in C++.** Qt is a strong native toolkit, but its Windows 11 look is an emulation and the brief asks for native Windows UI. It would also move every feature into C++.
- **WinUI 3.** It is the newest Windows UI stack, but command-line builds, unpackaged deployment and hosting a native render surface are less mature than in WPF. WPF's Fluent theme gives the same visual language.
- **ActiViz (VTK for .NET).** Kitware's current ActiViz releases are closed source, so they are unsuitable for a public open-source project.
- **Custom Direct3D renderer.** It would be fast, but it means maintaining a home-made imaging engine, which the brief rules out.

## Components

```
src/BetterDicomViewer.Core        Pure .NET: DICOM index, geometry, decoding, volumes, SUV, measurements,
                                  segmentation, de-identification, exports. No UI, fully unit tested.
src/BetterDicomViewer.Imaging     P/Invoke wrappers over bdv_vtk.dll: Reslicer, SliceRenderer (layer
                                  composition), VolumeRenderer (offscreen GPU ray casting).
src/BetterDicomViewer.App         WPF application (views, view models, input tools, dialogs).
src/BetterDicomViewer.Synthetic   Synthetic DICOM generator for tests, benchmarks and public screenshots.
native/bdv_vtk                    C ABI over VTK (see native/bdv_vtk/src/bdv_vtk.h).
tests/BetterDicomViewer.Tests     Unit and integration tests on synthetic data.
tools/BetterDicomViewer.Bench     Benchmarks against local data (paths come from local.settings.json).
```

## Data flow

1. **Indexing.** `LibraryScanner` reads a DICOMDIR first, if one exists, and shows its studies immediately as provisional entries. It then walks the folder recursively, sniffs the file content (the `DICM` preamble or a raw dataset), not the extension, and parses headers in parallel up to the pixel data with a bounded worker count. The header index is cached per root under `%LOCALAPPDATA%\BetterDicomViewer\index`. A reopen compares file size and modification time and parses only changed files.
2. **Display sets.** `SeriesAnalyzer` splits a series into display sets. It separates localizers, groups by orientation, size and spacing, sorts slices by position along the plane normal (never by file name), detects gantry tilt (kept as a sheared affine), resamples uneven spacing with a warning and keeps the native slices available, and turns multi-frame objects into cine sets or volumes when per-frame geometry exists.
3. **Decoding.** `VolumeLoader` allocates one unmanaged, 64-byte-aligned buffer per volume and decodes the slice in view first, then works outward with parallel workers. CT stays int16 in Hounsfield units. PET and non-integer rescales use float32. Colour uses 8-bit RGB. Two-dimensional sets such as radiographs and cine loops use `FrameSetSource`, which decodes frames on demand into a bounded LRU cache with prefetch.
4. **Rendering.** Every 2D viewport renders exactly at device-pixel resolution. `SliceRenderer` asks VTK to reslice each layer (base image, PET fusion, label map) straight onto the viewport's pixel grid, then applies the DICOM VOI LUT, colour maps and alpha blending in C#. The 4x4 reslice matrix comes from patient geometry computed in C#, so VTK always samples in voxel index space. 3D views use an offscreen VTK render window on a dedicated thread and copy the frame into a WPF bitmap.
5. **Overlays.** Measurements, annotations, crosshairs and labels are WPF vector graphics drawn over the image from patient coordinates.

## Threading

- The UI thread handles input and composes 2D frames. A 1024x1024 frame takes about 1 ms, so rendering never needs another thread.
- Indexing and decoding use `Parallel.ForEachAsync` with explicit parallelism limits and cancellation tokens.
- All VTK OpenGL work for one 3D view runs on that view's dedicated thread. Render requests coalesce, so only the latest camera state is drawn.

## Memory and caches

- Volumes are reference counted while visible. A process-wide budget evicts the least recently used volumes that are not on screen.
- 2D frame caches are capped at 512 MB per display set by default, including pyramid levels.
- On-disk state lives under `%LOCALAPPDATA%\BetterDicomViewer`: the header index, annotations, segmentations and the generated demo study. Thumbnails and decoded images stay in memory. The app can clear the index from the Export and privacy panel, and nothing is ever written next to the source scans.
- Large 2D images (1536 px or more) get 2x2 box-filtered pyramid levels so they are resampled from a matching level when shown small.

## Deployment requirements

- Windows 10 1809 or later (Windows 11 recommended for the Fluent look), x64.
- A GPU with OpenGL 3.2 or newer for 3D. 2D viewing, MPR and every other feature work without one.
- The release package is self-contained: the .NET runtime, `bdv_vtk.dll` (statically linked VTK and C runtime) and the codec DLLs ship together. No installer, administrator rights or network access are needed. Releases ship it two ways: a single exe that unpacks itself to `%TEMP%\.net` on first launch, and a zip of the same files.

## Build requirements

- .NET 10 SDK.
- Visual Studio 2022 Build Tools with the C++ workload (or an equivalent MSVC toolset), CMake 3.20+ and Ninja. These are needed only to rebuild VTK and the bridge.
- See the README for the exact commands.
