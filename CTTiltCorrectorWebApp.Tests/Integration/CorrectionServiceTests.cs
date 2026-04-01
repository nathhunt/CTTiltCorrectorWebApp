using CTTiltCorrector.Data;
using CTTiltCorrector.Infrastructure;
using CTTiltCorrector.Services;
using FellowOakDicom;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CTTiltCorrectorWebApp.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="CorrectionService.RunAsync"/> covering the
/// full in-memory pipeline.
///
/// Boundaries:
///   - IDicomQueryService is mocked  (replaces live PACS network calls)
///   - SendToAriaAsync is overridden (replaces live DICOM C-STORE to ARIA)
///   - ITiltCorrector is mocked      (replaces the ITK-bound algorithm)
///   - EF Core uses an in-memory DB
///   - InMemoryDicomStore, MonitorState — real instances
/// </summary>
public class CorrectionServiceTests : IDisposable
{
    private readonly string _tempLogDir;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly InMemoryDicomStore _store;
    private readonly MonitorState _monitor;
    private readonly Mock<IDicomQueryService> _queryMock;
    private readonly Mock<ITiltCorrector> _correctorMock;

    public CorrectionServiceTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), $"ct_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempLogDir);

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbFactory = new TestDbContextFactory(dbOptions);  // stored as IDbContextFactory<AppDbContext>

        _store   = new InMemoryDicomStore();
        _monitor = new MonitorState();
        _queryMock     = new Mock<IDicomQueryService>();
        _correctorMock = new Mock<ITiltCorrector>();

        // Default: no existing series in ARIA — conflict check is a no-op.
        // Override in individual tests to exercise conflict-resolution behaviour.
        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(int SeriesNumber, string SeriesDescription)>());
    }

    public void Dispose()
    {
        // Log files may still be held by Progress<T> thread-pool callbacks — best effort.
        try { if (Directory.Exists(_tempLogDir)) Directory.Delete(_tempLogDir, recursive: true); }
        catch (IOException) { }
    }

    // ── Builder helpers ───────────────────────────────────────────────────────

    private CorrectionService Build(
        bool skipWait = false,
        Func<CancellationToken, Task>? onSendAttempt = null) =>
        new TestCorrectionService(
            _queryMock.Object,
            _store,
            _correctorMock.Object,
            _dbFactory,
            _monitor,
            Options.Create(new DicomConfig()),
            Options.Create(new AppConfig { LogRootPath = _tempLogDir }),
            NullLogger<CorrectionService>.Instance,
            skipWait: skipWait,
            onSendAttempt: onSendAttempt);

    private static CorrectionJob MakeJob(
        string seriesUid     = "1.2.3",
        string patientId     = "P001",
        int    expectedSlices = 2) =>
        new(patientId, "study1", seriesUid, "alice", expectedSlices);

    /// <summary>
    /// Configures MoveSeriesAsync to populate the store immediately so
    /// WaitForStableDelivery exits on its very first count check (no poll delay).
    /// </summary>
    private void SetupMovePopulatesStore(string seriesUid, int sliceCount)
    {
        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, IProgress<string>? _, CancellationToken _) =>
            {
                for (int i = 1; i <= sliceCount; i++)
                {
                    var ds = new DicomDataset();
                    ds.Add(DicomTag.InstanceNumber, i);
                    ds.Add(DicomTag.SeriesInstanceUID, seriesUid);
                    _store.Add(seriesUid, ds);
                }
                return Task.CompletedTask;
            });
    }

    private async Task<CorrectionRun?> ReadRunAsync(string seriesUid)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.CorrectionRuns
            .FirstOrDefaultAsync(r => r.SeriesInstanceUid == seriesUid);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_HappyPath_DbStatusIsCompleted()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run.Should().NotBeNull();
        run!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task RunAsync_HappyPath_DbRecordContainsPatientAndUser()
    {
        var job = MakeJob(patientId: "XYZ999", expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.PatientId.Should().Be("XYZ999");
        run.UserName.Should().Be("alice");
    }

    [Fact]
    public async Task RunAsync_HappyPath_CorrectSliceCountPassedToCorrector()
    {
        var job = MakeJob(expectedSlices: 3);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 3);

        List<DicomDataset>? captured = null;
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .Callback<List<DicomDataset>, IProgress<string>, CancellationToken>(
                // ToList() snapshots the count before RunAsync calls slices.Clear()
                (slices, progress, ct) => captured = slices.ToList())
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        captured.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunAsync_HappyPath_SlicesArrivedSortedByInstanceNumber()
    {
        // Store is populated out of order; Drain must sort ascending before
        // handing off to the corrector.
        var job = MakeJob(expectedSlices: 3);
        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, IProgress<string>? _, CancellationToken _) =>
            {
                foreach (var instanceNum in new[] { 3, 1, 2 })
                {
                    var ds = new DicomDataset();
                    ds.Add(DicomTag.InstanceNumber, instanceNum);
                    _store.Add(job.SeriesInstanceUid, ds);
                }
                return Task.CompletedTask;
            });

        List<DicomDataset>? captured = null;
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .Callback<List<DicomDataset>, IProgress<string>, CancellationToken>(
                (slices, progress, ct) => captured = slices.ToList())
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        captured.Should().HaveCount(3);
        captured!
            .Select(ds => ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0))
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task RunAsync_HappyPath_MonitorIsRunningFalseAfterCompletion()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        _monitor.GetChannel("alice").IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_HappyPath_StoreExpectedBeforeMoveIsCalled()
    {
        // Verify that Expect() is registered on the store before MoveSeriesAsync
        // is called — otherwise the SCP would silently drop arriving slices.
        bool isReceivingAtMoveTime = false;
        var job = MakeJob(expectedSlices: 1);

        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, IProgress<string>? _, CancellationToken _) =>
            {
                isReceivingAtMoveTime = _store.IsReceiving;
                var ds = new DicomDataset();
                ds.Add(DicomTag.InstanceNumber, 1);
                _store.Add(job.SeriesInstanceUid, ds);
                return Task.CompletedTask;
            });

        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        isReceivingAtMoveTime.Should().BeTrue();
    }

    // ── Failure / cancellation paths ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CorrectorThrows_DbStatusIsFailed()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ITK resampling failed"));

        var sut = Build();
        await sut.Invoking(s => s.RunAsync(job, CancellationToken.None))
                 .Should().ThrowAsync<InvalidOperationException>();

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task RunAsync_CorrectorThrows_StoreIsDiscarded()
    {
        // Expect was called → if error, Discard should clean up so IsReceiving goes false.
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));

        var sut = Build();
        await sut.Invoking(s => s.RunAsync(job, CancellationToken.None))
                 .Should().ThrowAsync<InvalidOperationException>();

        _store.IsReceiving.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Cancellation_DbStatusIsCancelled()
    {
        // Simulate cancellation arriving at the MoveSeriesAsync boundary.
        var job = MakeJob(expectedSlices: 2);
        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated cancel"));

        await Build().RunAsync(job, CancellationToken.None);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task RunAsync_ZeroSlicesReceived_DbStatusIsFailed()
    {
        // WaitForStableDelivery is skipped (via TestCorrectionService.skipWait)
        // so Drain returns an empty list, triggering the zero-slice guard.
        var job = MakeJob(expectedSlices: 2);
        // MoveSeriesAsync returns successfully but adds nothing to the store.
        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = Build(skipWait: true);
        await sut.Invoking(s => s.RunAsync(job, CancellationToken.None))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*No DICOM slices*");

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task RunAsync_LogFileIsCreatedInLogRootPath()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        Directory.GetFiles(_tempLogDir, "*.log").Should().HaveCount(1);
    }

    // ── Fallback stability-window path ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_FallbackStabilityPath_DbStatusIsCompleted()
    {
        // expectedSlices = 0 triggers the stability-window fallback instead of
        // the deterministic count-match path.
        var job = MakeJob(expectedSlices: 0);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task RunAsync_DeterministicPath_WaitsForLateArrivingSlices()
    {
        // Two slices arrive immediately; the third is added by a background task
        // after 100 ms. The polling loop (50 ms interval) must wait and detect it.
        var job = MakeJob(expectedSlices: 3);

        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, IProgress<string>? _, CancellationToken _) =>
            {
                for (int i = 1; i <= 2; i++)
                {
                    var ds = new DicomDataset();
                    ds.Add(DicomTag.InstanceNumber, i);
                    _store.Add(job.SeriesInstanceUid, ds);
                }
                // Third slice arrives asynchronously after a short delay.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    var late = new DicomDataset();
                    late.Add(DicomTag.InstanceNumber, 3);
                    _store.Add(job.SeriesInstanceUid, late);
                });
                return Task.CompletedTask;
            });

        List<DicomDataset>? captured = null;
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .Callback<List<DicomDataset>, IProgress<string>, CancellationToken>(
                (slices, _, _) => captured = slices.ToList())
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        captured.Should().HaveCount(3);
    }

    // ── Upload retry logic ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_UploadRetry_SucceedsOnSecondAttempt_DbStatusIsCompleted()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        int callCount = 0;
        await Build(onSendAttempt: _ =>
        {
            if (++callCount == 1)
                throw new InvalidOperationException("simulated network failure");
            return Task.CompletedTask;
        }).RunAsync(job, CancellationToken.None);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Completed");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_UploadRetry_ExhaustsAllAttempts_DbStatusIsFailed()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        int callCount = 0;
        var sut = Build(onSendAttempt: _ =>
        {
            callCount++;
            throw new InvalidOperationException("persistent network failure");
        });

        await sut.Invoking(s => s.RunAsync(job, CancellationToken.None))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*3 attempts*");

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Failed");
        callCount.Should().Be(3);
    }

    // ── Cancellation at later pipeline stages ─────────────────────────────────

    [Fact]
    public async Task RunAsync_Cancellation_DuringWait_DbStatusIsCancelled()
    {
        // Move delivers only 1 slice; expected count is 3, so the polling loop
        // keeps waiting. Cancel after 250 ms (≈ 5 poll ticks at 50 ms each).
        var job = MakeJob(expectedSlices: 3);
        _queryMock
            .Setup(q => q.MoveSeriesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, IProgress<string>? _, CancellationToken _) =>
            {
                var ds = new DicomDataset();
                ds.Add(DicomTag.InstanceNumber, 1);
                _store.Add(job.SeriesInstanceUid, ds);
                return Task.CompletedTask;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Build().RunAsync(job, cts.Token);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task RunAsync_Cancellation_DuringCorrect_DbStatusIsCancelled()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // OperationCanceledException must be caught and NOT re-thrown.
        await Build().Invoking(s => s.RunAsync(job, CancellationToken.None))
                     .Should().NotThrowAsync();

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task RunAsync_Cancellation_DuringSend_DbStatusIsCancelled()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build(onSendAttempt: _ => throw new OperationCanceledException())
            .Invoking(s => s.RunAsync(job, CancellationToken.None))
            .Should().NotThrowAsync();

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Cancelled");
    }

    // ── Conflict resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ConflictResolution_NoConflicts_TagsUnchanged()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);

        var outputDs = new DicomDataset();
        outputDs.AddOrUpdate(DicomTag.SeriesNumber, "1005");
        outputDs.AddOrUpdate(DicomTag.SeriesDescription, "CT Chest-Rsmpld");
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DicomDataset> { outputDs });

        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (int, string)[] { (5, "CT Chest") });

        await Build().RunAsync(job, CancellationToken.None);

        outputDs.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty).Should().Be("1005");
        outputDs.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty).Should().Be("CT Chest-Rsmpld");
    }

    [Fact]
    public async Task RunAsync_ConflictResolution_SeriesNumberConflict_NumberIsIncremented()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);

        var outputDs = new DicomDataset();
        outputDs.AddOrUpdate(DicomTag.SeriesNumber, "1005");
        outputDs.AddOrUpdate(DicomTag.SeriesDescription, "CT Chest-Rsmpld");
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DicomDataset> { outputDs });

        // 1005 and 1006 both taken — resolved to 1007
        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (int, string)[] { (1005, "Other Series"), (1006, "Another") });

        await Build().RunAsync(job, CancellationToken.None);

        outputDs.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty).Should().Be("1007");
    }

    [Fact]
    public async Task RunAsync_ConflictResolution_DescriptionConflict_SuffixAppended()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);

        var outputDs = new DicomDataset();
        outputDs.AddOrUpdate(DicomTag.SeriesNumber, "1005");
        outputDs.AddOrUpdate(DicomTag.SeriesDescription, "CT Chest-Rsmpld");
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DicomDataset> { outputDs });

        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (int, string)[] { (999, "CT Chest-Rsmpld") });

        await Build().RunAsync(job, CancellationToken.None);

        outputDs.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty).Should().Be("CT Chest-Rsmpld(2)");
    }

    [Fact]
    public async Task RunAsync_ConflictResolution_DescriptionConflictChain_CounterIncrements()
    {
        // Both "(2)" and the base name are taken — should settle on "(3)".
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);

        var outputDs = new DicomDataset();
        outputDs.AddOrUpdate(DicomTag.SeriesNumber, "1005");
        outputDs.AddOrUpdate(DicomTag.SeriesDescription, "CT Chest-Rsmpld");
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DicomDataset> { outputDs });

        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (int, string)[]
            {
                (998, "CT Chest-Rsmpld"),
                (999, "CT Chest-Rsmpld(2)")
            });

        await Build().RunAsync(job, CancellationToken.None);

        outputDs.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty).Should().Be("CT Chest-Rsmpld(3)");
    }

    [Fact]
    public async Task RunAsync_ConflictResolution_BothConflict_BothResolved()
    {
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);

        var outputDs = new DicomDataset();
        outputDs.AddOrUpdate(DicomTag.SeriesNumber, "1005");
        outputDs.AddOrUpdate(DicomTag.SeriesDescription, "CT Chest-Rsmpld");
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DicomDataset> { outputDs });

        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (int, string)[] { (1005, "CT Chest-Rsmpld") });

        await Build().RunAsync(job, CancellationToken.None);

        outputDs.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty).Should().Be("1006");
        outputDs.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty).Should().Be("CT Chest-Rsmpld(2)");
    }

    [Fact]
    public async Task RunAsync_ConflictResolution_AllDatasetsUpdated()
    {
        // Verifies that the resolved values are applied to every slice, not just the first.
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);

        var outputSlices = Enumerable.Range(1, 3).Select(_ =>
        {
            var ds = new DicomDataset();
            ds.AddOrUpdate(DicomTag.SeriesNumber, "1005");
            ds.AddOrUpdate(DicomTag.SeriesDescription, "CT-Rsmpld");
            return ds;
        }).ToList();

        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(outputSlices);

        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (int, string)[] { (1005, "Other") });

        await Build().RunAsync(job, CancellationToken.None);

        outputSlices.Should().AllSatisfy(ds =>
            ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty).Should().Be("1006"));
    }

    [Fact]
    public async Task RunAsync_ConflictResolution_QueryReturnsEmpty_PipelineStillCompletes()
    {
        // If GetExistingSeriesMetadataAsync returns empty (e.g. query failed),
        // the pipeline must still finish successfully rather than throwing.
        var job = MakeJob(expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        _queryMock
            .Setup(q => q.GetExistingSeriesMetadataAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(int, string)>());

        await Build().RunAsync(job, CancellationToken.None);

        var run = await ReadRunAsync(job.SeriesInstanceUid);
        run!.Status.Should().Be("Completed");
    }

    // ── MonitorState ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_HappyPath_MonitorJobDescriptionContainsPatientId()
    {
        var job = MakeJob(patientId: "PAT777", expectedSlices: 2);
        SetupMovePopulatesStore(job.SeriesInstanceUid, 2);
        _correctorMock
            .Setup(c => c.CorrectAsync(It.IsAny<List<DicomDataset>>(),
                                       It.IsAny<IProgress<string>>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DicomDataset()]);

        await Build().RunAsync(job, CancellationToken.None);

        _monitor.GetChannel("alice").CurrentJobDescription.Should().Contain("PAT777");
    }
}

