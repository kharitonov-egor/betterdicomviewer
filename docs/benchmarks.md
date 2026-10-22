# Benchmarks

Machine: Intel Core i7-13650HX (14 cores, 20 threads), 32 GB RAM, WD SN740 NVMe SSD, NVIDIA RTX 4050 Laptop GPU plus Intel UHD integrated graphics, Windows 11 Pro, display at 125% scaling. Release build, .NET 10.

Data: two copied PET/CT discs (2,824 and 4,614 files, 0.9 and 3.1 GB) and the synthetic demo study. The benchmark tool prints counts and timings only. Paths come from the gitignored `local.settings.json`.

Reproduce:

```powershell
dotnet build tools\BetterDicomViewer.Bench -c Release
dotnet tools\BetterDicomViewer.Bench\bin\Release\net10.0\BetterDicomViewer.Bench.dll all   # scan, open, vr
```

"Cold" means no local index cache; the operating system may still hold the files in its cache, because flushing it needs administrator rights.

## Indexing

| | Disc 1 (2,824 files) | Disc 2 (4,614 files) |
| --- | --- | --- |
| DICOM files found / other files | 2,767 / 57 | 4,553 / 61 |
| First studies listed (DICOMDIR) | 411 ms | 593 ms |
| Full header index, no cache | 1,161 ms | 1,495 ms |
| Reopen with index cache | 172 ms | 367 ms |
| Peak working set during scan | 137 MB | 137 MB |

## Opening and scrolling (2D)

Viewport 1024 × 1024 device pixels; times include native reslicing and composition into the bitmap.

| Series | First slice decoded | First image | Full volume | Axial p50 / p95 | Coronal p50 | Sagittal p50 | Oblique p50 | 30 mm coronal MIP |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| CT 512×512×229, JPEG Lossless | 331 ms¹ | 404 ms¹ | 476 ms | 1.29 / 1.74 ms | 0.81 ms | 1.52 ms | 2.17 ms | 8.6 ms |
| PET 200×200×229, float, JPEG Lossless | 65 ms | 85 ms | 175 ms | 2.14 / 11.7 ms | 2.30 ms | 2.15 ms | 3.40 ms | 4.3 ms |
| CT 512×512×437, uncompressed | 92 ms | 96 ms | 170 ms | 0.97 / 1.30 ms | 1.19 ms | 2.51 ms | 3.71 ms | 16.9 ms |

¹ First series of the run, so it includes codec and JIT warm-up.

2D images:

| Series | First frame | Decode and render per frame p50 |
| --- | --- | --- |
| US cine 1182×899, 262 frames, JPEG Lossless | 51 ms | 21 ms (decode bound; cine prefetches ahead) |
| DX 4232×3468, 16-bit | 69 ms | 2.2 ms after decode |
| CR 2792×2743, 16-bit | 41 ms | 1.7 ms |

Peak working set after opening every benchmark series: 537 MB.

In the app, the status bar reports the same figures for each open. For example, the synthetic CT opened with its first image in 184 ms and fully loaded in 341 ms.

## 3D rendering

Volume 384 × 384 × 110 (synthetic CT), soft-tissue preset with shading and gradient opacity, 900 × 900 pixels:

| GPU | First frame (upload) | Still frame p50 | Interactive frame p50 |
| --- | --- | --- | --- |
| Intel UHD (integrated) | 521 ms | 169 ms | 49 ms |
| RTX 4050 (per-app high-performance preference) | 284 ms | 9.8 ms | 3.9 ms |

The app sets the Windows high-performance preference for its executable on first start, unless the user has chosen one, and restarts itself once so the setting takes effect. 3D then runs on the discrete GPU. Interactive frames use half the ray samples and 1.6× image subsampling, then a full-quality frame follows when the drag ends.

## Application start

Self-contained release build (ReadyToRun): the main window is ready for input about 2.7 s after launch on a cold start. A warm start is faster.

## Targets

| Interaction | Target | Measured |
| --- | --- | --- |
| Scroll, pan, zoom, window/level | ≤ 16 ms per frame (60 Hz) | 1–4 ms |
| Thick slab while scrolling | ≤ 33 ms | 4–17 ms |
| 3D rotation | ≤ 33 ms | 3.9 ms (discrete GPU), 49 ms (integrated) |
| First image of a typical CT | ≤ 500 ms | 96–404 ms |
| Reopen of an indexed disc | ≤ 1 s | 0.17–0.37 s |

The integrated GPU misses the 3D target. Those are the cases where the per-app GPU preference matters.
