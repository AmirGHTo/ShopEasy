using System.Security.Claims;
using Duende.IdentityServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Identity.Pages.Admin;
using ShopEasy.Shared.Security;
using Xunit;

namespace ShopEasy.UnitTests.Identity;

// The password an administrator generates for a user who cannot sign in
public class TemporaryPasswordGeneratorTests
{
    [Fact]
    public void ItIsLongMixedAndWrittenWithoutLookAlikeCharacters()
    {
        for (var i = 0; i < 500; i++)
        {
            var password = TemporaryPassword.Generate();

            password.Should().HaveLength(TemporaryPassword.Length);
            password.Any(char.IsUpper).Should().BeTrue();
            password.Any(char.IsLower).Should().BeTrue();
            password.Any(char.IsDigit).Should().BeTrue();
            TemporaryPassword.UsesOnlyTheAlphabet(password).Should().BeTrue();
            password.IndexOfAny("0O1lI".ToCharArray()).Should().Be(-1);
        }
    }

    [Fact]
    public void ItIsDifferentEveryTime_AndTheKindsOfCharactersAreNotAtFixedPlaces()
    {
        var passwords = Enumerable.Range(0, 1000).Select(_ => TemporaryPassword.Generate()).ToList();

        passwords.Distinct().Should().HaveCount(1000);
        passwords.Select(p => char.IsUpper(p[0])).Distinct().Should().HaveCount(2);
    }
}