// ── Test subclass ─────────────────────────────────────────────────────────────

/// <summary>
/// Replaces the network-bound virtual methods so tests never open a socket.
///   SendAttemptAsync           → no-op by default, or delegates to onSendAttempt
///   WaitForStableDeliveryAsync → no-op when skipWait=true (tests the zero-slice guard)
///   PollInterval / StableWindow / RetryDelay → shortened so timing-sensitive tests
///                                              finish in milliseconds, not seconds
/// </summary>
file sealed class TestCorrectionService : CorrectionService
{
    private readonly bool _skipWait;
    private readonly Func<CancellationToken, Task>? _onSendAttempt;

    public TestCorrectionService(
        IDicomQueryService dicomQuery,
        InMemoryDicomStore store,
        ITiltCorrector corrector,
        IDbContextFactory<AppDbContext> dbFactory,
        MonitorState monitorState,
        IOptions<DicomConfig> dicomCfg,
        IOptions<AppConfig> appCfg,
        ILogger<CorrectionService> logger,
        bool skipWait = false,
        Func<CancellationToken, Task>? onSendAttempt = null)
        : base(dicomQuery, store, corrector, dbFactory, monitorState, dicomCfg, appCfg, logger)
    {
        _skipWait = skipWait;
        _onSendAttempt = onSendAttempt;
    }

    // Fast intervals so timing-sensitive tests complete in <500 ms.
    protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(50);
    protected override TimeSpan StableWindow  => TimeSpan.FromMilliseconds(200);
    protected override TimeSpan RetryDelay    => TimeSpan.Zero;

    protected override Task SendAttemptAsync(
        List<DicomDataset> datasets,
        IProgress<string> progress,
        CancellationToken ct) =>
        _onSendAttempt?.Invoke(ct) ?? Task.CompletedTask;

    protected override Task WaitForStableDeliveryAsync(
        string seriesUid,
        int expectedCount,
        IProgress<string> progress,
        CancellationToken ct) =>
        _skipWait ? Task.CompletedTask
                  : base.WaitForStableDeliveryAsync(seriesUid, expectedCount, progress, ct);
}

// ── In-memory EF context factory ─────────────────────────────────────────────

file sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
    public AppDbContext CreateDbContext() => new(_options);
}
