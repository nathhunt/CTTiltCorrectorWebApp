using CTTiltCorrector.Data;
using CTTiltCorrector.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

/// <summary>
/// Unit tests for HistoryService using an in-memory SQLite-compatible
/// EF Core provider.  Each test gets its own isolated database instance.
/// </summary>
public class HistoryServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly HistoryService _sut;

    public HistoryServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var factory = new TestDbContextFactory(options);
        _sut = new HistoryService(factory);
    }

    public void Dispose() => _db.Dispose();

    // ── GetRunsAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunsAsync_NoFilter_ReturnsAllRuns()
    {
        SeedRuns(3);

        var result = await _sut.GetRunsAsync();

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetRunsAsync_WithPatientIdFilter_ReturnsOnlyMatching()
    {
        _db.CorrectionRuns.AddRange(
            MakeRun("P001"),
            MakeRun("P002"),
            MakeRun("P001-extra"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetRunsAsync(patientIdFilter: "P001");

        result.Should().HaveCount(2).And.OnlyContain(r => r.PatientId.Contains("P001"));
    }

    [Fact]
    public async Task GetRunsAsync_Paging_RespectsPageSize()
    {
        SeedRuns(10);

        var page1 = await _sut.GetRunsAsync(page: 1, pageSize: 3);
        var page2 = await _sut.GetRunsAsync(page: 2, pageSize: 3);

        page1.Should().HaveCount(3);
        page2.Should().HaveCount(3);
        page1.Select(r => r.Id).Should().NotIntersectWith(page2.Select(r => r.Id));
    }

    [Fact]
    public async Task GetRunsAsync_ReturnsRunsOrderedByDateDescending()
    {
        _db.CorrectionRuns.AddRange(
            MakeRun("A", DateTime.Now.AddDays(-2)),
            MakeRun("B", DateTime.Now.AddDays(-1)),
            MakeRun("C", DateTime.Now));
        await _db.SaveChangesAsync();

        var result = await _sut.GetRunsAsync();

        result.Should().BeInDescendingOrder(r => r.ExecutionDate);
    }

    // ── CountRunsAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CountRunsAsync_NoFilter_ReturnsTotal()
    {
        SeedRuns(5);

        var count = await _sut.CountRunsAsync();

        count.Should().Be(5);
    }

    [Fact]
    public async Task CountRunsAsync_WithFilter_ReturnsFilteredCount()
    {
        _db.CorrectionRuns.AddRange(MakeRun("P001"), MakeRun("P002"), MakeRun("P001"));
        await _db.SaveChangesAsync();

        var count = await _sut.CountRunsAsync("P001");

        count.Should().Be(2);
    }

    // ── ReadLogAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadLogAsync_RunNotFound_ReturnsNull()
    {
        var result = await _sut.ReadLogAsync(99999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadLogAsync_RunExistsButLogMissing_ReturnsNull()
    {
        var run = MakeRun("P001");
        run.LogFilePath = "/nonexistent/path.log";
        _db.CorrectionRuns.Add(run);
        await _db.SaveChangesAsync();
        var id = _db.CorrectionRuns.First().Id;

        var result = await _sut.ReadLogAsync(id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadLogAsync_ValidLogFile_ReturnsContent()
    {
        var logPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(logPath, "Test log content");

        var run = MakeRun("P001");
        run.LogFilePath = logPath;
        _db.CorrectionRuns.Add(run);
        await _db.SaveChangesAsync();
        var id = _db.CorrectionRuns.First().Id;

        try
        {
            var result = await _sut.ReadLogAsync(id);
            result.Should().Be("Test log content");
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SeedRuns(int count)
    {
        for (int i = 0; i < count; i++)
            _db.CorrectionRuns.Add(MakeRun($"P{i:000}"));
        _db.SaveChanges();
    }

    private static CorrectionRun MakeRun(string patientId, DateTime? date = null) => new()
    {
        PatientId = patientId,
        SeriesInstanceUid = Guid.NewGuid().ToString(),
        UserName = "DOMAIN\\testuser",
        ExecutionDate = date ?? DateTime.Now,
        Status = "Completed",
        LogFilePath = string.Empty
    };
}

/// <summary>
/// Minimal IDbContextFactory&lt;AppDbContext&gt; backed by an already-configured
/// DbContextOptions, so tests don't need the full DI container.
/// </summary>
file sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
    public AppDbContext CreateDbContext() => new(_options);
}
