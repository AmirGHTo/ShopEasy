using MassTransit;

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
}
