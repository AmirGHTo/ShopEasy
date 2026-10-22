using FluentAssertions;
using ShopEasy.Gateway.Security;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

public class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/orders")]
    [InlineData("/orders/42?page=2#top")]
    [InlineData("/products?next=https://elsewhere.example/")]
    public void APathOnThisSite_IsKept(string url)
    {
        LocalReturnUrl.IsLocal(url).Should().BeTrue();
        LocalReturnUrl.OrHome(url).Should().Be(url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("orders")]
    [InlineData("https://evil.example/")]
    [InlineData("http://evil.example/")]
    [InlineData("//evil.example")]
    [InlineData("///evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("\\/evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\r\nSet-Cookie: a=b")]
    public void AnythingElse_BecomesTheHomePage(string? url)
    {
        LocalReturnUrl.IsLocal(url).Should().BeFalse();
        LocalReturnUrl.OrHome(url).Should().Be("/");
    }
}
