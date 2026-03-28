using CTTiltCorrector.Data;
using CTTiltCorrector.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CTTiltCorrectorWebApp.Tests.Integration;

/// <summary>
/// Integration tests for HistoryService using a real SQLite file on disk,
/// exercising the full EF Core stack including migrations.
/// </summary>
public class HistoryServiceIntegrationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private AppDbContext _db = null!;
    private HistoryService _sut = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ct_tests_{Guid.NewGuid()}.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _sut = new HistoryService(new SqliteTestDbContextFactory(options));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        // SQLite pools connections on Windows; clear the pool so the file lock is released
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task RoundTrip_InsertAndRetrieve_MatchesOriginal()
    {
        _db.CorrectionRuns.Add(new CorrectionRun
        {
            PatientId        = "INTEGRATION_TEST",
            SeriesInstanceUid = "1.2.3.4.5",
            UserName         = "DOMAIN\\tester",
            ExecutionDate    = new DateTime(2025, 6, 1, 12, 0, 0),
            Status           = "Completed",
            LogFilePath      = string.Empty
        });
        await _db.SaveChangesAsync();

        var runs = await _sut.GetRunsAsync("INTEGRATION_TEST");

        runs.Should().ContainSingle()
            .Which.SeriesInstanceUid.Should().Be("1.2.3.4.5");
    }

    [Fact]
    public async Task CountRunsAsync_AfterBulkInsert_ReturnsCorrectTotal()
    {
        for (int i = 0; i < 25; i++)
        {
            _db.CorrectionRuns.Add(new CorrectionRun
            {
                PatientId = $"BULK_{i:00}",
                SeriesInstanceUid = Guid.NewGuid().ToString(),
                UserName = "tester",
                Status = "Completed",
                LogFilePath = string.Empty
            });
        }
        await _db.SaveChangesAsync();

        var count = await _sut.CountRunsAsync();

        count.Should().Be(25);
    }
}

file sealed class SqliteTestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    public SqliteTestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
    public AppDbContext CreateDbContext() => new(_options);
}
