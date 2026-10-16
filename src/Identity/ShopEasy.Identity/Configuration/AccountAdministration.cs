using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Identity;
using ShopEasy.Identity.Data;
using ShopEasy.Shared.Security;

namespace ShopEasy.Identity.Configuration;

public sealed record AdminOutcome(bool Succeeded, string Message)
{
    public static AdminOutcome Done(string message) => new(true, message);
    public static AdminOutcome Refused(string message) => new(false, message);
    public static AdminOutcome NoSuchAccount => new(false, "That account no longer exists.");
}

// What an administrator may do to another account: give it the admin or the customer role, disable
// it and enable it again. The rules that keep the system administrable live here, apart from the
// page, so that they are tested without one:
//   - nobody changes their own role or disables their own account;
//   - the last administrator that is not disabled can be neither demoted nor disabled.
// Accounts are disabled rather than deleted: orders refer to user ids.
public class AccountAdministration(
    UserManager<ApplicationUser> users,
    IPersistedGrantService grants,
    ILogger<AccountAdministration> logger)
{
    // A disabled account is locked out until the year 9999; a temporary lockout (too many wrong
    // passwords) ends by itself. A round date rather than DateTimeOffset.MaxValue, which PostgreSQL's
    // microsecond precision would not return unchanged.
    public static readonly DateTimeOffset DisabledUntil = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsDisabled(ApplicationUser user) => user.LockoutEnd is { } end && end >= DisabledUntil;

    public static bool IsLockedOut(ApplicationUser user) => user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow;

    public async Task<AdminOutcome> SetRoleAsync(string actorId, string userId, string role)
    {
        if (role is not (Roles.Admin or Roles.Customer))
            return AdminOutcome.Refused("Unknown role.");
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return AdminOutcome.NoSuchAccount;
        if (user.Id == actorId)
            return AdminOutcome.Refused("You cannot change your own role.");

        var current = await users.GetRolesAsync(user);
        if (current.Count == 1 && current[0] == role)
            return AdminOutcome.Done($"{user.Email} already is {Article(role)}.");
        if (role != Roles.Admin && current.Contains(Roles.Admin) && !await AnotherActiveAdministratorAsync(user.Id))
            return AdminOutcome.Refused(LastAdministrator);

        Check(await users.RemoveFromRolesAsync(user, current.Where(r => r != role).ToList()));
        if (!current.Contains(role))
            Check(await users.AddToRoleAsync(user, role));
        // The Identity session of that account carries its roles: end it at its next security check
        // (within a minute) so that the next sign-in carries the new ones. Tokens already issued keep
        // the old role until they expire or are renewed.
        await users.UpdateSecurityStampAsync(user);

        logger.LogWarning("Administrator {ActorId} made account {UserId} {Role}", actorId, user.Id, role);
        return AdminOutcome.Done($"{user.Email} is now {Article(role)}.");
    }

    public async Task<AdminOutcome> SetEnabledAsync(string actorId, string userId, bool enabled)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return AdminOutcome.NoSuchAccount;

        if (enabled)
        {
            // also lifts a lockout from too many wrong passwords
            Check(await users.SetLockoutEndDateAsync(user, null));
            Check(await users.ResetAccessFailedCountAsync(user));
            logger.LogWarning("Administrator {ActorId} enabled account {UserId}", actorId, user.Id);
            return AdminOutcome.Done($"{user.Email} can sign in again.");
        }

        if (user.Id == actorId)
            return AdminOutcome.Refused("You cannot disable your own account.");
        if (await users.IsInRoleAsync(user, Roles.Admin) && !await AnotherActiveAdministratorAsync(user.Id))
            return AdminOutcome.Refused(LastAdministrator);

        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, DisabledUntil));
        // end what the account holds: its Identity session within a minute, its refresh tokens now
        await users.UpdateSecurityStampAsync(user);
        await grants.RemoveAllGrantsAsync(user.Id);

        logger.LogWarning("Administrator {ActorId} disabled account {UserId}", actorId, user.Id);
        return AdminOutcome.Done($"{user.Email} is disabled. Tokens already issued stop working when they expire.");
    }

    // For a user who cannot sign in (a forgotten password: there is no e-mail to reset it by). The
    // password is generated here and returned once; the account has to replace it at its next
    // sign-in. The old password stops working and so do the account's sessions and refresh tokens.
    public async Task<TemporaryPasswordOutcome> SetTemporaryPasswordAsync(string actorId, string userId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return TemporaryPasswordOutcome.Refused(AdminOutcome.NoSuchAccount.Message);
        if (user.Id == actorId)
            return TemporaryPasswordOutcome.Refused("Change your own password on the Change password page.");

        var password = TemporaryPassword.Generate();
        // also updates the security stamp: the Identity session of that account ends within a minute
        Check(await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password));
        if (!(await users.GetClaimsAsync(user)).Any(c => c.Type == MustChangePassword.ClaimType))
            Check(await users.AddClaimAsync(user, MustChangePassword.NewClaim()));
        // a lockout from too many wrong passwords is over (the user is about to sign in); a disabled account stays disabled
        if (!IsDisabled(user))
        {
            Check(await users.SetLockoutEndDateAsync(user, null));
            Check(await users.ResetAccessFailedCountAsync(user));
        }
        await grants.RemoveAllGrantsAsync(user.Id);

        logger.LogWarning("Administrator {ActorId} set a temporary password for account {UserId}", actorId, user.Id);
        return new TemporaryPasswordOutcome(true, $"{user.Email} has a temporary password.", user.Email, password);
    }

    private const string LastAdministrator =
        "This is the last administrator that is not disabled: make another account an administrator first.";

    private async Task<bool> AnotherActiveAdministratorAsync(string excludingId) =>
        (await users.GetUsersInRoleAsync(Roles.Admin)).Any(u => u.Id != excludingId && !IsDisabled(u));

    private static string Article(string role) => role == Roles.Admin ? "an administrator" : "a customer";

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("The account store refused the change: " +
                string.Join("; ", result.Errors.Select(e => e.Code)));
    }
}
