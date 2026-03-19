using Microsoft.EntityFrameworkCore;

namespace CTTiltCorrector.Data;

// ─── Entity ──────────────────────────────────────────────────────────────────

public class CorrectionRun
{
    public int Id { get; set; }

    /// <summary>Patient MRN / ID queried in ARIA.</summary>
    public string PatientId { get; set; } = string.Empty;

    /// <summary>DICOM Series Instance UID that was corrected.</summary>
    public string SeriesInstanceUid { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the job started.</summary>
    public DateTime ExecutionDate { get; set; } = DateTime.UtcNow;

    /// <summary>Windows domain username (e.g. DOMAIN\jsmith).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Absolute path to the saved text log file for this run.</summary>
    public string LogFilePath { get; set; } = string.Empty;

    /// <summary>Final status: Completed | Failed | Cancelled</summary>
    public string Status { get; set; } = "Pending";
}

// ─── DbContext ───────────────────────────────────────────────────────────────

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<CorrectionRun> CorrectionRuns => Set<CorrectionRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CorrectionRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PatientId).IsRequired().HasMaxLength(64);
            e.Property(x => x.SeriesInstanceUid).IsRequired().HasMaxLength(128);
            e.Property(x => x.UserName).IsRequired().HasMaxLength(128);
            e.Property(x => x.LogFilePath).HasMaxLength(512);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasIndex(x => x.PatientId);
            e.HasIndex(x => x.ExecutionDate);
        });
    }
}
