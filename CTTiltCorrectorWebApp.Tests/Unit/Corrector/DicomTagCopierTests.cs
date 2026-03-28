using CTTiltCorrector.Corrector;
using FellowOakDicom;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class DicomTagCopierTests
{
    private static readonly IProgress<string> NoOp = new Progress<string>(_ => { });

    // ── Non-geometry tags are copied ──────────────────────────────────────────

    [Fact]
    public void CopyNonGeometryTags_PatientName_IsCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.PatientName, "Smith^John");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.PatientName).Should().BeTrue();
        dst.GetString(DicomTag.PatientName).Should().Be("Smith^John");
    }

    [Fact]
    public void CopyNonGeometryTags_StudyInstanceUID_IsCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.StudyInstanceUID, "1.2.840.99");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.GetString(DicomTag.StudyInstanceUID).Should().Be("1.2.840.99");
    }

    [Fact]
    public void CopyNonGeometryTags_MultipleNonGeometryTags_AllCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.PatientName, "Doe^Jane");
        src.Add(DicomTag.Modality, "CT");
        src.Add(DicomTag.StudyDate, "20240101");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.PatientName).Should().BeTrue();
        dst.Contains(DicomTag.Modality).Should().BeTrue();
        dst.Contains(DicomTag.StudyDate).Should().BeTrue();
    }

    // ── Geometry / excluded tags are NOT copied ───────────────────────────────

    [Fact]
    public void CopyNonGeometryTags_ImageOrientationPatient_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.ImageOrientationPatient, new double[] { 1, 0, 0, 0, 1, 0 });
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.ImageOrientationPatient).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_ImagePositionPatient_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.ImagePositionPatient, new double[] { 0, 0, 100 });
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.ImagePositionPatient).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_PixelData_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(new DicomOtherWord(DicomTag.PixelData, new ushort[] { 0, 1, 2 }));
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.PixelData).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_SOPInstanceUID_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.SOPInstanceUID, "1.2.3.4");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.SOPInstanceUID).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_PixelSpacing_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.PixelSpacing, new decimal[] { 0.5m, 0.5m });
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.PixelSpacing).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_Rows_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.Rows, (ushort)512);
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.Rows).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_Columns_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.Columns, (ushort)512);
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.Columns).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_PatientPosition_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.PatientPosition, "HFS");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.PatientPosition).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_SeriesDescription_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.SeriesDescription, "Axial Brain");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.SeriesDescription).Should().BeFalse();
    }

    [Fact]
    public void CopyNonGeometryTags_InstanceNumber_IsNotCopied()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.InstanceNumber, "42");
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Contains(DicomTag.InstanceNumber).Should().BeFalse();
    }

    // ── Empty source ──────────────────────────────────────────────────────────

    [Fact]
    public void CopyNonGeometryTags_EmptySource_DestinationRemainsEmpty()
    {
        var src = new DicomDataset();
        var dst = new DicomDataset();

        DicomTagCopier.CopyNonGeometryTags(src, dst, NoOp);

        dst.Should().BeEmpty();
    }

    // ── Progress reporting on error ───────────────────────────────────────────

    [Fact]
    public void CopyNonGeometryTags_ValidCopy_NoProgressWarningsEmitted()
    {
        var src = new DicomDataset();
        src.Add(DicomTag.PatientName, "Test^Patient");

        var messages = new List<string>();
        var progress = new Progress<string>(msg => messages.Add(msg));

        var dst = new DicomDataset();
        DicomTagCopier.CopyNonGeometryTags(src, dst, progress);

        messages.Should().NotContain(m => m.Contains("[WARN]"));
    }
}
