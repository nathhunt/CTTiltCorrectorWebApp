using CTTiltCorrector.Corrector;
using FellowOakDicom;
using itk.simple;

namespace DicomTiltCorrector
{
    /// <summary>
    /// Writes the resampled SimpleITK volume back out as individual DICOM slice
    /// files, one file per Z index.
    ///
    /// Because the resampled volume has an identity direction matrix, every output
    /// slice has:
    ///   ImageOrientationPatient = 1\0\0\0\1\0   (HFS standard axial)
    ///   ImagePositionPatient    = (originX, originY, originZ + z * Sz)
    ///
    /// All other DICOM tags are copied from the nearest original reference slice
    /// (by SlicePosition, i.e. projection onto the tilt normal) and then the
    /// geometry tags are overwritten.
    /// </summary>
    public class DicomSeriesWriter
    {
        private readonly DicomSeriesLoader.SliceInfo[] _referenceSlices;
        private readonly string _correctedPatientPosition;
        private readonly IProgress<string> _progress;

        public DicomSeriesWriter(IProgress<string> progress,
                                List<DicomSeriesLoader.SliceInfo> referenceSlices,
                                 string correctedPatientPosition = "HFS")
        {
            _referenceSlices = referenceSlices.ToArray();
            _correctedPatientPosition = correctedPatientPosition;
            _progress = progress;
        }

        public List<DicomDataset> BuildDatasets(Image resampledVolume)
        {
            var results = new List<DicomDataset>();

            VectorDouble spacing = resampledVolume.GetSpacing();
            VectorUInt32 size = resampledVolume.GetSize();
            VectorDouble origin = resampledVolume.GetOrigin();
            VectorDouble dir = resampledVolume.GetDirection();

            int nx = (int)size[0];
            int ny = (int)size[1];
            int nz = (int)size[2];

            double Sx = spacing[0];
            double Sy = spacing[1];
            double Sz = spacing[2];

            DicomWriterHelpers.VerifyIdentityDirection(dir, _progress);

            _progress.Report($"[Writer] {nz} slices  |  {nx}x{ny} px  |  dz = {Sz:F4} mm");

            var ref0 = _referenceSlices[0].Dataset;

            ushort bitsAlloc = ref0.GetSingleValueOrDefault(DicomTag.BitsAllocated, (ushort)16);
            ushort bitsStore = ref0.GetSingleValueOrDefault(DicomTag.BitsStored, (ushort)16);
            ushort highBit = ref0.GetSingleValueOrDefault(DicomTag.HighBit, (ushort)15);
            ushort pixelRepr = ref0.GetSingleValueOrDefault(DicomTag.PixelRepresentation, (ushort)1);

            double rescaleSlope = ref0.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
            double rescaleIntercept = ref0.GetSingleValueOrDefault(DicomTag.RescaleIntercept, -1024.0);

            bool isSigned = (pixelRepr == 1);

            _progress.Report($"[Writer] RescaleSlope={rescaleSlope}, RescaleIntercept={rescaleIntercept}");

            Image storedVolume;
            if (Math.Abs(rescaleSlope - 1.0) < 1e-9 && Math.Abs(rescaleIntercept) < 1e-9)
            {
                _progress.Report("[Writer] Rescale is identity, skipping inverse transform.");
                storedVolume = SimpleITK.Cast(resampledVolume, PixelIDValueEnum.sitkFloat32);
            }
            else
            {
                _progress.Report("[Writer] Applying inverse rescale to recover stored pixel values.");
                var shiftScale = new ShiftScaleImageFilter();
                shiftScale.SetShift(-rescaleIntercept);
                shiftScale.SetScale(1.0 / rescaleSlope);
                storedVolume = shiftScale.Execute(resampledVolume);
            }

            PixelIDValueEnum targetPixelType = isSigned
                ? PixelIDValueEnum.sitkInt16
                : PixelIDValueEnum.sitkUInt16;

            Image castVolume = SimpleITK.Cast(storedVolume, targetPixelType);
            storedVolume.Dispose();

            double[] pixelSpacing = new double[] { Sy, Sx };
            int sliceByteCount = nx * ny * 2;

            double outZmin = origin[2];
            double outZmax = origin[2] + (nz - 1) * Sz;

            double inPosMin = _referenceSlices[0].SlicePosition;
            double inPosMax = _referenceSlices[_referenceSlices.Length - 1].SlicePosition;

            string newSeriesUID = DicomUID.Generate().UID;
            int originalSeriesNumber = ref0.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0);
            string newSeriesNumber = (originalSeriesNumber + 1000).ToString();

            _progress.Report($"[Writer] New SeriesInstanceUID: {newSeriesUID}");

