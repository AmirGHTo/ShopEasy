using System.ComponentModel.DataAnnotations;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Shared.Security;

namespace ShopEasy.Identity.Pages.Account;

// Sign-up for customers. The role is never read from the request: every account created here is a
// customer, and an administrator can only come from AdminBootstrap.
[AllowAnonymous]
public class RegisterModel : PageModel
{
    // Said for every refusal that is not about the password, so the page does not tell a stranger
    // which addresses already have an account
    internal const string NotCreated = "We could not create an account with these details. If you already have one, sign in instead.";

    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IIdentityServerInteractionService _interaction;
    private readonly RegistrationSettings _settings;
    private readonly ILogger<RegisterModel> _logger;

    public RegisterModel(
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        IIdentityServerInteractionService interaction,
        IOptions<RegistrationSettings> settings,
        ILogger<RegisterModel> logger)
    {
        _users = users;
        _signIn = signIn;
        _interaction = interaction;
        _settings = settings.Value;
        _logger = logger;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required, StringLength(100)] public string FirstName { get; set; } = "";
        [Required, StringLength(100)] public string LastName { get; set; } = "";
        [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
        [Required, StringLength(128)] public string Password { get; set; } = "";
        [Required, Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (!_settings.Enabled)
            return NotFound();

        ReturnUrl = returnUrl ?? Url.Content("~/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!_settings.Enabled)
            return NotFound();

        returnUrl ??= Url.Content("~/");
        ReturnUrl = returnUrl;
        if (!ModelState.IsValid)
            return Page();

        var email = Input.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = Input.FirstName.Trim(),
            LastName = Input.LastName.Trim(),
            // nothing here proves the address belongs to the person: there is no confirmation e-mail
            EmailConfirmed = false,
        };

        if (await _users.FindByEmailAsync(email) is not null)
            return Refuse(user);

        IdentityResult created;
        try
        {
            created = await _users.CreateAsync(user, Input.Password);
        }
        catch (DbUpdateException)
        {
            // someone else registered the same address a moment ago (unique index)
            if (await _users.FindByEmailAsync(email) is null)
                throw;
            return Refuse(user);
        }

        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
                return Refuse(user);

            // the password policy and the like: say what to change (the messages never contain the password)
            foreach (var error in created.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        var role = await _users.AddToRoleAsync(user, Roles.Customer);
        if (!role.Succeeded)
        {
            // never leave a customer without the role behind
            _logger.LogError("Could not give the new account {UserId} the customer role: {Errors}",
                user.Id, string.Join("; ", role.Errors.Select(e => e.Code)));
            await _users.DeleteAsync(user);
            ErrorMessage = "Sign-up is not available right now. Please try again later.";
            return Page();
        }

        _logger.LogInformation("Customer account {UserId} created", user.Id);
        await _signIn.SignInAsync(user, isPersistent: false);

        if (_interaction.IsValidReturnUrl(returnUrl) || Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToPage("/Index");
    }

    // Takes about as long as a real sign-up (the password is hashed) and says nothing about why
    private IActionResult Refuse(ApplicationUser user)
    {
        _users.PasswordHasher.HashPassword(user, Input.Password);
        _logger.LogInformation("Sign-up refused: the address is already registered");
        ErrorMessage = NotCreated;
        return Page();
    }
}
