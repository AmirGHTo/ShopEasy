using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Resilience;

// Which failures are worth retrying later and which will fail the same way again. The consumers'
// retry policy (ConsumerResilience) is built on this answer, so a wrong "yes" keeps a poison
// message cycling and a wrong "no" sends a message to the error queue just because the
// database restarted.
public class TransientFaultsTests
{
    private static NpgsqlException Dropped() => new("connection dropped", new IOException("reset by peer"));

    [Fact]
    public void ADroppedDatabaseConnection_IsTransient() =>
        TransientFaults.IsTransient(Dropped()).Should().BeTrue();

    [Fact]
    public void EntityFrameworkWrappingDoesNotHideIt() =>
        TransientFaults.IsTransient(new DbUpdateException("save failed", Dropped())).Should().BeTrue();

    [Fact]
    public void ADeepWrapper_IsFollowed() =>
        TransientFaults.IsTransient(
            new InvalidOperationException("outer", new ApplicationException("middle", new TimeoutException())))
            .Should().BeTrue();

    [Fact]
    public void AnAggregate_IsTransient_WhenAnyOfItsFailuresIs() =>
        TransientFaults.IsTransient(new AggregateException(new ArgumentException("a"), Dropped()))
            .Should().BeTrue();

    [Theory]
    [MemberData(nameof(TransientExamples))]
    public void NetworkAndTimeoutFailures_AreTransient(Exception exception) =>
        TransientFaults.IsTransient(exception).Should().BeTrue();

    public static TheoryData<Exception> TransientExamples() => new()
    {
        new TimeoutException(),
        new SocketException((int)SocketError.ConnectionRefused),
        new IOException("Unable to read data from the transport connection"),
        new HttpRequestException("the remote service is down")
    };

    [Theory]
    [MemberData(nameof(PermanentExamples))]
    public void AFailureThatWillRepeat_IsNotTransient(Exception exception) =>
        TransientFaults.IsTransient(exception).Should().BeFalse();

    public static TheoryData<Exception> PermanentExamples() => new()
    {
        new ArgumentNullException("x"),
        new InvalidOperationException("a rule was broken"),
        new NullReferenceException(),
        new DbUpdateException("duplicate key", new InvalidOperationException()),
        // an unrelated IOException subclass: a missing file does not appear on the second try
        new FileNotFoundException("settings.json"),
        // shutdown: the host is stopping, retrying would only delay it
        new OperationCanceledException(),
        new TaskCanceledException()
    };

    [Fact]
    public void NoException_IsNotTransient() => TransientFaults.IsTransient(null).Should().BeFalse();

    [Fact]
    public void ACyclicOrVeryDeepChain_DoesNotHang()
    {
        Exception e = new InvalidOperationException("bottom");
        for (var i = 0; i < 100; i++) e = new InvalidOperationException("level " + i, e);
        TransientFaults.IsTransient(e).Should().BeFalse();
    }
}
