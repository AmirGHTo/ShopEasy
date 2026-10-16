using System.Reflection;
using System.Security.Claims;
using Duende.IdentityServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
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
using ShopEasy.Identity.Pages.Admin;
using ShopEasy.Shared.Security;
using Xunit;

namespace ShopEasy.UnitTests.Identity;

// The real UserManager and password policy over an in-memory database; only IdentityServer's grant
// service is replaced. Set up asynchronously (IAsyncLifetime) rather than blocking in a constructor.
public abstract class AdminTestBase : IAsyncLifetime
{
    protected const string Password = "Str0ngPassw0rd";

    private ServiceProvider _provider = default!;
    private IServiceScope _scope = default!;
    protected UserManager<ApplicationUser> Users = default!;
    protected ApplicationDbContext Db = default!;
    protected IPersistedGrantService Grants { get; } = Substitute.For<IPersistedGrantService>();
    protected AccountAdministration Administration = default!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddIdentity<ApplicationUser, IdentityRole>(IdentityPolicy.Configure)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        Users = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var roles = _scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await roles.CreateAsync(new IdentityRole(Roles.Admin));
        await roles.CreateAsync(new IdentityRole(Roles.Customer));
        Administration = new AccountAdministration(Users, Grants, NullLogger<AccountAdministration>.Instance);
    }

    public Task DisposeAsync()
    {
        _scope.Dispose();
        _provider.Dispose();
        return Task.CompletedTask;
    }

    protected async Task<ApplicationUser> CreateAsync(string email, string role = Roles.Customer, string first = "Ada", string last = "Lovelace")
    {
        var user = new ApplicationUser { UserName = email, Email = email, FirstName = first, LastName = last };
        (await Users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        (await Users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        return user;
    }

    protected async Task<IList<string>> RolesOf(ApplicationUser user) => await Users.GetRolesAsync(user);

    protected static string NoActor => "someone-else";
}

// The rules of AccountAdministration: who may change what, and what stays protected.
public class AccountAdministrationTests : AdminTestBase
{
    [Fact]
    public async Task ACustomer_CanBeMadeAnAdministrator_WhichEndsTheirOldSessionAndKeepsOneRole()
    {
        var customer = await CreateAsync("c@example.com");
        var before = await Users.GetSecurityStampAsync(customer);

        var outcome = await Administration.SetRoleAsync(NoActor, customer.Id, Roles.Admin);

        outcome.Succeeded.Should().BeTrue();
        (await RolesOf(customer)).Should().Equal(Roles.Admin);
        (await Users.GetSecurityStampAsync(customer)).Should().NotBe(before);
    }

    [Fact]
    public async Task AnAdministrator_CanBeMadeACustomer_WhenAnotherActiveOneRemains()
    {
        await CreateAsync("boss@example.com", Roles.Admin);
        var second = await CreateAsync("second@example.com", Roles.Admin);

        var outcome = await Administration.SetRoleAsync(NoActor, second.Id, Roles.Customer);

        outcome.Succeeded.Should().BeTrue();
        (await RolesOf(second)).Should().Equal(Roles.Customer);
    }

    [Fact]
    public async Task AnAccountThatAlreadyHasTheRole_IsLeftAlone()
    {
        var customer = await CreateAsync("c@example.com");
        var before = await Users.GetSecurityStampAsync(customer);

        var outcome = await Administration.SetRoleAsync(NoActor, customer.Id, Roles.Customer);

        outcome.Succeeded.Should().BeTrue();
        outcome.Message.Should().Contain("already");
        (await Users.GetSecurityStampAsync(customer)).Should().Be(before);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("")]
    [InlineData("ADMIN")]
    public async Task AnUnknownRole_IsRefused_AndNothingChanges(string role)
    {
        var customer = await CreateAsync("c@example.com");

        var outcome = await Administration.SetRoleAsync(NoActor, customer.Id, role);

        outcome.Succeeded.Should().BeFalse();
        (await RolesOf(customer)).Should().Equal(Roles.Customer);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Customer)]
    public async Task NobodyChangesTheirOwnRole(string role)
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        await CreateAsync("other@example.com", Roles.Admin);

        var outcome = await Administration.SetRoleAsync(me.Id, me.Id, role);

        outcome.Succeeded.Should().BeFalse();
        outcome.Message.Should().Contain("your own");
        (await RolesOf(me)).Should().Equal(Roles.Admin);
    }

    [Fact]
    public async Task TheLastAdministrator_CannotBeMadeACustomer()
    {
        var only = await CreateAsync("only@example.com", Roles.Admin);
        await CreateAsync("c@example.com");

        var outcome = await Administration.SetRoleAsync(NoActor, only.Id, Roles.Customer);

        outcome.Succeeded.Should().BeFalse();
        outcome.Message.Should().Contain("last administrator");
        (await RolesOf(only)).Should().Equal(Roles.Admin);
    }

    [Fact]
    public async Task AnAdministratorWhoseOnlyColleagueIsDisabled_IsStillTheLastOne()
    {
        var active = await CreateAsync("active@example.com", Roles.Admin);
        var colleague = await CreateAsync("gone@example.com", Roles.Admin);
        (await Administration.SetEnabledAsync(active.Id, colleague.Id, false)).Succeeded.Should().BeTrue();

        // someone else (not `active` itself) tries to take the admin role from the one that remains
        var outcome = await Administration.SetRoleAsync(NoActor, active.Id, Roles.Customer);

        outcome.Succeeded.Should().BeFalse();
        (await RolesOf(active)).Should().Equal(Roles.Admin);
    }

    [Fact]
    public async Task DisablingAnAccount_LocksItOut_EndsItsSessions_AndRevokesItsRefreshTokensOnly()
    {
        var customer = await CreateAsync("c@example.com");
        var other = await CreateAsync("other@example.com");
        var before = await Users.GetSecurityStampAsync(customer);

        var outcome = await Administration.SetEnabledAsync(NoActor, customer.Id, false);

        outcome.Succeeded.Should().BeTrue();
        (await Users.IsLockedOutAsync(customer)).Should().BeTrue();
        AccountAdministration.IsDisabled(customer).Should().BeTrue();
        (await Users.GetSecurityStampAsync(customer)).Should().NotBe(before);
        await Grants.Received(1).RemoveAllGrantsAsync(customer.Id, Arg.Any<string?>(), Arg.Any<string?>());
        await Grants.DidNotReceive().RemoveAllGrantsAsync(other.Id, Arg.Any<string?>(), Arg.Any<string?>());
        (await Users.IsLockedOutAsync(other)).Should().BeFalse();
    }

    [Fact]
    public async Task EnablingAnAccount_LetsItSignInAgain_AndClearsTheCountOfWrongPasswords()
    {
        var customer = await CreateAsync("c@example.com");
        await Administration.SetEnabledAsync(NoActor, customer.Id, false);
        await Users.AccessFailedAsync(customer);

        var outcome = await Administration.SetEnabledAsync(NoActor, customer.Id, true);

        outcome.Succeeded.Should().BeTrue();
        (await Users.IsLockedOutAsync(customer)).Should().BeFalse();
        AccountAdministration.IsDisabled(customer).Should().BeFalse();
        customer.AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task EnablingAlsoLiftsAnOrdinaryLockout()
    {
        var customer = await CreateAsync("c@example.com");
        await Users.SetLockoutEndDateAsync(customer, DateTimeOffset.UtcNow.AddMinutes(5));
        AccountAdministration.IsLockedOut(customer).Should().BeTrue();
        AccountAdministration.IsDisabled(customer).Should().BeFalse("a lockout from wrong passwords ends by itself");

        await Administration.SetEnabledAsync(NoActor, customer.Id, true);

        (await Users.IsLockedOutAsync(customer)).Should().BeFalse();
    }

    [Fact]
    public async Task NobodyDisablesTheirOwnAccount()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        await CreateAsync("other@example.com", Roles.Admin);

        var outcome = await Administration.SetEnabledAsync(me.Id, me.Id, false);

        outcome.Succeeded.Should().BeFalse();
        outcome.Message.Should().Contain("your own");
        (await Users.IsLockedOutAsync(me)).Should().BeFalse();
        await Grants.DidNotReceiveWithAnyArgs().RemoveAllGrantsAsync(default!, default, default);
    }

    [Fact]
    public async Task TheLastAdministrator_CannotBeDisabled_ButAnotherOneCan()
    {
        var first = await CreateAsync("first@example.com", Roles.Admin);

        (await Administration.SetEnabledAsync(NoActor, first.Id, false)).Succeeded.Should().BeFalse();
        (await Users.IsLockedOutAsync(first)).Should().BeFalse();

        var second = await CreateAsync("second@example.com", Roles.Admin);
        (await Administration.SetEnabledAsync(NoActor, second.Id, false)).Succeeded.Should().BeTrue();
        // and now `first` is the last one that is not disabled again
        (await Administration.SetEnabledAsync(NoActor, first.Id, false)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ACustomerNeedsNoAdministratorToBeLeft()
    {
        var customer = await CreateAsync("c@example.com");

        (await Administration.SetEnabledAsync(NoActor, customer.Id, false)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_IsReportedAsSuch()
    {
        (await Administration.SetRoleAsync(NoActor, "gone", Roles.Admin)).Message.Should().Contain("no longer exists");
        (await Administration.SetEnabledAsync(NoActor, "gone", false)).Message.Should().Contain("no longer exists");
        (await Administration.SetEnabledAsync(NoActor, "gone", true)).Succeeded.Should().BeFalse();
    }
}

// The accounts page: who may open it, what it lists, and how it reports what the rules answered.
public class UsersPageTests : AdminTestBase
{
    private UsersModel Page(ApplicationUser? actor = null)
    {
        var http = new DefaultHttpContext();
        if (actor is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id)], "test"));
        return new UsersModel(Db, Users, Administration)
        {
            PageContext = new PageContext(new ActionContext(http, new RouteData(), new PageActionDescriptor())),
        };
    }

    [Fact]
    public void ThePage_IsOnlyForAdministrators()
    {
        var authorize = typeof(UsersModel).GetCustomAttribute<AuthorizeAttribute>();

        authorize.Should().NotBeNull();
        authorize!.Roles.Should().Be(Roles.Admin);
        typeof(UsersModel).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }

    [Fact]
    public async Task TheList_ShowsEmailNameRoleAndStatus_InOrder_AndMarksTheViewer()
    {
        var me = await CreateAsync("b-me@example.com", Roles.Admin, "Grace", "Hopper");
        await CreateAsync("a-customer@example.com", Roles.Customer, "Ada", "Lovelace");
        var disabled = await CreateAsync("c-disabled@example.com");
        await Administration.SetEnabledAsync(NoActor, disabled.Id, false);
        var locked = await CreateAsync("d-locked@example.com");
        await Users.SetLockoutEndDateAsync(locked, DateTimeOffset.UtcNow.AddMinutes(5));

        var page = Page(me);
        await page.OnGetAsync();

        page.Rows.Select(r => r.Email).Should().Equal(
            "a-customer@example.com", "b-me@example.com", "c-disabled@example.com", "d-locked@example.com");
        page.Rows[0].Should().Match<UsersModel.Row>(r => r.Name == "Ada Lovelace" && r.Role == "customer" && !r.IsAdmin && r.Status == "Active" && !r.IsYou);
        page.Rows[1].Should().Match<UsersModel.Row>(r => r.IsYou && r.IsAdmin && r.Role == "admin");
        page.Rows[2].Should().Match<UsersModel.Row>(r => r.Status == "Disabled" && r.IsDisabled);
        page.Rows[3].Should().Match<UsersModel.Row>(r => r.Status == "Locked out for a while" && r.IsDisabled);
    }

    [Fact]
    public async Task Search_FindsAccountsByPartOfTheEmail_IgnoringCase_AndTrimsTheText()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        await CreateAsync("Alice@Shop.example");
        await CreateAsync("bob@shop.example");

        var page = Page(me);
        page.Q = "  ALICE ";
        await page.OnGetAsync();

        page.Rows.Select(r => r.Email).Should().Equal("Alice@Shop.example");
        page.Q.Should().Be("ALICE");
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task NoMatch_GivesAnEmptyListAndOnePage()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);

        var page = Page(me);
        page.Q = "nobody";
        await page.OnGetAsync();

        page.Rows.Should().BeEmpty();
        page.Pages.Should().Be(1);
        page.P.Should().Be(1);
    }

    [Fact]
    public async Task LongLists_ArePaged_AndAPageOutOfRangeIsBroughtBack()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        for (var i = 0; i < 29; i++)
            await CreateAsync($"user{i:00}@example.com");

        var first = Page(me);
        await first.OnGetAsync();
        first.Rows.Should().HaveCount(UsersModel.PageSize);
        first.Total.Should().Be(30);
        first.Pages.Should().Be(2);

        var second = Page(me);
        second.P = 2;
        await second.OnGetAsync();
        second.Rows.Should().HaveCount(5);

        var beyond = Page(me);
        beyond.P = 99;
        await beyond.OnGetAsync();
        beyond.P.Should().Be(2);
        beyond.Rows.Should().HaveCount(5);

        var below = Page(me);
        below.P = -4;
        await below.OnGetAsync();
        below.P.Should().Be(1);
    }

    [Fact]
    public async Task ARoleChange_ReportsSuccess_AndReturnsToTheSameSearchAndPage()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        var customer = await CreateAsync("c@example.com");

        var page = Page(me);
        var result = await page.OnPostRoleAsync(customer.Id, Roles.Admin, "c@", 2);

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.RouteValues!["q"].Should().Be("c@");
        redirect.RouteValues["p"].Should().Be(2);
        page.Message.Should().Contain("c@example.com is now an administrator");
        page.Error.Should().BeNull();
        (await RolesOf(customer)).Should().Equal(Roles.Admin);
    }

    [Fact]
    public async Task ARefusedChange_IsReportedAsAnError_AndChangesNothing()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);

        var page = Page(me);
        await page.OnPostEnabledAsync(me.Id, false, null, 1);

        page.Error.Should().Contain("your own account");
        page.Message.Should().BeNull();
        (await Users.IsLockedOutAsync(me)).Should().BeFalse();
    }

    [Fact]
    public async Task DisablingFromThePage_UsesTheSignedInAdministratorAsTheActor()
    {
        var me = await CreateAsync("me@example.com", Roles.Admin);
        var customer = await CreateAsync("c@example.com");

        var page = Page(me);
        await page.OnPostEnabledAsync(customer.Id, false, null, 1);

        page.Message.Should().Contain("is disabled");
        (await Users.IsLockedOutAsync(customer)).Should().BeTrue();
        await Grants.Received(1).RemoveAllGrantsAsync(customer.Id, Arg.Any<string?>(), Arg.Any<string?>());
    }
}
