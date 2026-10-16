using Microsoft.AspNetCore.Identity;

namespace ShopEasy.Identity.Configuration;

// The rules for accounts and passwords, in one place so that everything that creates an account
// (the demo seeder, the first administrator, tests) is held to the same ones.
public static class IdentityPolicy
{
    public static void Configure(IdentityOptions options)
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
    }
}
