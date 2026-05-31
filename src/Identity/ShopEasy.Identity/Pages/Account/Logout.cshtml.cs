using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Pages.Account;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IIdentityServerInteractionService _interaction;

    public LogoutModel(
        SignInManager<ApplicationUser> signInManager,
        IIdentityServerInteractionService interaction)
    {
        _signInManager = signInManager;
        _interaction   = interaction;
    }

    public async Task<IActionResult> OnGetAsync(string? logoutId = null)
    {
        await _signInManager.SignOutAsync();

        var logout = await _interaction.GetLogoutContextAsync(logoutId);
        if (logout?.PostLogoutRedirectUri is not null)
            return Redirect(logout.PostLogoutRedirectUri);

        return RedirectToPage("/Index");
    }
}
