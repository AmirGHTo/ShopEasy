using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ShopEasy.Identity.Data;
using ShopEasy.Shared.Security;

namespace ShopEasy.Identity.Configuration;

// Gives a new deployment its first administrator. Without it nobody could ever be an admin: the
// admin role is only ever assigned here and by the demo seeder, which must never run in production.
//
// At every start-up the roles exist afterwards. If Bootstrap:AdminEmail is set and no account in
// the admin role exists yet, that account is created with Bootstrap:AdminPassword (or, if the email
// already belongs to an account, that account is made an administrator). Once there is an
// administrator the settings are ignored: they never change an existing account, so a password
// left in the configuration cannot reset one, and can be removed after the first start.
//
// Several Identity pods may start together; whichever loses a race finds the account its sibling
// created and carries on.
public static class AdminBootstrap
{
    public const string EmailSetting = "Bootstrap:AdminEmail";
    public const string PasswordSetting = "Bootstrap:AdminPassword";

    public static async Task RunAsync(IServiceProvider services)
    {
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        var config = services.GetRequiredService<IConfiguration>();
        var db = services.GetRequiredService<ApplicationDbContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ShopEasy.Identity.AdminBootstrap");

        foreach (var role in new[] { Roles.Admin, Roles.Customer })
            await EnsureRoleAsync(roles, db, role);

        var email = config[EmailSetting]?.Trim();
        if (string.IsNullOrEmpty(email))
            return;

        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            logger.LogInformation(
                "An administrator already exists: {Setting} is ignored (it can be removed from the configuration)",
                PasswordSetting);
            return;
        }

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = await CreateAsync(users, db, email, config[PasswordSetting]);
            logger.LogWarning(
                "Created the first administrator {Email}. Remove {Setting} from the configuration; it is not used again",
                email, PasswordSetting);
        }
        else
        {
            logger.LogWarning(
                "No administrator exists: {Email} is made an administrator because {Setting} names it",
                email, EmailSetting);
        }

        await MakeAdministratorAsync(users, db, user);
    }

    private static async Task EnsureRoleAsync(RoleManager<IdentityRole> roles, ApplicationDbContext db, string role)
    {
        if (await roles.RoleExistsAsync(role))
            return;
        try
        {
            var result = await roles.CreateAsync(new IdentityRole(role));
            // "already taken": another instance created it between the check and now
            if (!result.Succeeded && result.Errors.Any(e => e.Code != "DuplicateRoleName"))
                Check(result, $"create the role '{role}'");
        }
        catch (DbUpdateException)
        {
            // fine if another instance created it a moment ago
            Forget(db);
            if (!await roles.RoleExistsAsync(role))
                throw;
        }
    }

    private static async Task<ApplicationUser> CreateAsync(
        UserManager<ApplicationUser> users, ApplicationDbContext db, string email, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                $"{EmailSetting} is set and there is no administrator yet, but {PasswordSetting} is not set: " +
                "there is no password to create the account with.");

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = "Administrator",
            LastName = "",
            EmailConfirmed = true,
        };

        try
        {
            var result = await users.CreateAsync(user, password);
            // not the password: the account was created by another instance first
            var raced = !result.Succeeded && result.Errors.All(e => e.Code is "DuplicateUserName" or "DuplicateEmail");
            if (!raced)
                Check(result, $"create the administrator {email}");
        }
        catch (DbUpdateException)
        {
            // fine if both instances passed the check and this one lost at the unique index
            Forget(db);
            if (await users.FindByEmailAsync(email) is null)
                throw;
        }

        return await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"The administrator {email} could not be created.");
    }

    private static async Task MakeAdministratorAsync(UserManager<ApplicationUser> users, ApplicationDbContext db, ApplicationUser user)
    {
        try
        {
            var result = await users.AddToRoleAsync(user, Roles.Admin);
            if (!result.Succeeded && result.Errors.Any(e => e.Code != "UserAlreadyInRole"))
                Check(result, $"make {user.Email} an administrator");
        }
        catch (DbUpdateException)
        {
            // fine if another instance did it a moment ago
            Forget(db);
            if (!await users.IsInRoleAsync(user, Roles.Admin))
                throw;
        }
    }

    // The insert that lost the race stays in the context, and the next save would try it again
    private static void Forget(ApplicationDbContext db) => db.ChangeTracker.Clear();

    // The reasons Identity gives never contain the password
    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Could not {what}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
    }
}
