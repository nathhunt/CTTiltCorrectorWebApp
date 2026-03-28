using CTTiltCorrector.Data;
using CTTiltCorrector.Services;
using CTTiltCorrector.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using CTTiltCorrector.Corrector;

var builder = WebApplication.CreateBuilder(args);

// ─── MudBlazor ───────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

// ─── Blazor Server ───────────────────────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// ─── Windows / Active Directory Authentication ───────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });

var allowedGroups = builder.Configuration
    .GetSection("App:AllowedAdGroups")
    .Get<List<string>>() ?? new List<string>();

builder.Services.AddAuthorization(options =>
{
    if (allowedGroups.Count > 0)
    {
        // User must be authenticated AND belong to at least one of the listed groups
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole(allowedGroups.ToArray())
            .Build();
    }
    else
    {
        // No groups configured — require authentication only (useful during development)
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
});

// ─── Configuration ───────────────────────────────────────────────────────────
builder.Services.Configure<DicomConfig>(
    builder.Configuration.GetSection(DicomConfig.SectionName));

builder.Services.Configure<AppConfig>(
    builder.Configuration.GetSection(AppConfig.SectionName));

// ─── SQLite / EF Core ────────────────────────────────────────────────────────
var dbPath = builder.Configuration["App:DatabasePath"] ?? "data/cttiltcorrector.db";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ─── Background Processing Queue (single-threaded via Channel) ───────────────
builder.Services.AddSingleton<CorrectionJobQueue>();
builder.Services.AddHostedService<CorrectionJobProcessor>();

// ─── In-memory DICOM slice store (shared between SCP listener and pipeline) ───
// Single instance holds all in-flight datasets; no disk I/O.
builder.Services.AddSingleton<InMemoryDicomStore>();

// ─── DICOM C-STORE SCP Listener (HostedService) ──────────────────────────────
builder.Services.AddHostedService<DicomStoreScp>();

// ─── Tilt corrector — swap YourTiltCorrector for your concrete implementation ─
// Your class must implement ITiltCorrector:
//   Task<List<DicomDataset>> CorrectAsync(List<DicomDataset>, IProgress<string>, CancellationToken)
builder.Services.AddScoped<ITiltCorrector, TiltCorrector>();

// ─── Application Services ────────────────────────────────────────────────────
builder.Services.AddScoped<IDicomQueryService, DicomQueryService>();
builder.Services.AddScoped<CorrectionService>();
builder.Services.AddScoped<HistoryService>();
builder.Services.AddScoped<AdAuthService>();
builder.Services.AddSingleton<MonitorState>();

// ─── HttpContextAccessor (for resolving current Windows user in Blazor) ───────
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ─── Auto-create database schema on startup ───────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // EnsureCreated creates the schema from the model if the DB doesn't exist.
    // When you're ready for production, switch to db.Database.Migrate() and
    // generate migrations with: dotnet ef migrations add InitialCreate
    db.Database.EnsureCreated();
}


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapGet("/", () => Results.Redirect("/search"));

app.Run();
