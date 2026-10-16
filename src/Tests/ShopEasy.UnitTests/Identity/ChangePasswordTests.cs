using System.ComponentModel.DataAnnotations;
using Duende.IdentityServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Identity.Pages.Account;
using Xunit;

namespace ShopEasy.UnitTests.Identity;

// A signed-in user changing their own password (ChangePasswordModel).
public class ChangePasswordTests
{
    private const string Email = "customer@example.com";
    private const string Current = "Str0ngPassw0rd";
    private const string Next = "An0therPassw0rd";

    // The real UserManager, SignInManager and password policy over an in-memory database, one user
    // signed in, the page model on top; only IdentityServer's grant service is replaced
    private sealed class Site : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        public DefaultHttpContext Http { get; }
        public ChangePasswordModel Page { get; }
        public UserManager<ApplicationUser> Users { get; }
        public IPersistedGrantService Grants { get; } = Substitute.For<IPersistedGrantService>();
        public ApplicationUser User { get; private set; } = default!;

        public Site()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            services.AddIdentity<ApplicationUser, IdentityRole>(IdentityPolicy.Configure)
                .AddEntityFrameworkStores<ApplicationDbContext>();
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();

            Http = new DefaultHttpContext { RequestServices = _scope.ServiceProvider };
            _scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = Http;
            Users = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            Page = new ChangePasswordModel(
                Users,
                _scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>(),
                Grants,
                NullLogger<ChangePasswordModel>.Instance)
            {
                PageContext = new PageContext(new ActionContext(Http, new RouteData(), new PageActionDescriptor())),
            };
            CreateUserAndSignIn().GetAwaiter().GetResult();
            // the accessor keeps the context in an AsyncLocal: set it here, not inside the async method
            _scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = Http;
        }

        // The account signs in on one request (its own scope and HttpContext); the cookie that
        // answer sets is what the browser sends with the request the page then handles
        private async Task CreateUserAndSignIn()
        {
            User = new ApplicationUser { UserName = Email, Email = Email, FirstName = "Ada", LastName = "Lovelace" };
            (await Users.CreateAsync(User, Current)).Succeeded.Should().BeTrue();

            using var signInScope = _provider.CreateScope();
            var signInRequest = new DefaultHttpContext { RequestServices = signInScope.ServiceProvider };
            signInScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = signInRequest;
            await signInScope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().SignInAsync(User, isPersistent: false);
            SignInCookie = signInRequest.Response.Headers.SetCookie.ToString().Split(';')[0];

            Http.Request.Headers.Cookie = SignInCookie;
            var auth = await Http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            auth.Succeeded.Should().BeTrue();
            Http.User = auth.Principal!;
        }

        public string SignInCookie { get; private set; } = "";

        public void Fill(string current = Current, string next = Next, string? confirm = null) =>
            Page.Input = new ChangePasswordModel.InputModel
            {
                CurrentPassword = current, NewPassword = next, ConfirmPassword = confirm ?? next,
            };

        public async Task<bool> PasswordIs(string password) =>
            await Users.CheckPasswordAsync((await Users.FindByIdAsync(User.Id))!, password);

        public IEnumerable<string> Errors => Page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    [Fact]
    public async Task ChangingThePassword_ReplacesIt_KeepsThisBrowserSignedIn_AndEndsEveryOtherSession()
    {
        using var site = new Site();
        var stampBefore = await site.Users.GetSecurityStampAsync(site.User);
        site.Fill();

        var result = await site.Page.OnPostAsync();

        result.Should().BeOfType<PageResult>();
        site.Page.Message.Should().Contain("changed");
        (await site.PasswordIs(Next)).Should().BeTrue();
        (await site.PasswordIs(Current)).Should().BeFalse();
        // the new stamp ends the other Identity sessions; this browser gets a cookie carrying it
        (await site.Users.GetSecurityStampAsync(site.User)).Should().NotBe(stampBefore);
        site.Http.Response.Headers.SetCookie.ToString().Should().Contain(".AspNetCore.Identity.Application");
        // and the refresh tokens of this user (every app, every device) are revoked
        await site.Grants.Received(1).RemoveAllGrantsAsync(site.User.Id, Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AWrongCurrentPassword_IsRefused_AndNothingChanges()
    {
        using var site = new Site();
        site.Fill(current: "Wr0ngPassword");

        var result = await site.Page.OnPostAsync();

        result.Should().BeOfType<PageResult>();
        site.Errors.Should().ContainSingle().Which.Should().Contain("current password is not correct");
        site.Page.Message.Should().BeNull();
        (await site.PasswordIs(Current)).Should().BeTrue();
        (await site.PasswordIs(Next)).Should().BeFalse();
        await site.Grants.DidNotReceiveWithAnyArgs().RemoveAllGrantsAsync(default!, default, default);
    }

    [Fact]
    public async Task WrongCurrentPasswords_CountTowardTheAccountLockout_WhichThenBlocksEvenTheRightOne()
    {
        using var site = new Site();

        // the default is five failures
        for (var i = 0; i < 5; i++)
        {
            site.Page.ModelState.Clear(); // every request starts with a fresh model state
            site.Fill(current: "Wr0ngPassword");
            await site.Page.OnPostAsync();
        }
        site.Page.ModelState.Clear();
        site.Fill(); // the right current password now
        await site.Page.OnPostAsync();

        site.Errors.Should().ContainSingle().Which.Should().Contain("locked out");
        (await site.PasswordIs(Current)).Should().BeTrue();
        await site.Grants.DidNotReceiveWithAnyArgs().RemoveAllGrantsAsync(default!, default, default);
    }

    [Theory]
    [InlineData("short1A")]
    [InlineData("alllowercase1")]
    [InlineData("ALLUPPERCASE1")]
    [InlineData("NoDigitsHere")]
    public async Task ANewPasswordThatBreaksThePolicy_IsRefused_WithWhatToChange_AndNeverEchoed(string weak)
    {
        using var site = new Site();
        site.Fill(next: weak);

        var result = await site.Page.OnPostAsync();

        result.Should().BeOfType<PageResult>();
        site.Errors.Should().NotBeEmpty().And.NotContain(m => m.Contains(weak));
        (await site.PasswordIs(Current)).Should().BeTrue();
        await site.Grants.DidNotReceiveWithAnyArgs().RemoveAllGrantsAsync(default!, default, default);
    }

    [Fact]
    public async Task TheNewPassword_MustDifferFromTheCurrentOne()
    {
        using var site = new Site();
        site.Fill(next: Current);

        await site.Page.OnPostAsync();

        site.Errors.Should().ContainSingle().Which.Should().Contain("differs");
        await site.Grants.DidNotReceiveWithAnyArgs().RemoveAllGrantsAsync(default!, default, default);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_IsSentToSignInAgain()
    {
        using var site = new Site();
        await site.Users.DeleteAsync(site.User);
        site.Fill();

        (await site.Page.OnPostAsync()).Should().BeOfType<ChallengeResult>();
    }

    [Fact]
    public void TheForm_RequiresAllThreeFieldsAndAMatchingConfirmation()
    {
        static bool Valid(ChangePasswordModel.InputModel input) =>
            Validator.TryValidateObject(input, new ValidationContext(input), new List<ValidationResult>(), validateAllProperties: true);

        ChangePasswordModel.InputModel Good() => new() { CurrentPassword = Current, NewPassword = Next, ConfirmPassword = Next };

        Valid(Good()).Should().BeTrue();
        var noCurrent = Good(); noCurrent.CurrentPassword = "";
        Valid(noCurrent).Should().BeFalse();
        var mismatch = Good(); mismatch.ConfirmPassword = "Different1Password";
        Valid(mismatch).Should().BeFalse();
        var huge = Good(); huge.NewPassword = huge.ConfirmPassword = new string('a', 129) + "A1";
        Valid(huge).Should().BeFalse();
    }

    // Changing the password checks the current one, so a client guessing it is limited like one
    // guessing at the sign-in page: both submissions draw on the same budget
    [Fact]
    public void ChangingThePassword_SharesTheSignInBudget_OfOneClient()
    {
        var settings = new AuthRateLimitSettings { Login = { PermitLimit = 3, WindowSeconds = 3600 } };
        using var limiter = AuthRateLimiting.CreateLimiter(settings);

        static DefaultHttpContext Post(string path)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = "POST";
            http.Request.Path = path;
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.5");
            return http;
        }

        var allowed = new[] { "/Account/Login", "/Account/ChangePassword", "/Account/ChangePassword", "/Account/Login", "/Account/ChangePassword" }
            .Select(path => { using var lease = limiter.AttemptAcquire(Post(path)); return lease.IsAcquired; })
            .ToList();

        allowed.Should().Equal(true, true, true, false, false);
    }
}
