using System;
using System.Collections.Generic;
using DicomTiltCorrector;
using FellowOakDicom;

namespace CTTiltCorrector.Corrector;

/// <summary>
/// Copies non-geometry DICOM tags from a reference slice dataset to an output
/// dataset. Geometry tags (IOP, IPP, spacing, size etc.) are excluded here and
/// written explicitly by <see cref="DicomSeriesWriter"/> after resampling.
/// </summary>
public static class DicomTagCopier
{
    // Tags whose values change after resampling — excluded from blind copy
    private static readonly HashSet<DicomTag> ExcludedTags = new HashSet<DicomTag>
    {
        DicomTag.ImageOrientationPatient,
        DicomTag.ImagePositionPatient,
        DicomTag.SliceLocation,
        DicomTag.SliceThickness,
        DicomTag.SpacingBetweenSlices,
        DicomTag.PixelSpacing,
        DicomTag.Rows,
        DicomTag.Columns,
        DicomTag.PixelData,
        DicomTag.NumberOfFrames,
        DicomTag.LargestImagePixelValue,
        DicomTag.SmallestImagePixelValue,
        DicomTag.SOPInstanceUID,
        DicomTag.InstanceNumber,
        DicomTag.SpecificCharacterSet,
        DicomTag.PatientPosition,
        DicomTag.SeriesDescription,
    };

    /// <summary>
    /// Copy all non-geometry, non-pixel tags from <paramref name="referenceDataset"/>
    /// to <paramref name="outputDataset"/>.
    /// </summary>
    public static void CopyNonGeometryTags(DicomDataset referenceDataset, DicomDataset outputDataset)
    {
        foreach (var item in referenceDataset)
        {
            var tag = item.Tag;

            if (tag == DicomTag.PixelData) continue;
            if (ExcludedTags.Contains(tag)) continue;
            if (tag.Element == 0x0000) continue; // obsolete group-length tags

            try
            {
                outputDataset.AddOrUpdate(item);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not copy tag {tag}: {ex.Message}");
            }
        }
    }
}