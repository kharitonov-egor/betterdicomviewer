# Third-party notices

Better DICOM Viewer uses the components below. Release packages include the license texts in the `licenses` folder (`scripts/package.ps1` collects them).

| Component | Version | License | Use |
| --- | --- | --- | --- |
| [.NET runtime and WPF](https://github.com/dotnet/runtime) | 10.0 | MIT | Application runtime and UI framework (self-contained in releases) |
| [fo-dicom](https://github.com/fo-dicom/fo-dicom) | 5.2.6 | MS-PL | DICOM parsing, DICOMDIR, writing de-identified files |
| [fo-dicom.Codecs](https://github.com/Efferent-Health/fo-dicom.Codecs) | 5.16.7 | MS-PL | Native decoders for JPEG, JPEG-LS, JPEG 2000 and HTJ2K |
| libijg (inside fo-dicom.Codecs) | | IJG license | JPEG baseline, extended and lossless |
| CharLS (inside fo-dicom.Codecs) | | BSD-3-Clause | JPEG-LS |
| OpenJPEG (inside fo-dicom.Codecs) | | BSD-2-Clause | JPEG 2000 |
| OpenJPH (inside fo-dicom.Codecs) | | BSD-2-Clause | HTJ2K |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | Dependency of the UI project |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | | MIT | Dependency of fo-dicom |
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | | MIT | Dependency of fo-dicom |
| [VTK](https://vtk.org) | 9.7.1 | BSD-3-Clause | Reslicing, GPU volume rendering, surface extraction (statically linked into `bdv_vtk.dll`) |
| VTK third-party modules (glad, fmt, freetype, jpeg, png, zlib, lz4, lzma, pugixml, jsoncpp, kissfft, loguru, eigen, exprtk, fast_float, nlohmann_json, pegtl, tiff, token, utf8, scn, kwiml, metaio, vtksys) | bundled with VTK 9.7.1 | Various permissive licenses (see `licenses/VTK`) | Linked as part of the VTK modules in use |

DICOM PS3.15 Table E.1-1 (2026d) was converted into `src/BetterDicomViewer.Core/Deidentification/ConfidentialityProfileTable.g.cs` by `tools/deid/generate_profile_table.py`. The DICOM Standard is published by NEMA.

Development-only tools (not shipped): xUnit v3 (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), SimpleITK (Apache-2.0, used by `tools/validation`), pydicom (MIT, used for local data surveys).
