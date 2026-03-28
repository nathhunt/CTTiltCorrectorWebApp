using CTTiltCorrector.Services;
using FellowOakDicom;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

public class InMemoryDicomStoreTests
{
    private static DicomDataset MakeDataset(int instanceNumber = 1)
    {
        var ds = new DicomDataset();
        ds.Add(DicomTag.InstanceNumber, instanceNumber);
        return ds;
    }

    // ── IsReceiving ───────────────────────────────────────────────────────────

    [Fact]
    public void IsReceiving_Initially_IsFalse()
    {
        var store = new InMemoryDicomStore();
        store.IsReceiving.Should().BeFalse();
    }

    [Fact]
    public void IsReceiving_AfterExpect_IsTrue()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.IsReceiving.Should().BeTrue();
    }

    [Fact]
    public void IsReceiving_AfterDrain_IsFalse()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Drain("series1");
        store.IsReceiving.Should().BeFalse();
    }

    [Fact]
    public void IsReceiving_AfterDiscard_IsFalse()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Discard("series1");
        store.IsReceiving.Should().BeFalse();
    }

    // ── Add ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Add_WithoutExpect_ReturnsFalse()
    {
        var store = new InMemoryDicomStore();

        var result = store.Add("series1", MakeDataset());

        result.Should().BeFalse();
    }

    [Fact]
    public void Add_AfterExpect_ReturnsTrue()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");

        var result = store.Add("series1", MakeDataset());

        result.Should().BeTrue();
    }

    [Fact]
    public void Add_ForDifferentSeries_ReturnsFalse()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");

        var result = store.Add("series2", MakeDataset());

        result.Should().BeFalse();
    }

    [Fact]
    public void Add_AfterDrain_ReturnsFalse()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Drain("series1");

        var result = store.Add("series1", MakeDataset());

        result.Should().BeFalse();
    }

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Count_BeforeExpect_IsZero()
    {
        var store = new InMemoryDicomStore();
        store.Count("series1").Should().Be(0);
    }

    [Fact]
    public void Count_AfterExpectNoAdd_IsZero()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Count("series1").Should().Be(0);
    }

    [Fact]
    public void Count_AfterOneAdd_IsOne()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Add("series1", MakeDataset());

        store.Count("series1").Should().Be(1);
    }

    [Fact]
    public void Count_AfterMultipleAdds_IsCorrect()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Add("series1", MakeDataset(1));
        store.Add("series1", MakeDataset(2));
        store.Add("series1", MakeDataset(3));

        store.Count("series1").Should().Be(3);
    }

    [Fact]
    public void Count_TwoSeries_AreIndependent()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Expect("series2");
        store.Add("series1", MakeDataset(1));
        store.Add("series1", MakeDataset(2));
        store.Add("series2", MakeDataset(1));

        store.Count("series1").Should().Be(2);
        store.Count("series2").Should().Be(1);
    }

    // ── Drain ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Drain_UnknownSeries_ReturnsEmptyList()
    {
        var store = new InMemoryDicomStore();

        var result = store.Drain("series1");

        result.Should().BeEmpty();
    }

    [Fact]
    public void Drain_ReturnsAllAddedDatasets()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        var ds1 = MakeDataset(1);
        var ds2 = MakeDataset(2);
        store.Add("series1", ds1);
        store.Add("series1", ds2);

        var result = store.Drain("series1");

        result.Should().HaveCount(2);
    }

    [Fact]
    public void Drain_SortsDatasetsByInstanceNumber()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Add("series1", MakeDataset(3));
        store.Add("series1", MakeDataset(1));
        store.Add("series1", MakeDataset(2));

        var result = store.Drain("series1");

        result.Select(ds => ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0))
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public void Drain_AfterDrain_CountIsZero()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Add("series1", MakeDataset(1));

        store.Drain("series1");

        store.Count("series1").Should().Be(0);
    }

    [Fact]
    public void Drain_UnregistersSeriesFromExpected()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Drain("series1");

        // After drain the series is no longer expected — Add should return false
        store.Add("series1", MakeDataset()).Should().BeFalse();
    }

    // ── Discard ───────────────────────────────────────────────────────────────

    [Fact]
    public void Discard_ClearsBufferedDatasets()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Add("series1", MakeDataset(1));
        store.Add("series1", MakeDataset(2));

        store.Discard("series1");

        store.Count("series1").Should().Be(0);
    }

    [Fact]
    public void Discard_UnregistersSeriesFromExpected()
    {
        var store = new InMemoryDicomStore();
        store.Expect("series1");
        store.Discard("series1");

        store.Add("series1", MakeDataset()).Should().BeFalse();
    }

    [Fact]
    public void Discard_UnknownSeries_DoesNotThrow()
    {
        var store = new InMemoryDicomStore();
        Action act = () => store.Discard("nonexistent");
        act.Should().NotThrow();
    }
}
