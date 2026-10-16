using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Shared.Security;
using ShopEasy.UnitTests.Infrastructure;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Identity;

// The first administrator of a deployment (see AdminBootstrap).
public class AdminBootstrapTests
{
    private const string Email = "owner@example.com";
    private const string Password = "Str0ngPassw0rd";

    private static Dictionary<string, string?> Settings(string? email = Email, string? password = Password) => new()
    {
        [AdminBootstrap.EmailSetting] = email,
        [AdminBootstrap.PasswordSetting] = password,
    };

    // One Identity "instance": its own service provider, sharing only the database
    private static ServiceProvider Instance(Action<DbContextOptionsBuilder> database, Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddDbContext<ApplicationDbContext>(database);
        services.AddIdentity<ApplicationUser, IdentityRole>(IdentityPolicy.Configure)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider InMemory(Dictionary<string, string?> settings, string? database = null)
    {
        var name = database ?? Guid.NewGuid().ToString();
        return Instance(o => o.UseInMemoryDatabase(name), settings);
    }

    private static async Task RunAsync(ServiceProvider instance)
    {
        using var scope = instance.CreateScope();
        await AdminBootstrap.RunAsync(scope.ServiceProvider);
    }

    private static async Task<T> InScope<T>(ServiceProvider instance, Func<UserManager<ApplicationUser>, Task<T>> action)
    {
        using var scope = instance.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    [Fact]
    public async Task WithNothingConfigured_OnlyTheRolesAreCreated()
    {
        using var instance = InMemory(Settings(email: null, password: null));

        await RunAsync(instance);

        using var scope = instance.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        (await roles.RoleExistsAsync(Roles.Admin)).Should().BeTrue();
        (await roles.RoleExistsAsync(Roles.Customer)).Should().BeTrue();
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFirstAdministrator_IsCreatedAndCanSignIn()
    {
        using var instance = InMemory(Settings());

        await RunAsync(instance);

        await InScope(instance, async users =>
        {
            var admin = await users.FindByEmailAsync(Email);
            admin.Should().NotBeNull();
            admin!.EmailConfirmed.Should().BeTrue();
            (await users.IsInRoleAsync(admin, Roles.Admin)).Should().BeTrue();
            (await users.CheckPasswordAsync(admin, Password)).Should().BeTrue();
            return 0;
        });
    }

    [Fact]
    public async Task AnExistingAdministrator_IsNeverChanged_ByARepeatedRunOrANewPassword()
    {
        var database = Guid.NewGuid().ToString();
        using (var first = InMemory(Settings(), database))
            await RunAsync(first);

        // a restart with a different password in the configuration: the account is left alone
        using var restart = InMemory(Settings(password: "An0therPassw0rd"), database);
        await RunAsync(restart);

        await InScope(restart, async users =>
        {
            users.Users.Should().ContainSingle();
            var admin = (await users.FindByEmailAsync(Email))!;
            (await users.CheckPasswordAsync(admin, Password)).Should().BeTrue();
            (await users.CheckPasswordAsync(admin, "An0therPassw0rd")).Should().BeFalse();
            return 0;
        });
    }

    [Fact]
    public async Task WhenAnotherAdministratorExists_NothingIsCreated()
    {
        var database = Guid.NewGuid().ToString();
        using (var first = InMemory(Settings(email: "existing@example.com"), database))
            await RunAsync(first);

        using var second = InMemory(Settings(email: "new@example.com", password: "Passw0rdOfTheNewOne"), database);
        await RunAsync(second);

        await InScope(second, async users =>
        {
            (await users.FindByEmailAsync("new@example.com")).Should().BeNull();
            (await users.GetUsersInRoleAsync(Roles.Admin)).Should().ContainSingle();
            return 0;
        });
    }

    [Fact]
    public async Task AnAccountThatAlreadyHasTheEmail_BecomesTheAdministrator_WithItsPasswordUntouched()
    {
        var database = Guid.NewGuid().ToString();
        using (var seeding = InMemory(Settings(email: null, password: null), database))
        {
            await RunAsync(seeding); // roles
            await InScope(seeding, async users =>
            {
                var created = await users.CreateAsync(new ApplicationUser
                {
                    UserName = Email, Email = Email, FirstName = "Olivia", LastName = "Owner", EmailConfirmed = true
                }, "Customer-Passw0rd");
                created.Succeeded.Should().BeTrue();
                return 0;
            });
        }

        using var instance = InMemory(Settings(password: null), database); // no password needed for an existing account
        await RunAsync(instance);

        await InScope(instance, async users =>
        {
            var user = (await users.FindByEmailAsync(Email))!;
            (await users.IsInRoleAsync(user, Roles.Admin)).Should().BeTrue();
            (await users.CheckPasswordAsync(user, "Customer-Passw0rd")).Should().BeTrue();
            user.FirstName.Should().Be("Olivia");
            return 0;
        });
    }

    [Fact]
    public async Task AnEmailWithoutAPassword_StopsTheStartUp_AndSaysWhy()
    {
        using var instance = InMemory(Settings(password: null));

        var failure = await FluentActions.Awaiting(() => RunAsync(instance)).Should().ThrowAsync<InvalidOperationException>();

        failure.Which.Message.Should().Contain(AdminBootstrap.EmailSetting).And.Contain(AdminBootstrap.PasswordSetting);
    }

    [Theory]
    [InlineData("short1A")]          // too short
    [InlineData("alllowercase123")]  // no upper case
    [InlineData("NoDigitsAtAllHere")]
    public async Task APasswordThatBreaksThePolicy_StopsTheStartUp_WithoutRevealingIt(string password)
    {
        using var instance = InMemory(Settings(password: password));

        var failure = await FluentActions.Awaiting(() => RunAsync(instance)).Should().ThrowAsync<InvalidOperationException>();

        failure.Which.Message.Should().NotContain(password);
        await InScope(instance, async users =>
        {
            (await users.FindByEmailAsync(Email)).Should().BeNull("no account is created with a password that is refused");
            return 0;
        });
    }

    [Fact]
    public async Task ATextThatIsNoEmailAddress_StopsTheStartUp()
    {
        using var instance = InMemory(Settings(email: "not-an-email"));

        await FluentActions.Awaiting(() => RunAsync(instance)).Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- the settings are written down where operators look, and the password nowhere in the repository

    [Fact]
    public void TheSettings_AreInAppsettingsComposeEnvExampleAndKubernetes_WithoutAPassword()
    {
        var appsettings = RepositoryFiles.Read("src", "Identity", "ShopEasy.Identity", "appsettings.json");
        appsettings.Should().Contain("\"AdminEmail\": \"\"");
        appsettings.Should().NotContainEquivalentOf("AdminPassword");

        var compose = RepositoryFiles.Read("docker-compose.yml");
        compose.Should().Contain("Bootstrap__AdminEmail: \"${BOOTSTRAP_ADMIN_EMAIL:-}\"");
        compose.Should().Contain("Bootstrap__AdminPassword: ${BOOTSTRAP_ADMIN_PASSWORD:-}");

        var example = RepositoryFiles.Read(".env.example");
        example.Should().MatchRegex(@"(?m)^BOOTSTRAP_ADMIN_EMAIL=$").And.MatchRegex(@"(?m)^BOOTSTRAP_ADMIN_PASSWORD=$");

        RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml").Should().Contain("Bootstrap__AdminEmail: \"\"");
        var identity = RepositoryFiles.Read("k8s", "identity", "identity.yaml");
        identity.Should().Contain("name: Bootstrap__AdminPassword").And.Contain("key: bootstrap-admin-password")
            .And.Contain("optional: true");
        RepositoryFiles.Read("k8s", "secrets", "secrets.example.yaml").Should().Contain("bootstrap-admin-password");
    }

    // The unique index of the real database is what decides when several pods start at once
    [RequiresPostgresFact]
    public async Task SeveralInstancesStartingTogether_EndWithExactlyOneAdministrator_AndNoneFails()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("bootstrap");
        await using (var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(database.ConnectionString).Options))
            await db.Database.MigrateAsync();

        for (var round = 0; round < 5; round++)
        {
            // each round a fresh database state would be ideal; the table is emptied instead
            await using (var db = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(database.ConnectionString).Options))
            {
                await db.Database.ExecuteSqlRawAsync("""DELETE FROM "AspNetUserRoles"; DELETE FROM "AspNetUsers"; DELETE FROM "AspNetRoles";""");
            }

            var instances = Enumerable.Range(0, 8)
                .Select(_ => Instance(o => o.UseNpgsql(database.ConnectionString), Settings()))
                .ToList();
            try
            {
                await Task.WhenAll(instances.Select(instance => Task.Run(() => RunAsync(instance))));

                await InScope(instances[0], async users =>
                {
                    users.Users.Should().ContainSingle(u => u.Email == Email);
                    (await users.GetUsersInRoleAsync(Roles.Admin)).Should().ContainSingle();
                    return 0;
                });
            }
            finally
            {
                foreach (var instance in instances) await instance.DisposeAsync();
            }
        }
    }
}
