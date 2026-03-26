using CTTiltCorrector.Infrastructure;
using CTTiltCorrector.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace CTTiltCorrector.Pages;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AdAuthService _authService;
    private readonly AppConfig _appCfg;

    public string ErrorMessage { get; private set; } = string.Empty;
    public string Username { get; private set; } = string.Empty;
    public string DomainName { get; private set; } = string.Empty;

    public LoginModel(AdAuthService authService, IOptions<AppConfig> appCfg)
    {
        _authService = authService;
        _appCfg = appCfg.Value;
        DomainName = appCfg.Value.Domain;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string username, string password)
    {
        Username = username?.Trim() ?? string.Empty;

        var (result, fullUserName) = _authService.Validate(Username, password ?? string.Empty);

        switch (result)
        {
            case AuthResult.Success:
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, fullUserName!),
                    new(ClaimTypes.WindowsAccountName, Username),
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = false,   // session cookie — expires on browser close
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                    });

                var returnUrl = Request.Query["returnUrl"].ToString();
                return Redirect(string.IsNullOrEmpty(returnUrl) ? "/search" : returnUrl);

            case AuthResult.InvalidCredentials:
                ErrorMessage = "Incorrect username or password.";
                break;

            case AuthResult.AccountDisabled:
                ErrorMessage = "Your account is disabled. Contact your administrator.";
                break;

            case AuthResult.NotInRequiredGroup:
                ErrorMessage = "Your account does not have access to this application. " +
                               "Contact your administrator to be added to the required group.";
                break;

            case AuthResult.LdapError:
                ErrorMessage = "Unable to contact the authentication server. Please try again or contact IT support.";
                break;
        }

        DomainName = _appCfg.Domain;
        return Page();
    }
}
