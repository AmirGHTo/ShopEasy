using System.ComponentModel.DataAnnotations;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Pages.Account;

// A signed-in user changes their own password. Everything that was issued under the old one stops
// working: the refresh tokens (so the gateway session and any other app end at their next token
// renewal) and, through the new security stamp, the other browser sessions of Identity itself.
// An account that signed in with a temporary password set by an administrator lands here first
// (PasswordChangeRequiredMiddleware) and is sent on to where it was going once it has chosen its own.
[Authorize]
public class ChangePasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IPersistedGrantService _grants;
    private readonly ILogger<ChangePasswordModel> _logger;

    public ChangePasswordModel(
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        IPersistedGrantService grants,
        ILogger<ChangePasswordModel> logger)
    {
        _users = users;
        _signIn = signIn;
        _grants = grants;
        _logger = logger;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Message { get; set; }

    // True while the account still has to replace a temporary password
    public bool Required { get; private set; }

    public class InputModel
    {
        [Required] public string CurrentPassword { get; set; } = "";
        [Required, StringLength(128)] public string NewPassword { get; set; } = "";
        [Required, Compare(nameof(NewPassword), ErrorMessage = "The two new passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    public void OnGet() => Required = MustChangePassword.IsSet(User);

    public async Task<IActionResult> OnPostAsync()
    {
        Required = MustChangePassword.IsSet(User);
        if (!ModelState.IsValid)
            return Page();

        var user = await _users.GetUserAsync(User);
        if (user is null)
            return Challenge(); // the account is gone: sign in again

        if (Input.NewPassword == Input.CurrentPassword)
        {
            ModelState.AddModelError(string.Empty, "Choose a new password that differs from the current one.");
            return Page();
        }

        // Counts a wrong current password against the account lockout, like the sign-in page does
        var check = await _signIn.CheckPasswordSignInAsync(user, Input.CurrentPassword, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            _logger.LogWarning("Password change refused: account {UserId} is locked out", user.Id);
            ModelState.AddModelError(string.Empty, "Account locked out. Please try again later.");
            return Page();
        }
        if (!check.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "The current password is not correct.");
            return Page();
        }

        var changed = await _users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!changed.Succeeded)
        {
            // the password policy: say what to change (the messages never contain the password)
            foreach (var error in changed.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        // the temporary password is gone: nothing more is required of this account (before the
        // cookie is renewed, so that the new one no longer carries the requirement)
        var pending = (await _users.GetClaimsAsync(user)).Where(c => c.Type == MustChangePassword.ClaimType).ToList();
        if (pending.Count > 0)
            await _users.RemoveClaimsAsync(user, pending);

        // keep this browser signed in under the new security stamp, then end every other session
        await _signIn.RefreshSignInAsync(user);
        await _grants.RemoveAllGrantsAsync(user.Id);
        _logger.LogInformation("Password of account {UserId} changed", user.Id);

        Required = false;
        if (Url.IsLocalUrl(ReturnUrl))
            return LocalRedirect(ReturnUrl!);

        Message = "Your password was changed. Other sessions and apps will ask you to sign in again.";
        return Page();
    }
}
