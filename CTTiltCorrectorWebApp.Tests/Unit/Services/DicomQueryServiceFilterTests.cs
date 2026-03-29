using CTTiltCorrector.Services;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

/// <summary>
/// Tests for the internal DicomQueryService.IsLikelyDiagnosticCt pre-filter,
/// which runs client-side before any image-level C-FIND to drop obviously
/// non-diagnostic series (4DCT, CBCT, respiratory gating) without a network call.
/// </summary>
public class DicomQueryServiceFilterTests
{
    private static DicomSeriesResult MakeSeries(string description) =>
        new(PatientId: "P001",
            PatientName: "Test Patient",
            PatientDob: "19800101",
            StudyInstanceUid: "1.2.3",
            SeriesInstanceUid: "1.2.3.4",
            SeriesDescription: description,
            Modality: "CT",
            SeriesDate: "20240101",
            NumberOfImages: 100);

    // ── Excluded series ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("4DCT Thorax")]
    [InlineData("4dct phase 0")]          // case-insensitive
    [InlineData("FREE BREATHING 4DCT")]
    public void Filter_FourDCT_Excluded(string description)
    {
        DicomQueryService.IsLikelyDiagnosticCt(MakeSeries(description)).Should().BeFalse();
    }

    [Theory]
    [InlineData("RESPIRATORY GATING")]
    [InlineData("Respiratory Motion")]    // case-insensitive
    [InlineData("4D respiratory")]
    public void Filter_Respiratory_Excluded(string description)
    {
        DicomQueryService.IsLikelyDiagnosticCt(MakeSeries(description)).Should().BeFalse();
    }

    [Theory]
    [InlineData("CBCT ON BOARD")]
    [InlineData("cbct")]                  // case-insensitive
    [InlineData("XVI CBCT Pelvis")]
    public void Filter_CBCT_Excluded(string description)
    {
        DicomQueryService.IsLikelyDiagnosticCt(MakeSeries(description)).Should().BeFalse();
    }

    // ── Included series ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("CT THORAX")]
    [InlineData("Contrast CT Chest Abd")]
    [InlineData("TOPOGRAM")]
    [InlineData("")]                      // no description — still considered diagnostic
    public void Filter_DiagnosticCt_Included(string description)
    {
        DicomQueryService.IsLikelyDiagnosticCt(MakeSeries(description)).Should().BeTrue();
    }
}
