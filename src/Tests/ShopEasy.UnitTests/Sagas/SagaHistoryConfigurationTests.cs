using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.OrderService.Data;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Sagas;

// How long the saga history is kept is written down in the Order service's appsettings.json, the
// Compose file (with .env.example), the Kubernetes ConfigMap and the README. The code's default is
// the truth; these tests fail when one of the copies says something else.
public class SagaHistoryConfigurationTests
{
    private static readonly SagaHistoryOptions Defaults = new();

    [Fact]
    public void TheOrderServicesAppsettings_ListTheDefault()
    {
        using var document = JsonDocument.Parse(
            RepositoryFiles.Read("src", "Services", "ShopEasy.OrderService", "appsettings.json"));

        var section = document.RootElement.GetProperty("SagaHistory");

        section.GetProperty("RetentionDays").GetInt32().Should().Be(Defaults.RetentionDays);
        section.EnumerateObject().Should().HaveCount(1, "every setting is listed, and nothing else");
    }

    [Fact]
    public void Compose_PassesTheSettingToTheOrderServiceOnly_WithTheDefault()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        var order = Regex.Match(compose, @"^  order:\n(.*?)(?=^  [a-z][a-z-]*:\n|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline).Value;
        order.Should().Contain(
            $"SagaHistory__RetentionDays: \"${{ORDER_SAGA_HISTORY_RETENTION_DAYS:-{Defaults.RetentionDays}}}\"");
        Regex.Matches(compose, "SagaHistory__").Should().HaveCount(1, "only the Order service keeps a saga history");
    }

    [Fact]
    public void TheKubernetesConfigMap_HasTheSettingWithTheDefault()
        => RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml")
            .Should().Contain($"SagaHistory__RetentionDays: \"{Defaults.RetentionDays}\"");

    [Fact]
    public void TheEnvExample_ShowsTheVariableWithTheDefault()
        => RepositoryFiles.Read(".env.example")
            .Should().Contain($"# ORDER_SAGA_HISTORY_RETENTION_DAYS={Defaults.RetentionDays}\n");

    [Fact]
    public void TheReadme_ExplainsTheSetting_WithItsDefault()
    {
        var readme = RepositoryFiles.Read("README.md");

        var row = readme.Split('\n').Single(line => line.StartsWith("| `SagaHistory__RetentionDays`", StringComparison.Ordinal));
        row.Should().Contain($"`{Defaults.RetentionDays}`");
    }
}
