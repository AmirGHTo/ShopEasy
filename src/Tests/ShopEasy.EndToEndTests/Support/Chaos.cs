using System.ComponentModel;
using System.Diagnostics;

namespace ShopEasy.EndToEndTests.Support;

// Breaks a part of the running stack on purpose, so that a test can check that the system copes and
// heals. "Chaos engineering" in its smallest form: the test takes a service down, lets the system try
// to work without it, brings it back, and asserts that nothing was lost or done twice.
//
// Two ways of taking a service down differ in what the service gets to do:
//   Stop  (SIGTERM)  a polite stop. The service is told to finish and gets a few seconds to do so:
//                    it stops taking new work, finishes the message in hand, closes its connections.
//   Kill  (SIGKILL)  a crash, like a power cut or an out-of-memory kill. Nothing runs. Whatever the
//                    service was in the middle of is simply cut off, and the rest of the system has
//                    to cope with that. This is the one that finds bugs.
//
// By default this runs `docker compose <action> <service>` against the Compose file of the repository.
// E2E_CONTROL names a program to run instead, called as `<program> <stop|kill|start> <service>`, for
// a stack that was started some other way.
public static class Chaos
{
    private static readonly TimeSpan CommandLimit = TimeSpan.FromSeconds(120);

    public static Task StopAsync(string service) => RunAsync("stop", service);

    public static Task KillAsync(string service) => RunAsync("kill", service);

    public static Task StartAsync(string service) => RunAsync("start", service);

    // Takes the service down (killed when `crash`, stopped politely otherwise) and returns something
    // to dispose when the outage should end, which starts the service again. Used with `await using`,
    // so that the service comes back even when the test fails half way: a test must never leave the
    // stack broken for the tests after it.
    public static async Task<IAsyncDisposable> TakeDownAsync(string service, bool crash = false)
    {
        if (crash) await KillAsync(service);
        else await StopAsync(service);
        return new Outage(service);
    }

    private sealed class Outage(string service) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await StartAsync(service);
    }

    private static async Task RunAsync(string action, string service)
    {
        var (program, arguments) = Command(action, service);
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not run '{program}' to {action} {service}. Docker is needed for the failure tests, or " +
                $"E2E_CONTROL has to name a program that can: {exception.Message}");
        }

        using (process)
        {
            // both streams are read while waiting, or a full pipe would stop the program
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(CommandLimit);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"'{program} {string.Join(' ', arguments)}' did not finish within {CommandLimit.TotalSeconds:0} s.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"'{program} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: {await errors} {await output}");
        }
    }

    private static (string Program, string[] Arguments) Command(string action, string service)
    {
        if (Environment.GetEnvironmentVariable("E2E_CONTROL") is { Length: > 0 } control)
            return (control, [action, service]);

        // -f names the Compose file, so the working directory of the test run does not matter
        return ("docker", ["compose", "-f", Path.Combine(RepositoryRoot(), "docker-compose.yml"), action, service]);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("docker-compose.yml not found above the test output.");
    }
}
