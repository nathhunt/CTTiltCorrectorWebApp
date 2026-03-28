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
    }

    public void Dispose()
    {
        // Log files may still be held by Progress<T> thread-pool callbacks — best effort.
        try { if (Directory.Exists(_tempLogDir)) Directory.Delete(_tempLogDir, recursive: true); }
        catch (IOException) { }
    }

    // ── Builder helpers ───────────────────────────────────────────────────────

    private CorrectionService Build(bool skipWait = false) => new TestCorrectionService(
        _queryMock.Object,
        _store,
        _correctorMock.Object,
        _dbFactory,
        _monitor,
        Options.Create(new DicomConfig()),
        Options.Create(new AppConfig { LogRootPath = _tempLogDir }),
        NullLogger<CorrectionService>.Instance,
        skipWait: skipWait);

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
}

// ── Test subclass ─────────────────────────────────────────────────────────────

/// <summary>
/// Replaces the two network-bound virtual methods so tests never open a socket.
///   SendToAriaAsync       → no-op (simulates successful ARIA upload)
///   WaitForStableDeliveryAsync → no-op when skipWait=true (tests the zero-slice guard)
/// </summary>
file sealed class TestCorrectionService : CorrectionService
{
    private readonly bool _skipWait;

    public TestCorrectionService(
        IDicomQueryService dicomQuery,
        InMemoryDicomStore store,
        ITiltCorrector corrector,
        IDbContextFactory<AppDbContext> dbFactory,
        MonitorState monitorState,
        IOptions<DicomConfig> dicomCfg,
        IOptions<AppConfig> appCfg,
        ILogger<CorrectionService> logger,
        bool skipWait = false)
        : base(dicomQuery, store, corrector, dbFactory, monitorState, dicomCfg, appCfg, logger)
    {
        _skipWait = skipWait;
    }

    protected override Task SendToAriaAsync(
        List<DicomDataset> datasets,
        IProgress<string> progress,
        CancellationToken ct) => Task.CompletedTask;

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
