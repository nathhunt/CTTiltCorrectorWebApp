using CTTiltCorrector.Services;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

public class MonitorStateTests
{
    // ── GetChannel ────────────────────────────────────────────────────────────

    [Fact]
    public void GetChannel_SameUser_ReturnsSameInstance()
    {
        var state = new MonitorState();

        var ch1 = state.GetChannel("alice");
        var ch2 = state.GetChannel("alice");

        ch1.Should().BeSameAs(ch2);
    }

    [Fact]
    public void GetChannel_DifferentUsers_ReturnsDifferentInstances()
    {
        var state = new MonitorState();

        var alice = state.GetChannel("alice");
        var bob   = state.GetChannel("bob");

        alice.Should().NotBeSameAs(bob);
    }

    [Fact]
    public void GetChannel_NewUser_ReturnsNewChannel()
    {
        var state = new MonitorState();

        var channel = state.GetChannel("carol");

        channel.Should().NotBeNull();
        channel.IsRunning.Should().BeFalse();
    }

    // ── SetJobStarted / SetJobFinished ────────────────────────────────────────

    [Fact]
    public void SetJobStarted_SetsChannelIsRunningTrue()
    {
        var state = new MonitorState();

        state.SetJobStarted("alice", "Resampling series 1.2.3");

        state.GetChannel("alice").IsRunning.Should().BeTrue();
    }

    [Fact]
    public void SetJobStarted_SetsCurrentJobDescription()
    {
        var state = new MonitorState();

        state.SetJobStarted("alice", "My Job");

        state.GetChannel("alice").CurrentJobDescription.Should().Be("My Job");
    }

    [Fact]
    public void SetJobFinished_SetsChannelIsRunningFalse()
    {
        var state = new MonitorState();
        state.SetJobStarted("alice", "job");

        state.SetJobFinished("alice");

        state.GetChannel("alice").IsRunning.Should().BeFalse();
    }

    [Fact]
    public void SetJobStarted_OneUser_DoesNotAffectAnother()
    {
        var state = new MonitorState();

        state.SetJobStarted("alice", "alice's job");

        state.GetChannel("bob").IsRunning.Should().BeFalse();
    }

    // ── CreateProgressReporter ────────────────────────────────────────────────

    [Fact]
    public async Task CreateProgressReporter_ReportedMessage_AppearsInUserChannel()
    {
        var state = new MonitorState();
        var progress = state.CreateProgressReporter("alice");

        progress.Report("step 1 done");
        await Task.Delay(50);

        state.GetChannel("alice").Lines.Should().Contain("step 1 done");
    }

    [Fact]
    public async Task CreateProgressReporter_MessageForOneUser_DoesNotAppearForAnother()
    {
        var state = new MonitorState();
        var progress = state.CreateProgressReporter("alice");

        progress.Report("alice's message");
        await Task.Delay(50);

        state.GetChannel("bob").Lines.Should().NotContain("alice's message");
    }
}
