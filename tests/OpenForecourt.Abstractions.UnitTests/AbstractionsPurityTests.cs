using System.Reflection;
using NetArchTest.Rules;
using OpenForecourt.Abstractions.Ports;
using Xunit;

namespace OpenForecourt.Abstractions.UnitTests;

/// <summary>
/// Enforces CLAUDE.md section 4: <c>OpenForecourt.Abstractions</c> is the ports + domain
/// layer and must stay dependency-free — no project references and no leakage of
/// infrastructure frameworks into the contract layer.
/// </summary>
public class AbstractionsPurityTests
{
    private static readonly Assembly Abstractions = typeof(IClock).Assembly;

    [Fact]
    public void Abstractions_has_no_dependency_on_infrastructure_frameworks()
    {
        // Fails if any type in Abstractions references ASP.NET, SQLite or RabbitMQ.
        var result = Types.InAssembly(Abstractions)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.Data.Sqlite",
                "Microsoft.EntityFrameworkCore.Sqlite",
                "System.Data.SQLite",
                "RabbitMQ.Client")
            .GetResult();

        Assert.True(result.IsSuccessful, DescribeFailures(result));
    }

    [Fact]
    public void Abstractions_references_only_the_base_class_library()
    {
        // The only assemblies Abstractions is allowed to depend on are the runtime itself.
        // Any transitive project/package reference would show up as a non-System assembly.
        var forbidden = Abstractions
            .GetReferencedAssemblies()
            .Where(a => !IsBaseClassLibrary(a.Name))
            .Select(a => a.Name)
            .ToArray();

        Assert.True(forbidden.Length == 0,
            "Abstractions must reference only the BCL, but also references: " + string.Join(", ", forbidden));
    }

    private static bool IsBaseClassLibrary(string? name) =>
        name is not null &&
        (name is "System.Private.CoreLib" or "System.Runtime" or "netstandard"
         || name.StartsWith("System.", StringComparison.Ordinal)
         || name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal));

    private static string DescribeFailures(TestResult result) =>
        result.FailingTypeNames is { Count: > 0 }
            ? "Offending types: " + string.Join(", ", result.FailingTypeNames)
            : "Abstractions has a forbidden framework dependency.";
}
