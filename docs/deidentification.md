# De-identification export

**Export de-identified copy…** (Ctrl+Shift+D) writes new DICOM files with identifying information removed or replaced. Originals are only read. The export applies to the copies alone and changes nothing in the app or in the source folders.

## Scope

You choose the active series, the active study, or all open studies. The dialog shows how many objects, images, non-image objects and images with possible burned-in text are in scope before anything is written.

The output folder must be empty and must lie outside every opened scan folder. It contains:

- `DICOM\STnn\SEnnn\IMnnnnn`: the exported files, named without patient or series details.
- `DICOMDIR`: a standard index so other viewers can open the folder.
- `deidentification-report.txt` and `.json`: profile, options, scope, counts, every object's status, and the limits listed below.

## Profile

The export implements the **Basic Application Level Confidentiality Profile** of DICOM PS3.15 Annex E. The attribute actions come from PS3.15 Table E.1-1, edition 2026d, which `tools/deid/generate_profile_table.py` converts into `ConfidentialityProfileTable.g.cs` (656 attributes, plus the private-attribute rule).

- **D, Z, X, U and K** are applied as defined. Combined actions (`X/Z`, `X/D`, `Z/D`, `X/Z/D`) keep an attribute, emptied or replaced with a dummy value, only when it is Type 1 or 2 in common image IODs, and remove it otherwise.
- `X/Z/U*` reference sequences (for example Source Image Sequence) are kept with their UIDs remapped, so references inside the export stay valid.
- **Nested sequences** are processed recursively with the same rules, so identifiers inside items are removed too.
- **Private attributes** (odd groups) are removed everywhere, including inside sequences.
- **Overlays and curves** (groups 60xx and 50xx data and comments) are removed.
- **UIDs** marked `U` are replaced with `2.25.` UIDs derived from a keyed hash with a random key per export. The same original UID maps to the same new UID across every file in one export, which keeps study, series and frame-of-reference relationships intact. Exports cannot be linked to each other or traced back to the originals.
- Patient Name and Patient ID take the replacement values from the dialog (default `ANONYMOUS` / `ANON0001`).
- `Patient Identity Removed` is set to `YES`, and De-identification Method plus De-identification Method Code Sequence (CID 7050) record the profile and options.

## Options

| Option | Standard option | Effect |
| --- | --- | --- |
| Remove dates and times | Basic Profile | Dates and times are removed, emptied or replaced with dummies |
| Shift dates by N days | Retain Longitudinal Temporal Information with Modified Dates | Every DA and DT value moves by the same number of days; times stay |
| Keep full dates | Retain Longitudinal Temporal Information with Full Dates | Dates are kept |
| Keep age, sex, height and weight | Retain Patient Characteristics | Needed to recompute SUV from the copies; selected by default when PET is in scope |
| Keep device identity | Retain Device Identity | Station name, serial numbers and similar |
| Keep institution | Retain Institution Identity | Institution name, address, department |
| Keep study and series descriptions | None (not part of the profile) | Descriptions are kept verbatim; the report warns that they can contain identifying text |

Retain UIDs, Retain Safe Private, Clean Descriptors, Clean Structured Content and Clean Graphics are not offered.

## Burned-in text

Removing attributes does not touch pixel data. An image is treated as possibly containing burned-in text when:

- Burned In Annotation is `YES`, or
- Burned In Annotation is absent and the object is a secondary capture, an ultrasound image, a text-prone modality (US, ES, XC, OT, SC, DOC, GM, SM, IVUS, OP, OPT), or a derived or screen-capture image.

For those images you choose one of three policies:

1. **Exclude** (default): they are not exported and are listed in the report.
2. **Export unchanged and flag**: they are exported and listed as not cleaned.
3. **Redact**: rectangles drawn with **Draw redaction region** on an image of the same size are blacked out in every frame. The pixel data is then rewritten uncompressed (colour as RGB). Images of a size with no rectangle are excluded.

Burned In Annotation is left as it was, because the app cannot prove that every piece of text was covered.

## Objects that are not exported

Objects without pixel data (structured reports, presentation states, key object selections, encapsulated documents) are excluded and listed. Their free text and references are not processed by this tool.

## Limits

- Tag removal does not guarantee anonymity. Rare or new attributes, free text kept on purpose, and burned-in text outside the rectangles can still identify a patient.
- Faces and other recognisable features in head CT or MR can be reconstructed in 3D; they are not removed.
- Removing all private attributes can drop vendor information that some software needs.
- Review the report and the exported images before sharing them.
