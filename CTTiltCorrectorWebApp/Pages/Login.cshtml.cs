using CTTiltCorrector.Infrastructure;
using CTTiltCorrector.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace CTTiltCorrector.Pages;

// This attribute is CRITICAL to prevent the "Too Many Redirects" error
[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AdAuthService _authService;
    private readonly AppConfig _appCfg;

    public string ErrorMessage { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;

    public LoginModel(AdAuthService authService, IOptions<AppConfig> appCfg)
    {
        _authService = authService;
        _appCfg = appCfg.Value;
        DomainName = _appCfg.Domain;
    }

    public void OnGet()
    {
        // Clear existing errors on a fresh page load
        ErrorMessage = string.Empty;
    }

    public async Task<IActionResult> OnPostAsync(string username, string password)
    {
        Username = username?.Trim() ?? string.Empty;

        // 1. Call the updated service that now returns the user's AD groups
        var (result, fullUserName, groups) = _authService.Validate(Username, password ?? string.Empty);

        switch (result)
        {
            case AuthResult.Success:
                // 2. Create the list of Claims
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, fullUserName!),
                    new(ClaimTypes.WindowsAccountName, Username),
                };

                // 3. MAP AD GROUPS TO ROLES
                // This is what satisfies the .RequireRole() in your FallbackPolicy
                foreach (var groupName in groups)
                {
                    claims.Add(new Claim(ClaimTypes.Role, groupName));
                }

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                // 4. Sign in and issue the authentication cookie
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = false, // Session-based (clears on browser close)
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                    });

                // 5. Redirect to the requested page or the default search page
                var returnUrl = Request.Query["returnUrl"].ToString();
                return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/search" : returnUrl);

            case AuthResult.InvalidCredentials:
                ErrorMessage = "Incorrect username or password.";
                break;

            case AuthResult.AccountDisabled:
                ErrorMessage = "Your account is disabled. Contact your administrator.";
                break;

            case AuthResult.NotInRequiredGroup:
                ErrorMessage = "Your account does not have access to this application.";
                break;

            case AuthResult.LdapError:
                ErrorMessage = "Unable to contact the authentication server. Please try again.";
                break;
        }

        // If we got here, something went wrong, redisplay the form
        DomainName = _appCfg.Domain;
        return Page();
    }
}