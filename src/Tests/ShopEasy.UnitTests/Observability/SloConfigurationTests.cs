using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// The service level objectives are stated in the README, computed by the rules, drawn by a dashboard and
// kept by Prometheus long enough to add up 30 days. The rules are the truth; these tests fail when another
// file says something else. (That the rules fire when they should is tested by promtool: prometheus-rules.test.yml.)
public class SloConfigurationTests
{
    private static readonly string Rules = RepositoryFiles.Read("prometheus-rules.yml");
    private static readonly string Readme = RepositoryFiles.Read("README.md");

    private static string Alert(string name) =>
        Regex.Match(Rules, $@"- alert: {name}\n(.*?)(?=\n      - alert:|\n      - record:|\n  - name:|\z)", RegexOptions.Singleline).Value;

    [Theory]
    [InlineData("availability", "0.005", "99.5 %", "0.5 %")]
    [InlineData("latency", "0.05", "95 %", "5 %")]
    public void TheBudget_OfEachMeasure_IsTheOneTheReadmeStates(string sli, string budget, string objective, string share)
    {
        Regex.IsMatch(Rules, $@"- record: shopeasy:slo_budget:ratio\n\s+labels:\n\s+sli: {sli}\n\s+expr: vector\({Regex.Escape(budget)}\)")
            .Should().BeTrue($"the {sli} budget is {budget}");
        var row = Readme.Split('\n').Single(line => line.StartsWith($"| {sli} |", StringComparison.Ordinal));
        row.Should().Contain(objective).And.Contain(share);
    }

    [Theory]
    [InlineData("5m")]
    [InlineData("30m")]
    [InlineData("1h")]
    [InlineData("6h")]
    [InlineData("3d")]
    [InlineData("30d")]
    public void EveryWindow_IsRecordedForBothMeasures(string window)
    {
        foreach (var sli in new[] { "availability", "latency" })
            Regex.IsMatch(Rules, $@"- record: shopeasy:gateway_bad_ratio:rate{window}\n\s+labels:\n\s+sli: {sli}\n")
                .Should().BeTrue($"{sli} over {window}");
        Readme.Should().MatchRegex($"(`{window}`|rate{window})", "the README lists the window");
    }

    [Fact]
    public void TheFastBurnAlert_AsksTheHourAndFiveMinutes_AtFourteenPointFour_AsTheReadmeSays()
    {
        var alert = Alert("ErrorBudgetFastBurn");

        alert.Should().Contain("rate1h").And.Contain("rate5m");
        Regex.Matches(alert, @"> 14\.4").Should().HaveCount(2);
        alert.Should().Contain("for: 2m").And.Contain("severity: critical").And.Contain("increase1h >= 100");
        Readme.Should().Contain("above 14.4 times the budget, for 2 minutes");
        Readme.Should().Contain("under 100 requests in the hour");
    }

    [Fact]
    public void TheSlowBurnAlert_AsksSixHoursAndThirtyMinutes_AtSix_AsTheReadmeSays()
    {
        var alert = Alert("ErrorBudgetSlowBurn");

        alert.Should().Contain("rate6h").And.Contain("rate30m");
        Regex.Matches(alert, @"> 6\n|> 6$", RegexOptions.Multiline).Should().HaveCount(2);
        alert.Should().Contain("for: 15m").And.Contain("severity: warning").And.Contain("increase6h >= 300");
        Readme.Should().Contain("above 6 times the budget, for 15 minutes");
        Readme.Should().Contain("(300 in six hours)");
    }

    [Fact]
    public void HealthChecks_AreLeftOutOfEveryMeasure_AndTheLatencyIsCountedAtTheEdgeAboveTwoSeconds()
    {
        var group = Rules[Rules.IndexOf("- name: shopeasy-slo", StringComparison.Ordinal)..Rules.IndexOf("- name: shopeasy-dependencies", StringComparison.Ordinal)];

        // every selector of the group on http_* leaves the health checks out
        Regex.Matches(group, @"http_request\w*\{[^}]*\}").Select(m => m.Value).Should().OnlyContain(selector => selector.Contains("endpoint!~\"/health.*\""));
        // the prometheus-net histogram doubles from 1 ms: its edges around 2 s are 1.024 and 2.048
        Regex.Matches(group, @"le=""([^""]+)""").Select(m => m.Groups[1].Value).Should().OnlyContain(edge => edge == "2.048");
        Readme.Should().Contain("2.048 s");
    }

    [Fact]
    public void Prometheus_KeepsAsLongAsTheBudgetPanelsAddUp()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        compose.Should().Contain("      - --storage.tsdb.retention.time=30d\n");
        Readme.Should().Contain("`--storage.tsdb.retention.time=30d`");
    }

    [Fact]
    public void TheDashboard_OnlyUsesRecordingRulesTheRulesFileDefines()
    {
        var dashboard = RepositoryFiles.Read("grafana", "provisioning", "dashboards", "shopeasy-slo.json");
        var used = Regex.Matches(dashboard, @"shopeasy:[a-z_]+:[a-z0-9_]+").Select(m => m.Value).Distinct().ToList();

        used.Should().NotBeEmpty();
        foreach (var name in used)
            Rules.Should().Contain($"- record: {name}\n", $"the dashboard asks for {name}");
    }
}
