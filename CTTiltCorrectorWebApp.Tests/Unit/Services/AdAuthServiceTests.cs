using CTTiltCorrector.Infrastructure;
using CTTiltCorrector.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CTTiltCorrectorWebApp.Tests.Unit.Services;

/// <summary>
/// Tests the parts of AdAuthService that do not require a live domain controller:
///   - IsAdConfigured property
///   - Input validation (empty username / password)
///   - Dev bypass (all AD fields blank → accept any non-empty credentials)
///
/// The full LDAP path (PrincipalContext) is integration-test territory.
/// </summary>
public class AdAuthServiceTests
{
    private static AdAuthService Build(AppConfig cfg)
        => new(Options.Create(cfg), NullLogger<AdAuthService>.Instance);

    // ── IsAdConfigured ────────────────────────────────────────────────────────

    [Fact]
    public void IsAdConfigured_AllFieldsSet_IsTrue()
    {
        var sut = Build(new AppConfig
        {
            Domain = "HOSPITAL",
            LdapServer = "192.168.1.10",
            AllowedAdGroups = ["CTUsers"]
        });

        sut.IsAdConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsAdConfigured_DomainBlank_IsFalse()
    {
        var sut = Build(new AppConfig
        {
            Domain = "",
            LdapServer = "192.168.1.10",
            AllowedAdGroups = ["CTUsers"]
        });

        sut.IsAdConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsAdConfigured_LdapServerBlank_IsFalse()
    {
        var sut = Build(new AppConfig
        {
            Domain = "HOSPITAL",
            LdapServer = "",
            AllowedAdGroups = ["CTUsers"]
        });

        sut.IsAdConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsAdConfigured_AllowedGroupsEmpty_IsFalse()
    {
        var sut = Build(new AppConfig
        {
            Domain = "HOSPITAL",
            LdapServer = "192.168.1.10",
            AllowedAdGroups = []
        });

        sut.IsAdConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsAdConfigured_AllowedGroupsNull_IsFalse()
    {
        var sut = Build(new AppConfig
        {
            Domain = "HOSPITAL",
            LdapServer = "192.168.1.10",
            AllowedAdGroups = null!
        });

        sut.IsAdConfigured.Should().BeFalse();
    }

    // ── Input validation (applies regardless of AD config) ───────────────────

    [Fact]
    public void Validate_EmptyUsername_ReturnsInvalidCredentials()
    {
        var sut = Build(new AppConfig()); // AD not configured

        var (result, _, _) = sut.Validate("", "password");

        result.Should().Be(AuthResult.InvalidCredentials);
    }

    [Fact]
    public void Validate_WhitespaceUsername_ReturnsInvalidCredentials()
    {
        var sut = Build(new AppConfig());

        var (result, _, _) = sut.Validate("   ", "password");

        result.Should().Be(AuthResult.InvalidCredentials);
    }

    [Fact]
    public void Validate_EmptyPassword_ReturnsInvalidCredentials()
    {
        var sut = Build(new AppConfig());

        var (result, _, _) = sut.Validate("user", "");

        result.Should().Be(AuthResult.InvalidCredentials);
    }

    [Fact]
    public void Validate_WhitespacePassword_ReturnsInvalidCredentials()
    {
        var sut = Build(new AppConfig());

        var (result, _, _) = sut.Validate("user", "   ");

        result.Should().Be(AuthResult.InvalidCredentials);
    }

    [Fact]
    public void Validate_BothEmpty_ReturnsInvalidCredentials()
    {
        var sut = Build(new AppConfig());

        var (result, _, _) = sut.Validate("", "");

        result.Should().Be(AuthResult.InvalidCredentials);
    }

    // ── Dev bypass (AD not configured) ────────────────────────────────────────

    [Fact]
    public void Validate_AdNotConfigured_AnyNonEmptyCredentials_ReturnsSuccess()
    {
        var sut = Build(new AppConfig
        {
            Domain = "",
            LdapServer = "",
            AllowedAdGroups = []
        });

        var (result, _, _) = sut.Validate("anyuser", "anypassword");

        result.Should().Be(AuthResult.Success);
    }

    [Fact]
    public void Validate_AdNotConfigured_ReturnsUsernameAsFullUserName()
    {
        var sut = Build(new AppConfig
        {
            Domain = "",
            LdapServer = "",
            AllowedAdGroups = []
        });

        var (_, fullUserName, _) = sut.Validate("jsmith", "password");

        fullUserName.Should().Be("jsmith");
    }

    [Fact]
    public void Validate_AdNotConfigured_ReturnsEmptyGroups()
    {
        var sut = Build(new AppConfig
        {
            Domain = "",
            LdapServer = "",
            AllowedAdGroups = []
        });

        var (_, _, groups) = sut.Validate("jsmith", "password");

        groups.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidCredentials_ReturnsNullFullUserName()
    {
        var sut = Build(new AppConfig());

        var (_, fullUserName, _) = sut.Validate("", "");

        fullUserName.Should().BeNull();
    }
}
