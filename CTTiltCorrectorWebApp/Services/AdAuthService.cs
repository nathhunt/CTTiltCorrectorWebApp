using CTTiltCorrector.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.DirectoryServices.AccountManagement;

namespace CTTiltCorrector.Services;

public enum AuthResult
{
    Success,
    InvalidCredentials,
    AccountDisabled,
    NotInRequiredGroup,
    LdapError
}

/// <summary>
/// Validates domain credentials and AD group membership via LDAP.
/// The host server does not need to be domain-joined.
/// When Domain, LdapServer, and AllowedAdGroups are all blank/empty,
/// the service bypasses LDAP and accepts any non-empty credentials.
/// </summary>
public class AdAuthService
{
    private readonly AppConfig _cfg;
    private readonly ILogger<AdAuthService> _logger;

    public AdAuthService(IOptions<AppConfig> cfg, ILogger<AdAuthService> logger)
    {
        _cfg = cfg.Value;
        _logger = logger;
    }

    /// <summary>
    /// True only when all three AD fields are populated.
    /// When false, Validate skips LDAP and accepts any non-empty credentials.
    /// </summary>
    public bool IsAdConfigured =>
        !string.IsNullOrWhiteSpace(_cfg.Domain) &&
        !string.IsNullOrWhiteSpace(_cfg.LdapServer) &&
        _cfg.AllowedAdGroups?.Count > 0;

    /// <summary>
    /// Validates the username and password against AD via LDAP, then checks
    /// group membership if <see cref="AppConfig.AllowedAdGroups"/> is non-empty.
    /// Returns the fully qualified username (DOMAIN\username) on success.
    /// When AD is not configured, accepts any non-empty credentials immediately.
    /// </summary>
    public (AuthResult Result, string? FullUserName, List<string> Groups) Validate(string username, string password)
    {
        var groups = new List<string>();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (AuthResult.InvalidCredentials, null, groups);

        // ── Dev/unconfigured bypass ───────────────────────────────────────────
        // When AD fields are blank, skip LDAP entirely. The authorization policy
        // in Program.cs already requires auth-only (no role) in this case.
        if (!IsAdConfigured)
        {
            _logger.LogWarning(
                "AD not configured — accepting credentials for {User} without LDAP validation.",
                username);
            return (AuthResult.Success, username, groups);
        }

        // ── Full AD path ──────────────────────────────────────────────────────
        try
        {
            using var context = new PrincipalContext(
                ContextType.Domain,
                _cfg.LdapServer,
                null,
                ContextOptions.Negotiate | ContextOptions.Signing | ContextOptions.Sealing);

            if (!context.ValidateCredentials(username, password))
                return (AuthResult.InvalidCredentials, null, groups);

            using var user = UserPrincipal.FindByIdentity(context, username);
            if (user == null) return (AuthResult.InvalidCredentials, null, groups);
            if (!user.Enabled ?? false) return (AuthResult.AccountDisabled, null, groups);

            foreach (var group in user.GetAuthorizationGroups())
            {
                if (!string.IsNullOrEmpty(group.Name))
                {
                    groups.Add(group.Name);
                    groups.Add($"{_cfg.Domain}\\{group.Name}");
                }
            }

            var fullUserName = $"{_cfg.Domain}\\{username}";
            return (AuthResult.Success, fullUserName, groups);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP error for user {User}", username);
            return (AuthResult.LdapError, null, groups);
        }
    }
}