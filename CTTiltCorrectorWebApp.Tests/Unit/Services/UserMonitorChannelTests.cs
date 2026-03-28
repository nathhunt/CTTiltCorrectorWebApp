using CTTiltCorrector.Services;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

public class UserMonitorChannelTests
{
    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void IsRunning_Initially_IsFalse()
    {
        var channel = new UserMonitorChannel();
        channel.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Lines_Initially_IsEmpty()
    {
        var channel = new UserMonitorChannel();
        channel.Lines.Should().BeEmpty();
    }

    [Fact]
    public void CurrentJobDescription_Initially_IsNull()
    {
        var channel = new UserMonitorChannel();
        channel.CurrentJobDescription.Should().BeNull();
    }

    // ── SetJobStarted ─────────────────────────────────────────────────────────

    [Fact]
    public void SetJobStarted_SetsIsRunningTrue()
    {
        var channel = new UserMonitorChannel();
        channel.SetJobStarted("test job");
        channel.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void SetJobStarted_SetsCurrentJobDescription()
    {
        var channel = new UserMonitorChannel();
        channel.SetJobStarted("Patient XYZ");
        channel.CurrentJobDescription.Should().Be("Patient XYZ");
    }

    [Fact]
    public async Task SetJobStarted_ClearsExistingLines()
    {
        var channel = new UserMonitorChannel();
        var progress = channel.CreateProgressReporter();
        progress.Report("old line");
        await Task.Delay(50); // let Progress<T> fire

        channel.SetJobStarted("new job");

        channel.Lines.Should().BeEmpty();
    }

    // ── SetJobFinished ────────────────────────────────────────────────────────

    [Fact]
    public void SetJobFinished_SetsIsRunningFalse()
    {
        var channel = new UserMonitorChannel();
        channel.SetJobStarted("job");
        channel.SetJobFinished();
        channel.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void SetJobFinished_WhenNotStarted_DoesNotThrow()
    {
        var channel = new UserMonitorChannel();
        Action act = () => channel.SetJobFinished();
        act.Should().NotThrow();
    }

    // ── Progress reporter ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressReporter_ReportedMessage_AppearsInLines()
    {
        var channel = new UserMonitorChannel();
        var progress = channel.CreateProgressReporter();

        progress.Report("hello world");
        await Task.Delay(50);

        channel.Lines.Should().Contain("hello world");
    }

    [Fact]
    public async Task ProgressReporter_MultipleMessages_AllAppearInOrder()
    {
        var channel = new UserMonitorChannel();
        var progress = channel.CreateProgressReporter();

        progress.Report("msg1");
        progress.Report("msg2");
        progress.Report("msg3");
        await Task.Delay(50);

        channel.Lines.Should().ContainInOrder("msg1", "msg2", "msg3");
    }

    [Fact]
    public async Task Lines_AtMaxCapacity_OldestLineDropped()
    {
        var channel = new UserMonitorChannel();
        var progress = channel.CreateProgressReporter();

        // Add 500 lines (the cap) plus one more
        for (int i = 0; i < 501; i++)
            progress.Report($"line {i}");

        await Task.Delay(200); // give the thread pool time to process all 501

        channel.Lines.Count.Should().BeLessThanOrEqualTo(500);
        channel.Lines.Should().NotContain("line 0"); // oldest should be dropped
    }

    // ── Subscribe / Unsubscribe ───────────────────────────────────────────────

    [Fact]
    public void Subscribe_CalledSynchronously_WhenSetJobStarted()
    {
        var channel = new UserMonitorChannel();
        int callCount = 0;
        channel.Subscribe(() => callCount++);

        channel.SetJobStarted("job");

        callCount.Should().Be(1);
    }

    [Fact]
    public void Subscribe_CalledSynchronously_WhenSetJobFinished()
    {
        var channel = new UserMonitorChannel();
        int callCount = 0;
        channel.Subscribe(() => callCount++);

        channel.SetJobFinished();

        callCount.Should().Be(1);
    }

    [Fact]
    public void Subscribe_MultipleSubscribers_AllNotified()
    {
        var channel = new UserMonitorChannel();
        int a = 0, b = 0;
        channel.Subscribe(() => a++);
        channel.Subscribe(() => b++);

        channel.SetJobStarted("job");

        a.Should().Be(1);
        b.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_StopsNotifications()
    {
        var channel = new UserMonitorChannel();
        int callCount = 0;
        Action handler = () => callCount++;
        channel.Subscribe(handler);
        channel.Unsubscribe(handler);

        channel.SetJobStarted("job");

        callCount.Should().Be(0);
    }

    [Fact]
    public void SubscriberException_DoesNotPropagate()
    {
        var channel = new UserMonitorChannel();
        channel.Subscribe(() => throw new InvalidOperationException("subscriber boom"));

        Action act = () => channel.SetJobStarted("job");

        act.Should().NotThrow();
    }
}
