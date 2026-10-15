using FluentAssertions;
using ShopEasy.OrderService.Idempotency;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The Idempotency-Key header and the fingerprint of an order request (no database, no HTTP).
public class IdempotencyKeyTests
{
    private static readonly Guid Desk = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Webcam = Guid.Parse("00000000-0000-0000-0000-000000000005");

    // ── the header ───────────────────────────────────────────────────────────

    [Fact]
    public void WithoutAHeader_ThereIsNoKey_AndThatIsFine_UnlessOneIsRequired()
    {
        IdempotencyKey.TryParse(null, required: false, out var key, out var error).Should().BeTrue();
        key.Should().BeNull();
        error.Should().BeNull();

        IdempotencyKey.TryParse("  ", required: false, out key, out _).Should().BeTrue();
        key.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenAKeyIsRequired_AMissingHeaderIsRejected(string? header)
    {
        IdempotencyKey.TryParse(header, required: true, out var key, out var error).Should().BeFalse();
        key.Should().BeNull();
        error.Should().Contain("Idempotency-Key").And.Contain("required");
    }

    [Theory]
    [InlineData("7b0c3f0e-5d5b-4d57-9f6e-2d5f3f6a9c11")]   // UUID
    [InlineData("01HZX3Q9V4T8M2K6N7P5R1S0AB")]             // ULID
    [InlineData("order_2026-10-15.attempt:1~a")]          // the punctuation that is allowed
    [InlineData("k")]                                      // shortest
    public void ACommonlyUsedKey_IsAccepted(string header)
    {
        IdempotencyKey.TryParse(header, required: false, out var key, out var error).Should().BeTrue();
        key.Should().Be(header);
        error.Should().BeNull();
    }

    [Fact]
    public void SurroundingWhitespace_IsIgnored()
    {
        IdempotencyKey.TryParse("  abc-123  ", required: false, out var key, out _).Should().BeTrue();
        key.Should().Be("abc-123");
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("a,b")]                          // what several header values are joined into
    [InlineData("path/with/slash")]
    [InlineData("naïve")]
    [InlineData("tab\tinside")]
    public void AKeyWithOtherCharacters_IsRejected(string header)
    {
        IdempotencyKey.TryParse(header, required: false, out var key, out var error).Should().BeFalse();
        key.Should().BeNull();
        error.Should().Contain("Idempotency-Key").And.Contain("1-128");
    }

    [Fact]
    public void TheLengthLimit_IsOneHundredTwentyEight()
    {
        IdempotencyKey.TryParse(new string('a', 128), required: false, out _, out _).Should().BeTrue();
        IdempotencyKey.TryParse(new string('a', 129), required: false, out _, out var error).Should().BeFalse();
        error.Should().Contain("128");
    }

    // ── the fingerprint ──────────────────────────────────────────────────────

    private static RequestedLine L(Guid product, int quantity) => new(product, quantity);

    [Fact]
    public void TheSameOrder_HasTheSameFingerprint_WhateverTheOrderOfItsLines()
    {
        var one = OrderRequestFingerprint.Compute([L(Desk, 2), L(Webcam, 1)], "gift");
        var other = OrderRequestFingerprint.Compute([L(Webcam, 1), L(Desk, 2)], "gift");

        other.Should().Be(one);
        one.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void AChangeInWhatIsBought_ChangesTheFingerprint()
    {
        var baseline = OrderRequestFingerprint.Compute([L(Desk, 2)], null);

        OrderRequestFingerprint.Compute([L(Desk, 3)], null).Should().NotBe(baseline);
        OrderRequestFingerprint.Compute([L(Webcam, 2)], null).Should().NotBe(baseline);
        OrderRequestFingerprint.Compute([L(Desk, 2), L(Webcam, 1)], null).Should().NotBe(baseline);
    }

    [Fact]
    public void TheNote_CountsButItsSurroundingWhitespaceAndNullVersusEmptyDoNot()
    {
        var none = OrderRequestFingerprint.Compute([L(Desk, 1)], null);

        OrderRequestFingerprint.Compute([L(Desk, 1)], "").Should().Be(none);
        OrderRequestFingerprint.Compute([L(Desk, 1)], "   ").Should().Be(none);
        OrderRequestFingerprint.Compute([L(Desk, 1)], "  gift ").Should().Be(
            OrderRequestFingerprint.Compute([L(Desk, 1)], "gift"));
        OrderRequestFingerprint.Compute([L(Desk, 1)], "gift").Should().NotBe(none);
    }

    [Fact]
    public void ARequestAsItReachesTheController_IgnoresWhatTheServerIgnores()
    {
        // productName and unitPrice are accepted but never used, so they cannot make two otherwise
        // identical requests look different; a product listed twice is merged by the validator
        var plain = new PlaceOrderRequestBuilder().Item(Desk, 2).Build();
        var decorated = new PlaceOrderRequestBuilder().Item(Desk, 1, "Desk", 1.00m).Item(Desk, 1, "Standing Desk", 999m).Build();

        var first = OrderRequestValidator.Validate(plain);
        var second = OrderRequestValidator.Validate(decorated);

        OrderRequestFingerprint.Compute(second.Lines, decorated.Notes)
            .Should().Be(OrderRequestFingerprint.Compute(first.Lines, plain.Notes));
    }

    [Fact]
    public void TheDefaults_AreOptionalKeys_RememberedForADay()
    {
        var options = new IdempotencyOptions();

        options.RequireKey.Should().BeFalse();
        options.RetentionHours.Should().Be(24);
    }
}

// Builds PlaceOrderRequest values without repeating the object initialisers
internal sealed class PlaceOrderRequestBuilder
{
    private readonly List<ShopEasy.OrderService.Controllers.OrderItemRequest?> _items = [];
    private string? _notes;

    public PlaceOrderRequestBuilder Item(Guid productId, int quantity, string? name = null, decimal? price = null)
    {
        _items.Add(new ShopEasy.OrderService.Controllers.OrderItemRequest
        {
            ProductId = productId, Quantity = quantity, ProductName = name, UnitPrice = price
        });
        return this;
    }

    public PlaceOrderRequestBuilder Notes(string? notes)
    {
        _notes = notes;
        return this;
    }

    public ShopEasy.OrderService.Controllers.PlaceOrderRequest Build() =>
        new() { Items = _items, Notes = _notes };
}