            for (int z = 0; z < nz; z++)
            {
                // 1. Extract 2-D plane
                Image plane = SimpleITK.Extract(
                    castVolume,
                    new VectorUInt32(new uint[] { (uint)nx, (uint)ny, 0 }),
                    new VectorInt32(new int[] { 0, 0, z }));

                // 2. Copy pixel buffer
                byte[] pixelBytes = DicomWriterHelpers.CopyPixelBuffer(plane, sliceByteCount, isSigned);
                plane.Dispose();

                // 3. Compute IPP
                double ippX = origin[0];
                double ippY = origin[1];
                double ippZ = origin[2] + z * Sz;
                double[] ipp = new double[] { ippX, ippY, ippZ };

                // 4. Map to nearest reference slice
                double mappedPosition = (nz == 1)
                    ? inPosMin
                    : inPosMin + (ippZ - outZmin) / (outZmax - outZmin) * (inPosMax - inPosMin);

                var refSlice = DicomWriterHelpers.FindNearestReferenceSlice(mappedPosition, _referenceSlices);
                var refDs = refSlice.Dataset;

                // 5. Build output dataset
                var outDs = new DicomDataset();
                DicomTagCopier.CopyNonGeometryTags(refDs, outDs, _progress);

                // 6. Geometry tags
                outDs.AddOrUpdate(DicomTag.ImageOrientationPatient,
                    new double[] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 });
                outDs.AddOrUpdate(DicomTag.ImagePositionPatient, ipp);
                outDs.AddOrUpdate(DicomTag.SliceLocation, (decimal)ippZ);
                outDs.AddOrUpdate(DicomTag.SliceThickness, (decimal)Sz);
                outDs.AddOrUpdate(DicomTag.SpacingBetweenSlices, (decimal)Sz);
                outDs.AddOrUpdate(DicomTag.PixelSpacing,
                    new decimal[] { (decimal)pixelSpacing[0], (decimal)pixelSpacing[1] });
                outDs.AddOrUpdate(DicomTag.Rows, (ushort)ny);
                outDs.AddOrUpdate(DicomTag.Columns, (ushort)nx);
                outDs.AddOrUpdate(DicomTag.PatientPosition, _correctedPatientPosition);

                // 7. Series identity
                outDs.AddOrUpdate(DicomTag.SeriesInstanceUID, newSeriesUID);
                outDs.AddOrUpdate(DicomTag.SeriesNumber, newSeriesNumber);

                string desc = refDs.TryGetValue<string>(DicomTag.SeriesDescription, 0, out string value)
                    ? value.Trim() : string.Empty;
                string suffix = desc.Length > 0 ? "-Rsmpld" : "Rsmpld";
                string newDescription = desc.Substring(0, Math.Min(desc.Length, 64 - suffix.Length)) + suffix;
                outDs.AddOrUpdate(DicomTag.SeriesDescription, newDescription);

                // 8. Per-slice UIDs
                string newUID = DicomUID.Generate().UID;
                outDs.AddOrUpdate(DicomTag.SOPInstanceUID, newUID);
                outDs.AddOrUpdate(DicomTag.InstanceNumber, (z + 1).ToString());

                // 9. Pixel format
                outDs.AddOrUpdate(DicomTag.BitsAllocated, bitsAlloc);
                outDs.AddOrUpdate(DicomTag.BitsStored, bitsStore);
                outDs.AddOrUpdate(DicomTag.HighBit, highBit);
                outDs.AddOrUpdate(DicomTag.PixelRepresentation, pixelRepr);
                outDs.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);

                // 10. HU calibration
                outDs.AddOrUpdate(DicomTag.RescaleSlope, (decimal)rescaleSlope);
                outDs.AddOrUpdate(DicomTag.RescaleIntercept, (decimal)rescaleIntercept);
                outDs.AddOrUpdate(DicomTag.RescaleType, "HU");

                // 11. Pixel data
                ushort[] pixelWords = new ushort[pixelBytes.Length / 2];
                Buffer.BlockCopy(pixelBytes, 0, pixelWords, 0, pixelBytes.Length);
                outDs.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, pixelWords));

                // 12. File meta — still populate it so downstream C-STORE has what it needs,
                //     but we don't save to disk
                var outFile = new DicomFile(outDs);
                outFile.FileMetaInfo.TransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                outFile.FileMetaInfo.MediaStorageSOPInstanceUID =
                    new DicomUID(newUID, "SOP Instance", DicomUidType.SOPInstance);

                if (refDs.Contains(DicomTag.SOPClassUID))
                {
                    var sopClass = refDs.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
                    outFile.FileMetaInfo.MediaStorageSOPClassUID = sopClass;
                    outDs.AddOrUpdate(DicomTag.SOPClassUID, sopClass);
                }

                // 13. Add to results instead of saving to disk
                results.Add(outDs);

                if ((z + 1) % 25 == 0 || z == nz - 1)
                    _progress.Report($"  [{z + 1}/{nz}]  IPP-Z = {ippZ:F3} mm");
            }

            castVolume.Dispose();
            _progress.Report($"[Writer] Done. {nz} datasets built.");
            return results;
        }
    }
}