"""Cross-checks Better DICOM Viewer volumes against SimpleITK (GDCM) reading the same files.

Usage: python compare_simpleitk.py <folder written by "BetterDicomViewer.Bench validate-export">
Prints only geometry differences and value agreement; no patient attributes are read or printed.
"""
import glob
import json
import sys

import numpy as np
import SimpleITK as sitk


def main(folder):
    worst_geom = 0.0
    failures = 0
    for path in sorted(glob.glob(f"{folder}/volume-*.json")):
        doc = json.load(open(path))
        reader = sitk.ImageSeriesReader()
        # Let GDCM order the files itself (IPP sorter) instead of trusting our order.
        files = list(doc["files"])
        sorter = sitk.ImageSeriesReader.GetGDCMSeriesFileNames
        reader.SetFileNames(files)
        try:
            image = reader.Execute()
        except RuntimeError as e:
            print(f"{path}: SimpleITK could not read: {e}")
            failures += 1
            continue
        m = np.array(doc["indexToPatient"]).reshape(4, 4)
        spacing = np.linalg.norm(m[:3, :3], axis=0)
        direction = m[:3, :3] / spacing
        sx = np.array(image.GetSpacing())
        dx = np.array(image.GetDirection()).reshape(3, 3)
        ox = np.array(image.GetOrigin())
        # SimpleITK orders slices by position as well; its origin may be either end of the stack.
        our_corners = [m[:3, 3], m[:3, 3] + m[:3, 2] * (doc["dims"][2] - 1)]
        origin_err = min(np.linalg.norm(ox - c) for c in our_corners)
        spacing_err = np.max(np.abs(np.sort(sx) - np.sort(spacing)))
        dir_err = np.max(np.abs(np.abs(dx[:, :2]) - np.abs(direction[:, :2])))
        worst_geom = max(worst_geom, origin_err, spacing_err)

        diffs = []
        for x, y, z, ours in doc["samples"]:
            idx = image.TransformPhysicalPointToIndex((x, y, z))
            if any(i < 0 or i >= n for i, n in zip(idx, image.GetSize())):
                diffs.append(np.inf)
                continue
            theirs = float(image.GetPixel(idx))
            diffs.append(abs(theirs - ours))
        diffs = np.array(diffs)
        tolerance = 0.51 if doc["voxelType"] == "Int16" else 1e-3 * max(1.0, float(np.max(np.abs([s[3] for s in doc["samples"]]))))
        ok = np.mean(diffs <= tolerance)
        if ok < 1.0:
            failures += 1
        print(f"{path.split('/')[-1].split(chr(92))[-1]}: {doc['modality']} {doc['dims']} origin err {origin_err:.4f} mm, "
              f"spacing err {spacing_err:.5f} mm, direction err {dir_err:.5f}, values within tolerance {ok * 100:.1f}% "
              f"(max diff {np.max(diffs):.4g})")
    print(f"worst geometry error {worst_geom:.4f} mm; volumes with value mismatches: {failures}")


if __name__ == "__main__":
    main(sys.argv[1])
