using System.ComponentModel.DataAnnotations;
using Duende.IdentityServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Identity.Pages.Account;
using ShopEasy.Shared.Security;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Identity;

// Customers creating their own account (RegisterModel) and the limits on the sign-in and sign-up
// forms (AuthRateLimiting).
public class SignUpTests
{
    private const string Email = "customer@example.com";
    private const string Password = "Str0ngPassw0rd";

    // A small Identity instance: the real UserManager, SignInManager and password policy over an
    // in-memory database, the page model on top of it
    private sealed class Site : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        public DefaultHttpContext Http { get; }
        public RegisterModel Page { get; }
        public UserManager<ApplicationUser> Users { get; }

        public Site(bool enabled = true, string? database = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(database ?? Guid.NewGuid().ToString()));
            services.AddIdentity<ApplicationUser, IdentityRole>(IdentityPolicy.Configure)
                .AddEntityFrameworkStores<ApplicationDbContext>();
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();

            Http = new DefaultHttpContext { RequestServices = _scope.ServiceProvider };
            _scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = Http;
            Users = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var roles = _scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            roles.CreateAsync(new IdentityRole(Roles.Customer)).GetAwaiter().GetResult();
            roles.CreateAsync(new IdentityRole(Roles.Admin)).GetAwaiter().GetResult();

            var interaction = Substitute.For<IIdentityServerInteractionService>();
            interaction.IsValidReturnUrl(Arg.Any<string>()).Returns(false);
            var url = Substitute.For<IUrlHelper>();
            url.Content(Arg.Any<string>()).Returns("/");
            url.IsLocalUrl(Arg.Any<string>()).Returns(call => call.Arg<string>().StartsWith('/') && !call.Arg<string>().StartsWith("//"));