public class SetTemporaryPasswordTests : AdminTestBase
{
    [Fact]
    public async Task TheOldPasswordStopsWorking_AndTheNewOneMeetsThePasswordPolicy()
    {
        var user = await CreateAsync("c@example.com");

        var outcome = await Administration.SetTemporaryPasswordAsync(NoActor, user.Id);

        outcome.Succeeded.Should().BeTrue();
        outcome.Email.Should().Be("c@example.com");
        (await Users.CheckPasswordAsync(user, outcome.Password!)).Should().BeTrue();
        (await Users.CheckPasswordAsync(user, Password)).Should().BeFalse();
        foreach (var validator in Users.PasswordValidators)
            (await validator.ValidateAsync(Users, user, outcome.Password!)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task TheAccountMustChangeIt_AndIsMarkedOnlyOnce_HoweverOftenItIsSet()
    {
        var user = await CreateAsync("c@example.com");

        await Administration.SetTemporaryPasswordAsync(NoActor, user.Id);
        await Administration.SetTemporaryPasswordAsync(NoActor, user.Id);

        (await Users.GetClaimsAsync(user)).Where(c => c.Type == MustChangePassword.ClaimType).Should().ContainSingle();
    }

    [Fact]
    public async Task ItEndsWhatTheAccountHolds_ItsSessionAndItsRefreshTokens_AndNothingOfAnyoneElse()
    {
        var user = await CreateAsync("c@example.com");
        var other = await CreateAsync("other@example.com");
        var stamp = await Users.GetSecurityStampAsync(user);
        var otherStamp = await Users.GetSecurityStampAsync(other);

        await Administration.SetTemporaryPasswordAsync(NoActor, user.Id);

        (await Users.GetSecurityStampAsync(user)).Should().NotBe(stamp);
        await Grants.Received(1).RemoveAllGrantsAsync(user.Id, Arg.Any<string?>(), Arg.Any<string?>());
        await Grants.DidNotReceive().RemoveAllGrantsAsync(other.Id, Arg.Any<string?>(), Arg.Any<string?>());
        (await Users.GetSecurityStampAsync(other)).Should().Be(otherStamp);
        (await Users.CheckPasswordAsync(other, Password)).Should().BeTrue();
    }

    [Fact]
    public async Task NobodySetsOneForTheirOwnAccount()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);

        var outcome = await Administration.SetTemporaryPasswordAsync(me.Id, me.Id);

        outcome.Succeeded.Should().BeFalse();
        outcome.Password.Should().BeNull();
        (await Users.CheckPasswordAsync(me, Password)).Should().BeTrue();
        (await Users.GetClaimsAsync(me)).Should().BeEmpty();
        await Grants.DidNotReceiveWithAnyArgs().RemoveAllGrantsAsync(default!, default, default);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_IsRefused()
    {
        var outcome = await Administration.SetTemporaryPasswordAsync(NoActor, "gone");

        outcome.Succeeded.Should().BeFalse();
        outcome.Password.Should().BeNull();
        outcome.Message.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task AnotherAdministrator_CanHaveOneSetToo_ForTheCaseWhereNoOneElseCanGetThemIn()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        var colleague = await CreateAsync("colleague@example.com", Roles.Admin);

        var outcome = await Administration.SetTemporaryPasswordAsync(me.Id, colleague.Id);

        outcome.Succeeded.Should().BeTrue();
        (await Users.CheckPasswordAsync(colleague, outcome.Password!)).Should().BeTrue();
    }

    [Fact]
    public async Task ALockoutFromWrongPasswords_IsOver_ButADisabledAccountStaysDisabled()
    {
        var locked = await CreateAsync("locked@example.com");
        await Users.SetLockoutEnabledAsync(locked, true);
        await Users.SetLockoutEndDateAsync(locked, DateTimeOffset.UtcNow.AddMinutes(5));
        await Users.AccessFailedAsync(locked);
        var disabled = await CreateAsync("disabled@example.com");
        await Administration.SetEnabledAsync(NoActor, disabled.Id, false);

        await Administration.SetTemporaryPasswordAsync(NoActor, locked.Id);
        await Administration.SetTemporaryPasswordAsync(NoActor, disabled.Id);

        AccountAdministration.IsLockedOut((await Users.FindByIdAsync(locked.Id))!).Should().BeFalse();
        (await Users.GetAccessFailedCountAsync(locked)).Should().Be(0);
        AccountAdministration.IsDisabled((await Users.FindByIdAsync(disabled.Id))!).Should().BeTrue();
    }

    [Fact]
    public async Task ThePassword_IsNeverInTheMessage_TheLog_OrWhatTheOutcomePrints()
    {
        var log = new CapturingLogger<AccountAdministration>();
        var administration = new AccountAdministration(Users, Grants, log);
        var user = await CreateAsync("c@example.com");

        var outcome = await administration.SetTemporaryPasswordAsync(NoActor, user.Id);

        outcome.Message.Should().NotContain(outcome.Password!);
        outcome.ToString().Should().NotContain(outcome.Password!);
        log.Messages.Should().NotBeEmpty();
        log.Messages.Should().OnlyContain(m => !m.Contains(outcome.Password!));
        log.Messages.Should().Contain(m => m.Contains(user.Id) && m.Contains("temporary password"));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

// The Users page: showing the password once, and marking the accounts that still have to change it
public class TemporaryPasswordPageTests : AdminTestBase
{
    private UsersModel Page(ApplicationUser actor)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id)], "test")),
        };
        return new UsersModel(Db, Users, Administration)
        {
            MetadataProvider = new EmptyModelMetadataProvider(),
            PageContext = new PageContext(new ActionContext(http, new RouteData(), new PageActionDescriptor()))
            {
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            },
        };
    }

    [Fact]
    public async Task ThePassword_IsShownOnTheAnswerToThatRequest_NeverCached_AndNeverKeptInTempData()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        var user = await CreateAsync("c@example.com");
        var page = Page(me);

        var result = await page.OnPostTemporaryPasswordAsync(user.Id, "c@", 2);

        var shown = result.Should().BeOfType<PartialViewResult>().Which;
        shown.ViewName.Should().Be("_TemporaryPassword");
        var issued = shown.Model.Should().BeOfType<UsersModel.Revealed>().Subject;
        issued.Email.Should().Be("c@example.com");
        (issued.Q, issued.P).Should().Be(("c@", 2)); // for the link back to the same list
        (await Users.CheckPasswordAsync(user, issued.Password)).Should().BeTrue();
        page.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        page.Message.Should().BeNull();
        page.Error.Should().BeNull();
        issued.ToString().Should().NotContain(issued.Password);
    }

    [Fact]
    public async Task ARefusal_IsReportedAndRedirected_WithNoPassword()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        var page = Page(me);

        var result = await page.OnPostTemporaryPasswordAsync(me.Id, "q", 1);

        result.Should().BeOfType<RedirectToPageResult>();
        page.Error.Should().Contain("Change password page");
        page.Response.Headers.CacheControl.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task TheList_MarksTheAccountsThatStillHaveToChangeTheirPassword()
    {
        var me = await CreateAsync("a-me@example.com", Roles.Admin);
        var waiting = await CreateAsync("b-waiting@example.com");
        await CreateAsync("c-plain@example.com");
        await Administration.SetTemporaryPasswordAsync(me.Id, waiting.Id);

        var page = Page(me);
        await page.OnGetAsync();

        page.Rows.Select(r => r.Status).Should().Equal("Active", "Active, must change password", "Active");
    }
}

