using CTTiltCorrector.Services;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

public class CorrectionJobQueueTests
{
    private static CorrectionJob MakeJob(string seriesUid = "series1") =>
        new("P001", "study1", seriesUid, "user1", 10);

    // ── EnqueueAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_NewSeries_ReturnsTrue()
    {
        var queue = new CorrectionJobQueue();

        var result = await queue.EnqueueAsync(MakeJob("series1"));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateSeries_ReturnsFalse()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));

        var result = await queue.EnqueueAsync(MakeJob("series1"));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EnqueueAsync_DifferentSeries_BothReturnTrue()
    {
        var queue = new CorrectionJobQueue();

        var r1 = await queue.EnqueueAsync(MakeJob("series1"));
        var r2 = await queue.EnqueueAsync(MakeJob("series2"));

        r1.Should().BeTrue();
        r2.Should().BeTrue();
    }

    // ── IsActive ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsActive_BeforeEnqueue_ReturnsFalse()
    {
        var queue = new CorrectionJobQueue();

        queue.IsActive("series1").Should().BeFalse();
    }

    [Fact]
    public async Task IsActive_AfterEnqueue_ReturnsTrue()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));

        queue.IsActive("series1").Should().BeTrue();
    }

    [Fact]
    public async Task IsActive_AfterMarkComplete_ReturnsFalse()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));

        queue.MarkComplete("series1");

        queue.IsActive("series1").Should().BeFalse();
    }

    [Fact]
    public async Task IsActive_OneSeriesCompleted_OtherStillActive()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));
        await queue.EnqueueAsync(MakeJob("series2"));

        queue.MarkComplete("series1");

        queue.IsActive("series2").Should().BeTrue();
    }

    // ── MarkComplete ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkComplete_FiresJobCompletedEvent_WithCorrectUid()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));

        string? receivedUid = null;
        queue.JobCompleted += uid => receivedUid = uid;

        queue.MarkComplete("series1");

        receivedUid.Should().Be("series1");
    }

    [Fact]
    public async Task MarkComplete_AllowsReEnqueue_AfterCompletion()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));
        queue.MarkComplete("series1");

        var result = await queue.EnqueueAsync(MakeJob("series1"));

        result.Should().BeTrue();
    }

    [Fact]
    public void MarkComplete_UnknownSeries_DoesNotThrow()
    {
        var queue = new CorrectionJobQueue();

        Action act = () => queue.MarkComplete("nonexistent");

        act.Should().NotThrow();
    }

    // ── Reader ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reader_IsNotNull()
    {
        var queue = new CorrectionJobQueue();
        queue.Reader.Should().NotBeNull();
    }

    [Fact]
    public async Task Reader_AfterEnqueue_HasItem()
    {
        var queue = new CorrectionJobQueue();
        await queue.EnqueueAsync(MakeJob("series1"));

        queue.Reader.TryRead(out var item).Should().BeTrue();
        item.Job.SeriesInstanceUid.Should().Be("series1");
    }
}
