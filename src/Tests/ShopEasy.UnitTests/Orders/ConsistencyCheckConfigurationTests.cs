using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The consistency check's settings are written down where an operator looks: the Order service's
// appsettings.json, the Compose file (and .env.example for its variables) and the Kubernetes ConfigMap.
// The code's defaults are the truth; these tests fail when one of the copies says something else.
public class ConsistencyCheckConfigurationTests
{
    private static string Read(params string[] path) => RepositoryFiles.Read(path);

    private static Dictionary<string, string> Defaults()
    {
        var options = new ConsistencyCheckOptions();
        return new Dictionary<string, string>
        {
            [nameof(options.Enabled)] = options.Enabled ? "true" : "false",
            [nameof(options.IntervalSeconds)] = options.IntervalSeconds.ToString(CultureInfo.InvariantCulture),
            [nameof(options.SettleSeconds)] = options.SettleSeconds.ToString(CultureInfo.InvariantCulture),
            [nameof(options.LookbackMinutes)] = options.LookbackMinutes.ToString(CultureInfo.InvariantCulture),
            [nameof(options.MaxOrdersPerPass)] = options.MaxOrdersPerPass.ToString(CultureInfo.InvariantCulture),
        };
    }

    [Fact]
    public void TheDefaultsListEveryProperty()
    {
        var settable = typeof(ConsistencyCheckOptions).GetProperties().Where(p => p.CanWrite).Select(p => p.Name);

        Defaults().Keys.Should().BeEquivalentTo(settable, "a new setting needs its place in every copy");
    }

    [Fact]
    public void TheOrderAppsettings_ShowTheDefaults()
    {
        using var document = JsonDocument.Parse(Read("src", "Services", "ShopEasy.OrderService", "appsettings.json"));

        var section = document.RootElement.GetProperty(ConsistencyCheckOptions.SectionName).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetRawText());

        section.Should().BeEquivalentTo(Defaults());
    }

    [Fact]
    public void TheAppsettings_AreWhatTheServiceReads()
    {
        var file = Path.Combine(RepositoryFiles.Root, "src", "Services", "ShopEasy.OrderService", "appsettings.json");

        var options = new ConsistencyCheckOptions();
        new ConfigurationBuilder().AddJsonFile(file).Build().GetSection(ConsistencyCheckOptions.SectionName).Bind(options);

        options.IntervalIsValid.Should().BeTrue();
        options.SettleIsValid.Should().BeTrue();
        options.LookbackIsLongEnough.Should().BeTrue();
        options.MaxOrdersIsValid.Should().BeTrue();
    }

    private static Dictionary<string, string> EnvironmentLines(string text, string valuePattern) =>
        Regex.Matches(text, $@"^\s*ConsistencyCheck__(\w+):\s*""{valuePattern}""\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    [Fact]
    public void Compose_PassesEverySettingWithTheSameDefault() =>
        EnvironmentLines(Read("docker-compose.yml"), @"\$\{ORDER_CONSISTENCY_CHECK_[A-Z_]+:-([a-z0-9]+)\}")
            .Should().BeEquivalentTo(Defaults());

    [Fact]
    public void TheKubernetesConfigMap_HasEverySettingWithTheSameValue() =>
        EnvironmentLines(Read("k8s", "configmaps", "configmap.yaml"), "([a-z0-9]+)")
            .Should().BeEquivalentTo(Defaults());

    [Fact]
    public void TheEnvExample_ShowsEveryComposeVariableWithItsDefault()
    {
        var fromCompose = Regex.Matches(Read("docker-compose.yml"),
                @"ConsistencyCheck__\w+:\s*""\$\{(ORDER_CONSISTENCY_CHECK_[A-Z_]+):-([a-z0-9]+)\}""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var fromExample = Regex.Matches(Read(".env.example"), @"^# (ORDER_CONSISTENCY_CHECK_[A-Z_]+)=([a-z0-9]+)\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        fromCompose.Should().HaveCount(Defaults().Count);
        fromExample.Should().BeEquivalentTo(fromCompose);
    }

    [Fact]
    public void TheOrderService_IsTheOneThatGetsThem()
    {
        var compose = Read("docker-compose.yml");
        var order = compose[compose.IndexOf("\n  order:", StringComparison.Ordinal)..];
        order = order[..order.IndexOf("\n  gateway:", StringComparison.Ordinal)];

        order.Should().Contain("ConsistencyCheck__IntervalSeconds");
    }
}
