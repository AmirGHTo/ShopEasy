using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IIdentityServerInteractionService _interaction;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        SignInManager<ApplicationUser> signInManager,
        IIdentityServerInteractionService interaction,
        ILogger<LoginModel> logger)
    {
        _signInManager  = signInManager;
        _interaction    = interaction;
        _logger         = logger;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        public string Email    { get; set; } = default!;
        public string Password { get; set; } = default!;
        public bool RememberMe { get; set; }
    }

    public async Task OnGetAsync(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(HttpContext.Request.Query["error"].ToString()))
            ErrorMessage = "An error occurred. Please try again.";

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (!ModelState.IsValid)
        {
            ReturnUrl = returnUrl;
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password,
            isPersistent: Input.RememberMe,
            lockoutOnFailure: true);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in", Input.Email);

            // Validate the return URL is from a legitimate IdentityServer context
            if (_interaction.IsValidReturnUrl(returnUrl) || Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToPage("/Index");
        }

        if (result.IsLockedOut)
        {
            _logger.LogWarning("User account {Email} locked out", Input.Email);
            ErrorMessage = "Account locked out. Please try again later.";
        }
        else
        {
            ErrorMessage = "Invalid email or password.";
        }

        ReturnUrl = returnUrl;
        return Page();
    }
}
