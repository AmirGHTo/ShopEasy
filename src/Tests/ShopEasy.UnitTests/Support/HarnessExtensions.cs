using MassTransit;
using MassTransit.Testing;

namespace ShopEasy.UnitTests.Support;

public static class HarnessExtensions
{
    // The MassTransit test harness treats a bus that has shown no activity for its inactivity
    // timeout (about a second by default) as "the test is over", and a pending Any<T>() then
    // returns false — whatever CancellationToken the caller passed. On a busy CI runner, with
    // the whole suite running in parallel and MassTransit being JIT-compiled for the first time,
    // the first message can take longer than that to arrive, which made tests fail with
    // "expected true but found False" after ~1 s. A generous inactivity timeout leaves the
    // caller's own token in charge of how long to wait; it costs nothing when messages arrive
    // promptly, and a wait for something that never happens still ends at the caller's token.
    public static void AllowSlowRunners(this IBusRegistrationConfigurator configurator) =>
        configurator.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromSeconds(30));

    // What the harness has seen so far, without waiting for more. Select<T>() without a token keeps
    // enumerating until the harness decides the test is over (the inactivity timeout, 30 s with
    // AllowSlowRunners). That timeout fires on another thread; when it fires just as the enumeration
    // ends, the two wait for each other inside MassTransit and the test host never finishes (seen as
    // a stalled run, with the stacks of both threads). An already cancelled token ends the
    // enumeration at once, on the calling thread, and the test no longer sits out the timeout.
    public static IReadOnlyList<IPublishedMessage<T>> Snapshot<T>(this IPublishedMessageList messages) where T : class =>
        messages.Select<T>(new CancellationToken(true)).ToList();

    public static IReadOnlyList<IReceivedMessage<T>> Snapshot<T>(this IReceivedMessageList messages) where T : class =>
        messages.Select<T>(new CancellationToken(true)).ToList();
}
