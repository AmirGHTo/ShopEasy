using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ShopEasy.Shared.Logging;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The console format and the version shown in every log line are written down in several places:
// each service's appsettings.json, the Compose file with .env.example, the Kubernetes ConfigMap, the
// Dockerfiles (the version) and the README. The code's own defaults are the truth; these tests fail
// when a copy says something else.
public class LoggingConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private static readonly string DefaultFormat = ShopEasyLogging.TextFormat;

    public static TheoryData<string> Services => new()
    {
        "src/Identity/ShopEasy.Identity",
        "src/Gateway/ShopEasy.Gateway",
        "src/Services/ShopEasy.ProductService",
        "src/Services/ShopEasy.InventoryService",
        "src/Services/ShopEasy.PaymentService",
        "src/Services/ShopEasy.OrderService"
    };

    // ── the console format ────────────────────────────────────────────────────

    [Fact]
    public void TheConsoleIsReadableTextUnlessToldOtherwise()
        => ShopEasyLogging.ResolveConsoleFormat(Config()).Should().Be("text");

    [Theory]
    [InlineData("json", "json")]
    [InlineData("JSON", "json")]
    [InlineData(" Json ", "json")]
    [InlineData("text", "text")]
    [InlineData("", "text")]
    public void TheConsoleFormatComesFromConfiguration(string configured, string expected)
        => ShopEasyLogging.ResolveConsoleFormat(Config((ShopEasyLogging.ConsoleFormatKey, configured)))
            .Should().Be(expected);

    [Theory]
    [InlineData("jsno")]
    [InlineData("xml")]
    public void AFormatThatIsNeitherTextNorJson_StopsTheService(string configured)
    {
        var resolve = () => ShopEasyLogging.ResolveConsoleFormat(Config((ShopEasyLogging.ConsoleFormatKey, configured)));

        resolve.Should().Throw<InvalidOperationException>().WithMessage($"*'{configured}'*'text' or 'json'*");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void EveryServicesAppsettings_ListTheConsoleFormatDefault(string directory)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read(directory, "appsettings.json"));

        document.RootElement.GetProperty("Logging").GetProperty("ConsoleFormat").GetString()
            .Should().Be(DefaultFormat);
    }

    [Fact]
    public void Compose_PassesTheFormatToAllSixServices_WithTheSameDefault()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        Regex.Match(compose, @"Logging__ConsoleFormat:\s*""\$\{LOG_FORMAT:-(\w+)\}""").Groups[1].Value
            .Should().Be(DefaultFormat);
        Regex.Matches(compose, @"<<: \[\*logging\b").Count.Should().Be(6, "the six application services, not the infrastructure images");
    }

    [Fact]
    public void TheKubernetesConfigMap_SwitchesToJson()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        var value = Regex.Match(configMap, @"^\s*Logging__ConsoleFormat:\s*""(\w+)""", RegexOptions.Multiline).Groups[1].Value;

        value.Should().Be("json", "a cluster's log collector reads JSON");
        ShopEasyLogging.ResolveConsoleFormat(Config((ShopEasyLogging.ConsoleFormatKey, value))).Should().Be("json");
    }

    [Fact]
    public void TheEnvExample_ShowsTheFormatWithItsDefault()
        => RepositoryFiles.Read(".env.example").Should().Contain($"# LOG_FORMAT={DefaultFormat}\n");

    // ── the version ───────────────────────────────────────────────────────────

    [Fact]
    public void WithoutAVersion_TheLinesSayDev()
        => ShopEasyLogging.ResolveServiceVersion(Config()).Should().Be("dev");

    [Fact]
    public void TheVersionComesFromConfiguration()
        => ShopEasyLogging.ResolveServiceVersion(Config((ShopEasyLogging.ServiceVersionKey, " 3f2c9ab ")))
            .Should().Be("3f2c9ab");

    [Fact]
    public void EveryDockerfile_TakesTheVersionAsABuildArgument_AndBakesItIntoTheImage()
    {
        var dockerfiles = Directory.GetFiles(Path.Combine(RepositoryFiles.Root, "src"), "Dockerfile", SearchOption.AllDirectories);
        dockerfiles.Should().HaveCount(6);

        foreach (var file in dockerfiles)
        {
            var text = File.ReadAllText(file);
            text.Should().Contain($"ARG SERVICE_VERSION={ShopEasyLogging.DefaultServiceVersion}\n", file);
            text.Should().Contain("ENV Service__Version=$SERVICE_VERSION\n", file);
        }
    }

    [Fact]
    public void Compose_BuildsAllSixImagesWithTheVersion()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        Regex.Match(compose, @"SERVICE_VERSION:\s*""\$\{SERVICE_VERSION:-(\w+)\}""").Groups[1].Value
            .Should().Be(ShopEasyLogging.DefaultServiceVersion);
        Regex.Matches(compose, @"^      args: \*build-args$", RegexOptions.Multiline).Count.Should().Be(6);
    }

    [Fact]
    public void ThePublishedImages_AreBuiltWithTheCommitAsTheirVersion()
    {
        var workflow = RepositoryFiles.Read(".github", "workflows", "ci.yml");

        workflow.Should().Contain("build-args: SERVICE_VERSION=${{ steps.image.outputs.version }}");
        workflow.Should().Contain("echo \"version=${GITHUB_SHA::7}\" >> \"$GITHUB_OUTPUT\"");
    }

    // ── the README ────────────────────────────────────────────────────────────

    [Fact]
    public void TheReadme_ExplainsBothSettings_WithTheirDefaults()
    {
        var readme = RepositoryFiles.Read("README.md");

        var format = readme.Split('\n').Single(line => line.StartsWith("| `Logging__ConsoleFormat`", StringComparison.Ordinal));
        format.Should().Contain($"`{DefaultFormat}`").And.Contain("`json`");

        var version = readme.Split('\n').Single(line => line.StartsWith("| `Service__Version`", StringComparison.Ordinal));
        version.Should().Contain($"`{ShopEasyLogging.DefaultServiceVersion}`");
    }
}
