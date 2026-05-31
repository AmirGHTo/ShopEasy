using Microsoft.AspNetCore.Identity;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Configuration;

public static class DatabaseSeeder
{
    // Seed test users:
    //   alice@shopeasy.com / Alice1234! → admin role
    //   bob@shopeasy.com   / Bob1234!   → customer role
    public static async Task SeedUsersAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        // Ensure roles exist
        foreach (var role in new[] { "admin", "customer" })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // Seed Alice — admin user
        await EnsureUser(userManager, roleManager,
            email:     "alice@shopeasy.com",
            password:  "Alice1234!",
            firstName: "Alice",
            lastName:  "Admin",
            role:      "admin");

        // Seed Bob — regular customer
        await EnsureUser(userManager, roleManager,
            email:     "bob@shopeasy.com",
            password:  "Bob1234!",
            firstName: "Bob",
            lastName:  "Customer",
            role:      "customer");
    }

    private static async Task EnsureUser(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        string email, string password,
        string firstName, string lastName, string role)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null) return;

        user = new ApplicationUser
        {
            UserName  = email,
            Email     = email,
            FirstName = firstName,
            LastName  = lastName,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new Exception($"Failed to create user {email}: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        await userManager.AddToRoleAsync(user, role);
    }
}
