extern alias LoadGenAssembly;

using System.Text.RegularExpressions;
using FluentAssertions;
using LoadGenAssembly::ShopEasy.LoadGen;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Demo;

// The demo (ops/demo, README "Traffic and failures to try") names alerts, services and settings that live in
// other files. A rename there must not leave the demo telling people to watch something that no longer exists.
public class DemoScriptsTests
{
    private static readonly string Library = RepositoryFiles.Read("ops", "demo", "lib.sh");
    private static readonly string Compose = RepositoryFiles.Read("docker-compose.yml");

    private static string Service(string name) =>
        Regex.Match(Compose, $@"^  {name}:\n(.*?)(?=^  [a-z][a-z-]*:\n|^\S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Value;

    private static string[] Scenarios => Regex.Match(Library, @"^SCENARIOS=""([^""]+)""", RegexOptions.Multiline)
        .Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TheDemoHasTheSixFailuresOfTheIssue()
        => Scenarios.Should().BeEquivalentTo("payment", "postgres", "rabbitmq", "order", "product-hang", "poison");

    [Fact]
    public void EveryScenario_CanBeBroken_AndUndone()
    {
        foreach (var scenario in Scenarios)
        {
            var name = scenario.Replace('-', '_');
            Library.Should().MatchRegex($@"(?m)^break_{name}\(\) \{{", $"{scenario} can be caused");
            Library.Should().MatchRegex($@"(?m)^fix_{name}\(\) \{{", $"{scenario} can be undone");
        }
    }

    [Fact]
    public void TheAlertsTheDemoPromises_AreAlertsOfTheRulesFile()
    {
        var rules = RepositoryFiles.Read("prometheus-rules.yml");
        string[] promised =
        [
            "ServiceDown", "DependencyDown", "QueueWithoutConsumers", "ShopServerErrorsHigh", "ShopSlow",
            "SagaCommandsNotSent", "OutboxBacklog", "PollingLoopStalled", "DeadLettersPresent",
        ];

        foreach (var alert in promised)
        {
            Library.Should().Contain(alert, "the demo says to expect it");
            rules.Should().Contain($"- alert: {alert}\n", "the demo names it");
        }
    }

    [Fact]
    public void TheServicesTheScenariosTouch_AreServicesOfCompose()
    {
        foreach (var service in new[] { "payment", "postgres", "rabbitmq", "order", "product" })
            Compose.Should().MatchRegex($@"(?m)^  {service}:\n", $"the demo stops or freezes {service}");
    }

    [Fact]
    public void ThePoisonMessage_GoesToAQueueTheDeadLetterMonitorWatches()
    {
        var monitored = RepositoryFiles.Read("src", "Services", "ShopEasy.OrderService", "Services", "ErrorQueueClient.cs");

        Library.Should().Contain("POISON_ERROR_QUEUE=payment-process_error");
        monitored.Should().Contain("\"payment-process_error\"");
        RepositoryFiles.Read("src", "Services", "ShopEasy.PaymentService", "Program.cs").Should().Contain("ReceiveEndpoint(\"payment-process\"");
    }

    [Fact]
    public void TheLoadGenerator_IsAProfile_Hardened_WithNoPort_AndComposeHasTheGeneratorsDefaults()
    {
        var load = Service("loadgen");
        var (defaults, _) = LoadSettings.Parse([], _ => null);

        load.Should().Contain("profiles: [\"load\"]", "a plain `docker compose up` must not start it");
        load.Should().Contain("<<: *hardened");
        load.Should().NotContain("ports:");
        load.Should().MatchRegex(@"gateway:\s+condition: service_healthy");
        load.Should().Contain($"LOAD_RATE_PER_MINUTE: \"${{LOAD_RATE_PER_MINUTE:-{defaults!.RatePerMinute}}}\"");
        load.Should().Contain($"LOAD_DURATION_SECONDS: \"${{LOAD_DURATION_SECONDS:-{defaults.DurationSeconds}}}\"");
        load.Should().Contain($"LOAD_MIX: \"${{LOAD_MIX:-{defaults.Confirmed},{defaults.Declined},{defaults.Cancelled}}}\"");
    }

    [Fact]
    public void TheEnvExample_ListsTheLoadSettings_WithTheGeneratorsDefaults()
    {
        var example = RepositoryFiles.Read(".env.example");
        var (defaults, _) = LoadSettings.Parse([], _ => null);

        example.Should().Contain($"# LOAD_RATE_PER_MINUTE={defaults!.RatePerMinute}\n");
        example.Should().Contain($"# LOAD_DURATION_SECONDS={defaults.DurationSeconds}\n");
        example.Should().Contain($"# LOAD_MIX={defaults.Confirmed},{defaults.Declined},{defaults.Cancelled}\n");
    }
}
