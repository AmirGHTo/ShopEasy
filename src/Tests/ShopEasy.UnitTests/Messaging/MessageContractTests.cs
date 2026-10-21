using System.Reflection;
using FluentAssertions;
using MassTransit;
using ShopEasy.Shared.Contracts;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The messages the services send each other (ShopEasy.Shared.Contracts) are a contract between
// services that are deployed one at a time: during a rolling update an old Inventory consumes what a
// new Order publishes, and the other way round. Renaming or retyping a property, or removing an enum
// value, breaks that without any compile error. This test fails when the contracts differ from the
// approved list, so a change is a decision: read the difference, and if it is compatible (a new
// message, a new optional property) approve it with UPDATE_CONTRACTS=1 dotnet test --filter MessageContractTests.
// A breaking change needs a new message type instead, with both versions served until the old one is gone.
public class MessageContractTests
{
    private static readonly string[] ApprovedFile = ["src", "Tests", "ShopEasy.UnitTests", "Messaging", "message-contracts.approved.txt"];

    private static readonly NullabilityInfoContext Nullability = new();

    private static string Name(Type type, bool nullable = false)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
            return Name(inner) + "?";
        var name = type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(a => Name(a)))}>"
            : type switch
            {
                _ when type == typeof(string) => "string",
                _ when type == typeof(int) => "int",
                _ when type == typeof(bool) => "bool",
                _ when type == typeof(decimal) => "decimal",
                _ when type == typeof(long) => "long",
                _ when type == typeof(double) => "double",
                _ => type.Name
            };
        return nullable ? name + "?" : name;
    }

    // One block per contract type, everything sorted, so the text only changes when a contract does
    private static string Describe()
    {
        var lines = new List<string>();
        foreach (var type in typeof(ReserveInventoryCommand).Assembly.GetTypes()
                     .Where(t => t.Namespace == typeof(ReserveInventoryCommand).Namespace && t.IsPublic)
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (type.IsEnum)
            {
                lines.Add($"enum {type.Name}");
                lines.AddRange(Enum.GetValues(type).Cast<object>()
                    .OrderBy(v => Convert.ToInt64(v))
                    .Select(v => $"  {v} = {Convert.ToInt64(v)}"));
                continue;
            }

            lines.Add($"message {type.Name}");
            lines.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"  {p.Name}: {Name(p.PropertyType, Nullability.Create(p).ReadState == NullabilityState.Nullable && !p.PropertyType.IsValueType)}"));
        }
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public void TheContracts_AreTheApprovedOnes()
    {
        var actual = Describe();
        var path = Path.Combine([RepositoryFiles.Root, .. ApprovedFile]);

        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var approved = File.ReadAllText(path).Replace("\r\n", "\n");
        if (approved == actual)
            return;

        var approvedLines = approved.Split('\n').ToHashSet();
        var actualLines = actual.Split('\n').ToHashSet();
        var removed = approved.Split('\n').Where(l => !actualLines.Contains(l));
        var added = actual.Split('\n').Where(l => !approvedLines.Contains(l));
        Assert.Fail(
            "The message contracts differ from message-contracts.approved.txt.\n"
            + "No longer there (a consumer that is not updated yet still expects it):\n  " + string.Join("\n  ", removed)
            + "\nNew:\n  " + string.Join("\n  ", added)
            + "\nIf this is compatible (a new message or a new optional property), approve it: "
            + "UPDATE_CONTRACTS=1 dotnet test --filter MessageContractTests. A breaking change needs a new message type.");
    }

    // MassTransit routes a message by the name of its type, namespace included. A consumer of a copy of
    // a contract declared inside a service would never receive what the other service publishes.
    [Fact]
    public void EveryConsumer_ListensToASharedContract()
    {
        var services = new[]
        {
            typeof(ShopEasy.OrderService.Sagas.OrderConfirmedSagaStarter).Assembly,
            typeof(ShopEasy.InventoryService.Consumers.ReserveInventoryConsumer).Assembly,
            typeof(ShopEasy.PaymentService.Consumers.ProcessPaymentConsumer).Assembly
        };
        var contractNamespace = typeof(ReserveInventoryCommand).Namespace;

        var consumed = services.SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
                .Select(i => (Consumer: t, Message: i.GetGenericArguments()[0])))
            .ToList();

        consumed.Should().NotBeEmpty();
        consumed.Where(c => c.Message.Namespace != contractNamespace)
            .Select(c => $"{c.Consumer.Name} consumes {c.Message.FullName}")
            .Should().BeEmpty($"messages between services must be declared in {contractNamespace}");
    }
}
