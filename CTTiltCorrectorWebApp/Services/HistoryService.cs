using CTTiltCorrector.Data;
using Microsoft.EntityFrameworkCore;

namespace CTTiltCorrector.Services;

public class HistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public HistoryService(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<List<CorrectionRun>> GetRunsAsync(
        string? patientIdFilter = null,
        int page = 1,
        int pageSize = 50)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.CorrectionRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(patientIdFilter))
            query = query.Where(r => r.PatientId.Contains(patientIdFilter));

        return await query
            .OrderByDescending(r => r.ExecutionDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> CountRunsAsync(string? patientIdFilter = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.CorrectionRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(patientIdFilter))
            query = query.Where(r => r.PatientId.Contains(patientIdFilter));

        return await query.CountAsync();
    }

    public async Task<string?> ReadLogAsync(int runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var run = await db.CorrectionRuns.FindAsync(runId);
        if (run is null || !File.Exists(run.LogFilePath)) return null;
        return await File.ReadAllTextAsync(run.LogFilePath);
    }
}
