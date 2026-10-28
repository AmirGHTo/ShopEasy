using Microsoft.AspNetCore.Identity;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Configuration;

public static class DatabaseSeeder
{
    // Seeds demo users for local development (only called when Seed:Enabled=true):
    //   alice@shopeasy.com → admin role,   password from Seed:AdminPassword
    //   bob@shopeasy.com   → customer role, password from Seed:CustomerPassword
    public static async Task SeedUsersAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var config      = services.GetRequiredService<IConfiguration>();

        var adminPassword = config["Seed:AdminPassword"];
        var customerPassword = config["Seed:CustomerPassword"];
        if (string.IsNullOrWhiteSpace(adminPassword) || string.IsNullOrWhiteSpace(customerPassword))
            throw new InvalidOperationException(
                "Seed:Enabled is true but Seed:AdminPassword / Seed:CustomerPassword are not set.");

        // Ensure roles exist
        foreach (var role in new[] { "admin", "customer" })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // Seed Alice — admin user
        await EnsureUser(userManager,
            email:     "alice@shopeasy.com",
            password:  adminPassword,
            firstName: "Alice",
            lastName:  "Admin",
            role:      "admin");

        // Seed Bob — regular customer
        await EnsureUser(userManager,
            email:     "bob@shopeasy.com",
            password:  customerPassword,
            firstName: "Bob",
            lastName:  "Customer",
            role:      "customer");
    }

    private static async Task EnsureUser(
        UserManager<ApplicationUser> userManager,
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