// What a signed-in account with a temporary password may reach until it has chosen its own
public class PasswordChangeRequiredMiddlewareTests
{
    private static async Task<(bool Reached, HttpContext Http)> Request(string path, string query = "", string method = "GET", bool signedIn = true, bool mustChange = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1") };
        if (mustChange)
            claims.Add(MustChangePassword.NewClaim());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));

        var auth = Substitute.For<IAuthenticationService>();
        auth.AuthenticateAsync(Arg.Any<HttpContext>(), IdentityConstants.ApplicationScheme).Returns(signedIn
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme))
            : AuthenticateResult.NoResult());
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(auth).BuildServiceProvider(),
        };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);

        var reached = false;
        await new PasswordChangeRequiredMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(http);
        return (reached, http);
    }

    [Theory]
    [InlineData("/connect/authorize/callback", "?client_id=gateway&scope=openid", "/Account/ChangePassword?returnUrl=%2Fconnect%2Fauthorize%2Fcallback%3Fclient_id%3Dgateway%26scope%3Dopenid")]
    [InlineData("/", "", "/Account/ChangePassword?returnUrl=%2F")]
    [InlineData("/Admin/Users", "?q=a", "/Account/ChangePassword?returnUrl=%2FAdmin%2FUsers%3Fq%3Da")]
    public async Task AnythingElse_SendsTheAccountToChangeItsPasswordFirst_AndBackAfterwards(string path, string query, string expected)
    {
        var (reached, http) = await Request(path, query);

        reached.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        http.Response.Headers.Location.ToString().Should().Be(expected);
    }

    [Fact]
    public async Task ActingOnAPage_IsRefusedTheSameWay_NotJustReadingIt()
    {
        var (reached, http) = await Request("/Admin/Users", method: "POST");

        reached.Should().BeFalse();
        http.Response.Headers.Location.ToString().Should().StartWith("/Account/ChangePassword?returnUrl=");
    }

    [Theory]
    [InlineData("/Account/ChangePassword")]
    [InlineData("/account/changepassword")]
    [InlineData("/Account/Logout")]
    [InlineData("/connect/endsession")]
    [InlineData("/connect/endsession/callback")]
    public async Task ChangingThePassword_AndSigningOut_StayOpen(string path)
    {
        var (reached, http) = await Request(path);

        reached.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData(true, false)]  // signed in, nothing required
    [InlineData(false, true)]  // not signed in (a service fetching the discovery document, a visitor)
    public async Task EverybodyElse_IsNotAffected(bool signedIn, bool mustChange)
    {
        var (reached, _) = await Request("/connect/token", signedIn: signedIn, mustChange: mustChange);

        reached.Should().BeTrue();
    }
}
