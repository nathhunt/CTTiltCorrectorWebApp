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
    /// Validates the username and password against AD via LDAP, then checks
    /// group membership if <see cref="AppConfig.AllowedAdGroups"/> is non-empty.
    /// Returns the fully qualified username (DOMAIN\username) on success.
    /// </summary>
    public (AuthResult Result, string? FullUserName) Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (AuthResult.InvalidCredentials, null);

        try
        {
            using var context = new PrincipalContext(
                ContextType.Domain,
                _cfg.LdapServer,
                _cfg.Domain);

            // Validate credentials
            bool credentialsValid = context.ValidateCredentials(username, password);
            if (!credentialsValid)
                return (AuthResult.InvalidCredentials, null);

            // Retrieve the user principal
            using var user = UserPrincipal.FindByIdentity(context, username);
            if (user is null)
                return (AuthResult.InvalidCredentials, null);

            if (!user.Enabled ?? false)
                return (AuthResult.AccountDisabled, null);

            var fullUserName = $"{_cfg.Domain}\\{username}";

            // Check group membership if groups are configured
            if (_cfg.AllowedAdGroups.Count > 0)
            {
                bool inGroup = _cfg.AllowedAdGroups.Any(groupName =>
                {
                    try
                    {
                        // Strip domain prefix if present (e.g. "HOSPITAL\GroupName" → "GroupName")
                        var bare = groupName.Contains('\\')
                            ? groupName.Split('\\', 2)[1]
                            : groupName;

                        using var group = GroupPrincipal.FindByIdentity(context, bare);
                        return group is not null && user.IsMemberOf(group);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check group membership for {Group}", groupName);
                        return false;
                    }
                });

                if (!inGroup)
                    return (AuthResult.NotInRequiredGroup, null);
            }

            _logger.LogInformation("Auth success: {User}", fullUserName);
            return (AuthResult.Success, fullUserName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP error during authentication for user {User}", username);
            return (AuthResult.LdapError, null);
        }
    }
}