            Page = new RegisterModel(
                Users,
                _scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>(),
                interaction,
                Options.Create(new RegistrationSettings { Enabled = enabled }),
                NullLogger<RegisterModel>.Instance)
            {
                PageContext = new PageContext(new ActionContext(Http, new RouteData(), new PageActionDescriptor())),
                Url = url,
            };
        }

        public void Fill(string email = Email, string password = Password, string first = "Ada", string last = "Lovelace") =>
            Page.Input = new RegisterModel.InputModel
            {
                FirstName = first, LastName = last, Email = email, Password = password, ConfirmPassword = password,
            };

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    [Fact]
    public async Task SigningUp_CreatesACustomer_SignsThemIn_AndReturnsToWhereTheyCameFrom()
    {
        using var site = new Site();
        site.Fill();

        var result = await site.Page.OnPostAsync("/connect/authorize/callback?x=1");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/connect/authorize/callback?x=1");
        var user = await site.Users.FindByEmailAsync(Email);
        user.Should().NotBeNull();
        (await site.Users.GetRolesAsync(user!)).Should().Equal(Roles.Customer);
        (await site.Users.CheckPasswordAsync(user, Password)).Should().BeTrue();
        user.FirstName.Should().Be("Ada");
        user.EmailConfirmed.Should().BeFalse("nothing proved the address is theirs");
        site.Http.Response.Headers.SetCookie.ToString().Should().Contain(".AspNetCore.Identity.Application");
    }

    [Fact]
    public async Task SigningUp_NeverMakesAnAdministrator_EvenWhenOneAlreadyExists()
    {
        using var site = new Site();
        var admin = new ApplicationUser { UserName = "boss@example.com", Email = "boss@example.com", FirstName = "B", LastName = "B" };
        await site.Users.CreateAsync(admin, Password);
        await site.Users.AddToRoleAsync(admin, Roles.Admin);
        site.Fill();

        await site.Page.OnPostAsync();

        (await site.Users.GetUsersInRoleAsync(Roles.Admin)).Should().ContainSingle().Which.Email.Should().Be("boss@example.com");
    }

    [Fact]
    public async Task AReturnAddressOnAnotherSite_IsNotFollowed()
    {
        using var site = new Site();
        site.Fill();

        var result = await site.Page.OnPostAsync("https://evil.example/steal");

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("/Index");
    }

    [Theory]
    [InlineData("customer@example.com")]
    [InlineData("CUSTOMER@Example.com")]
    public async Task AnAddressThatIsAlreadyRegistered_IsRefusedWithoutSayingSo_AndTheAccountIsLeftAlone(string again)
    {
        using var site = new Site();
        site.Fill();
        await site.Page.OnPostAsync();

        site.Fill(email: again, password: "An0therPassw0rd");
        var result = await site.Page.OnPostAsync();

        result.Should().BeOfType<PageResult>();
        site.Page.ErrorMessage.Should().Be(RegisterModel.NotCreated);
        site.Page.ErrorMessage.Should().NotContainEquivalentOf("taken").And.NotContainEquivalentOf("already registered");
        site.Page.ModelState.ErrorCount.Should().Be(0);
        site.Users.Users.Should().ContainSingle();
        var user = (await site.Users.FindByEmailAsync(Email))!;
        (await site.Users.CheckPasswordAsync(user, Password)).Should().BeTrue();
        (await site.Users.CheckPasswordAsync(user, "An0therPassw0rd")).Should().BeFalse();
    }

    [Theory]
    [InlineData("short1A")]          // too short
    [InlineData("alllowercase1")]    // no upper case
    [InlineData("ALLUPPERCASE1")]    // no lower case
    [InlineData("NoDigitsHere")]     // no digit
    public async Task AWeakPassword_IsRefused_WithWhatToChange_AndNeverEchoed(string weak)
    {
        using var site = new Site();
        site.Fill(password: weak);

        var result = await site.Page.OnPostAsync();

        result.Should().BeOfType<PageResult>();
        site.Users.Users.Should().BeEmpty();
        var messages = site.Page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
        messages.Should().NotBeEmpty();
        messages.Should().NotContain(m => m.Contains(weak));
    }

    [Fact]
    public async Task WhenSignUpIsClosed_BothTheFormAndTheSubmissionAnswerNotFound_AndNothingIsCreated()
    {
        using var site = new Site(enabled: false);
        site.Fill();

        site.Page.OnGet().Should().BeOfType<NotFoundResult>();
        (await site.Page.OnPostAsync()).Should().BeOfType<NotFoundResult>();
        site.Users.Users.Should().BeEmpty();
    }

    [Fact]
    public void TheForm_RequiresTheFieldsAMatchingConfirmationAndARealAddress()
    {
        static bool Valid(RegisterModel.InputModel input) =>
            Validator.TryValidateObject(input, new ValidationContext(input), new List<ValidationResult>(), validateAllProperties: true);

        RegisterModel.InputModel Good() => new()
        {
            FirstName = "Ada", LastName = "Lovelace", Email = Email, Password = Password, ConfirmPassword = Password,
        };

        Valid(Good()).Should().BeTrue();

        var mismatch = Good(); mismatch.ConfirmPassword = "Different1Password";
        Valid(mismatch).Should().BeFalse();
        var notAnAddress = Good(); notAnAddress.Email = "not-an-email";
        Valid(notAnAddress).Should().BeFalse();
        var noName = Good(); noName.FirstName = "";
        Valid(noName).Should().BeFalse();
        var huge = Good(); huge.Password = huge.ConfirmPassword = new string('a', 129) + "A1";
        Valid(huge).Should().BeFalse();
    }

    // ---- the limits on the forms

    private static DefaultHttpContext Request(string method, string path, string client)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(client);
        return http;
    }

    private static AuthRateLimitSettings Limits(int login = 3, int registration = 2, int windowSeconds = 3600) => new()
    {
        Login = { PermitLimit = login, WindowSeconds = windowSeconds },
        Registration = { PermitLimit = registration, WindowSeconds = windowSeconds },
    };

    private static int Allowed(System.Threading.RateLimiting.PartitionedRateLimiter<HttpContext> limiter, Func<HttpContext> request, int tries)
    {
        var allowed = 0;
        for (var i = 0; i < tries; i++)
        {
            using var lease = limiter.AttemptAcquire(request());
            if (lease.IsAcquired) allowed++;
        }
        return allowed;
    }

    [Fact]
    public void ASingleClient_MaySubmitTheSignInFormOnlySoOften_AndOthersAreNotAffected()
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 3));

        Allowed(limiter, () => Request("POST", "/Account/Login", "203.0.113.5"), 10).Should().Be(3);
        Allowed(limiter, () => Request("POST", "/Account/Login", "203.0.113.6"), 10).Should().Be(3);
    }

    [Fact]
    public void SigningInAndSigningUp_AreCountedSeparately_WithTheirOwnLimits()
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 3, registration: 2));

        Allowed(limiter, () => Request("POST", "/Account/Register", "203.0.113.5"), 10).Should().Be(2);
        Allowed(limiter, () => Request("POST", "/Account/Login", "203.0.113.5"), 10).Should().Be(3);
    }

    [Theory]
    [InlineData("GET", "/Account/Login")]
    [InlineData("GET", "/Account/Register")]
    [InlineData("POST", "/connect/token")]
    [InlineData("POST", "/health")]
    public void OnlySubmissionsOfTheTwoForms_AreLimited(string method, string path)
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 1, registration: 1));

        Allowed(limiter, () => Request(method, path, "203.0.113.5"), 50).Should().Be(50);
    }

    [Fact]
    public void TheAddressOfTheRequest_DecidesWhoseBudgetIsUsed_NotTheCaseOfThePath()
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 2));

        Allowed(limiter, () => Request("POST", "/account/login", "203.0.113.5"), 5).Should().Be(2);
        Allowed(limiter, () => Request("POST", "/ACCOUNT/LOGIN", "203.0.113.5"), 5).Should().Be(0);
    }

    [Fact]
    public void AnIPv6User_CannotGetAFreshBudgetByChangingTheirAddress_WithinTheirNetwork()
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 2));

        Allowed(limiter, () => Request("POST", "/Account/Login", "2001:db8:1:2:aaaa::1"), 5).Should().Be(2);
        // same /64, another address: the budget is shared
        Allowed(limiter, () => Request("POST", "/Account/Login", "2001:db8:1:2:bbbb::9"), 5).Should().Be(0);
        // a different /64 is another client
        Allowed(limiter, () => Request("POST", "/Account/Login", "2001:db8:1:3::1"), 5).Should().Be(2);
    }

    [Fact]
    public void AnIPv4AddressInIPv6Notation_IsTheSameClient()
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 2));

        Allowed(limiter, () => Request("POST", "/Account/Login", "203.0.113.5"), 5).Should().Be(2);
        Allowed(limiter, () => Request("POST", "/Account/Login", "::ffff:203.0.113.5"), 5).Should().Be(0);
    }

    [Fact]
    public async Task TheBudget_ComesBackWhenTheWindowHasPassed()
    {
        using var limiter = AuthRateLimiting.CreateLimiter(Limits(login: 1, windowSeconds: 1));
        Allowed(limiter, () => Request("POST", "/Account/Login", "203.0.113.5"), 3).Should().Be(1);

        // the window is replenished by a timer: wait for it rather than for a fixed time
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var again = 0;
        while (again == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            again = Allowed(limiter, () => Request("POST", "/Account/Login", "203.0.113.5"), 1);
        }
        again.Should().Be(1);
    }

    [Fact]
    public void TheLimitsMustBeAtLeastOne()
    {
        FluentActions.Invoking(() => Limits(login: 0).Validate()).Should().Throw<InvalidOperationException>().WithMessage("*Login*");
        FluentActions.Invoking(() => Limits(windowSeconds: 0).Validate()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => new AuthRateLimitSettings().Validate()).Should().NotThrow();
    }

    // ---- the settings are written down where operators look

    [Fact]
    public void TheSettings_AreInAppsettingsComposeEnvExampleAndKubernetes_WithTheDefaultsOfTheCode()
    {
        var defaults = new AuthRateLimitSettings();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryFiles.Root, "src", "Identity", "ShopEasy.Identity", "appsettings.json"))
            .Build();
        var settings = AuthRateLimitSettings.From(configuration);
        settings.Login.PermitLimit.Should().Be(defaults.Login.PermitLimit);
        settings.Login.WindowSeconds.Should().Be(defaults.Login.WindowSeconds);
        settings.Registration.PermitLimit.Should().Be(defaults.Registration.PermitLimit);
        settings.Registration.WindowSeconds.Should().Be(defaults.Registration.WindowSeconds);
        configuration.GetValue<bool>("Registration:Enabled").Should().Be(new RegistrationSettings().Enabled);

        var compose = RepositoryFiles.Read("docker-compose.yml");
        compose.Should().Contain($"Registration__Enabled: \"${{REGISTRATION_ENABLED:-true}}\"");
        compose.Should().Contain($"RateLimiting__Login__PermitLimit: \"${{RATE_LIMIT_LOGIN_ATTEMPTS:-{defaults.Login.PermitLimit}}}\"");
        compose.Should().Contain($"RateLimiting__Login__WindowSeconds: \"${{RATE_LIMIT_LOGIN_WINDOW_SECONDS:-{defaults.Login.WindowSeconds}}}\"");
        compose.Should().Contain($"RateLimiting__Registration__PermitLimit: \"${{RATE_LIMIT_REGISTRATION_ATTEMPTS:-{defaults.Registration.PermitLimit}}}\"");
        compose.Should().Contain($"RateLimiting__Registration__WindowSeconds: \"${{RATE_LIMIT_REGISTRATION_WINDOW_SECONDS:-{defaults.Registration.WindowSeconds}}}\"");

        var example = RepositoryFiles.Read(".env.example");
        foreach (var variable in new[]
        {
            "REGISTRATION_ENABLED=true",
            $"RATE_LIMIT_LOGIN_ATTEMPTS={defaults.Login.PermitLimit}",
            $"RATE_LIMIT_LOGIN_WINDOW_SECONDS={defaults.Login.WindowSeconds}",
            $"RATE_LIMIT_REGISTRATION_ATTEMPTS={defaults.Registration.PermitLimit}",
            $"RATE_LIMIT_REGISTRATION_WINDOW_SECONDS={defaults.Registration.WindowSeconds}",
        })
            example.Should().Contain("# " + variable);

        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");
        configMap.Should().Contain("Registration__Enabled: \"true\"");
        configMap.Should().Contain($"RateLimiting__Login__PermitLimit: \"{defaults.Login.PermitLimit}\"");
        configMap.Should().Contain($"RateLimiting__Login__WindowSeconds: \"{defaults.Login.WindowSeconds}\"");
        configMap.Should().Contain($"RateLimiting__Registration__PermitLimit: \"{defaults.Registration.PermitLimit}\"");
        configMap.Should().Contain($"RateLimiting__Registration__WindowSeconds: \"{defaults.Registration.WindowSeconds}\"");
    }
}
