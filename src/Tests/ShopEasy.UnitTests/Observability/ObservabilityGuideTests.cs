using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// docs/observability is the team guide, the scenario table (the demo script) and the presentation outline. They
// name files, headings, alerts, dashboards, ports and scripts that live elsewhere; a rename there must not leave
// the guide sending a newcomer to something that is gone.
public class ObservabilityGuideTests
{
    private static readonly string[] PageNames = ["README.md", "scenarios.md", "presentation.md"];

    private static readonly string GuideDirectory = Path.Combine(RepositoryFiles.Root, "docs", "observability");

    private static string Page(string name) => File.ReadAllText(Path.Combine(GuideDirectory, name));

    private static string AllPages => string.Join("\n", PageNames.Select(Page));

    // GitHub's anchor for a heading: lower case, punctuation dropped (the hyphen kept), spaces become hyphens
    private static string Anchor(string heading) =>
        Regex.Replace(Regex.Replace(heading.Replace("`", "").Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s_-]", ""), @"\s", "-");

    private static HashSet<string> Anchors(string markdown) =>
        Regex.Matches(markdown, @"(?m)^#{1,6}\s+(.+?)\s*$").Select(m => Anchor(m.Groups[1].Value)).ToHashSet();

    private static IEnumerable<(string Page, string Target, string Fragment)> Links() =>
        PageNames.SelectMany(page => Regex.Matches(Page(page), @"\]\((?!https?:|mailto:)([^)\s#]*)(#[^)\s]*)?\)")
            .Select(m => (page, m.Groups[1].Value, m.Groups[2].Value.TrimStart('#'))));

    [Fact]
    public void TheThreePagesExist_AndTheGuideLeadsToTheOthers()
    {
        foreach (var name in PageNames)
            File.Exists(Path.Combine(GuideDirectory, name)).Should().BeTrue($"docs/observability/{name} is part of the guide");

        Page("README.md").Should().Contain("](scenarios.md)").And.Contain("](presentation.md)");
        Page("presentation.md").Should().Contain("](scenarios.md");
    }

    [Fact]
    public void EveryRelativeLink_PointsAtAFileOrFolderThatExists()
    {
        Links().Should().NotBeEmpty();
        foreach (var (page, target, _) in Links().Where(l => l.Target.Length > 0))
        {
            var path = Path.GetFullPath(Path.Combine(GuideDirectory, target));
            (File.Exists(path) || Directory.Exists(path)).Should().BeTrue($"{page} links to {target}");
        }
    }

    [Fact]
    public void EveryLinkToAHeading_PointsAtAHeadingThatExists()
    {
        var checkedLinks = 0;
        foreach (var (page, target, fragment) in Links().Where(l => l.Fragment.Length > 0))
        {
            var path = Path.GetFullPath(target.Length == 0 ? Path.Combine(GuideDirectory, page) : Path.Combine(GuideDirectory, target));
            Anchors(File.ReadAllText(path)).Should().Contain(fragment, $"{page} links to {target}#{fragment}");
            checkedLinks++;
        }
        checkedLinks.Should().BeGreaterThan(20, "the pages lean on the README for the details");
    }

    [Fact]
    public void Anchors_FollowTheRulesOfGitHub()
    {
        Anchor("Developer Portal & Monitoring").Should().Be("developer-portal--monitoring");
        Anchor("24. Surviving Database and Network Outages").Should().Be("24-surviving-database-and-network-outages");
        Anchor("What it does not do").Should().Be("what-it-does-not-do");
    }

    [Fact]
    public void EveryScenarioOfTheScripts_HasARowInTheScenarioTable()
    {
        var library = RepositoryFiles.Read("ops", "demo", "lib.sh");
        var scenarios = Regex.Match(library, @"^SCENARIOS=""([^""]+)""", RegexOptions.Multiline).Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        scenarios.Should().HaveCount(6);

        foreach (var scenario in scenarios)
            Page("scenarios.md").Should().MatchRegex($@"(?m)^\| `{Regex.Escape(scenario)}` ", $"the demo script has a row for {scenario}");
    }

    [Fact]
    public void TheAlertsTheGuideNames_AreAlertsWithARunbook()
    {
        var rules = RepositoryFiles.Read("prometheus-rules.yml");
        var readme = RepositoryFiles.Read("README.md");

        // a CamelCase word in code font is an alert, except for the field names of the logs
        string[] notAlerts = ["OrderId", "CorrelationId", "TraceId"];
        var named = Regex.Matches(AllPages, @"`([A-Z][a-z]+(?:[A-Z][a-z]+)+)`").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(AllPages, @"`(Watchdog)`").Select(m => m.Groups[1].Value))
            .Where(word => !notAlerts.Contains(word)).ToHashSet();

        named.Should().Contain(["ServiceDown", "DependencyDown", "QueueWithoutConsumers", "DeadLettersPresent", "ErrorBudgetFastBurn", "Watchdog"],
            "the scenario table and the guide are about these");
        foreach (var alert in named)
        {
            rules.Should().Contain($"- alert: {alert}\n", $"the guide names {alert}");
            readme.Should().Contain($"#### {alert}\n", $"{alert} has a runbook in the README");
        }
    }

    [Fact]
    public void EveryDashboardTheGuideNames_IsProvisioned()
    {
        var titles = Directory.GetFiles(Path.Combine(RepositoryFiles.Root, "grafana", "provisioning", "dashboards"), "*.json")
            .Select(file => JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("title").GetString()!)
            .ToHashSet();

        var named = Regex.Matches(AllPages, @"\*\*(ShopEasy [A-Z][A-Za-z ]+?)\*\*").Select(m => m.Groups[1].Value).ToHashSet();

        named.Should().Contain(["ShopEasy Overview", "ShopEasy Service Health", "ShopEasy Order Flow", "ShopEasy Dependencies", "ShopEasy SLOs"]);
        named.Should().BeSubsetOf(titles, "a dashboard the guide sends people to must exist");
    }

    [Fact]
    public void EveryLocalAddress_IsAPortCompose_Publishes()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        var ports = Regex.Matches(AllPages, @"http://localhost:(\d+)").Select(m => m.Groups[1].Value).ToHashSet();

        ports.Should().Contain(["5000", "3000", "5341", "16686", "9090", "9093", "8025"]);
        foreach (var port in ports)
            compose.Should().MatchRegex($@"""{port}:\d+""", $"the guide sends people to localhost:{port}");
    }

    [Fact]
    public void EveryDemoScriptTheGuideRuns_Exists()
    {
        var scripts = Regex.Matches(AllPages, @"ops/demo/(\w+)\.sh").Select(m => m.Groups[1].Value).ToHashSet();

        scripts.Should().Contain(["load", "break", "fix"]);
        foreach (var script in scripts)
            File.Exists(Path.Combine(RepositoryFiles.Root, "ops", "demo", $"{script}.sh")).Should().BeTrue($"{script}.sh is run in the guide");
    }
}
