using System.Text.Json;
using IdempotencyKit.Core;
using Xunit;

namespace IdempotencyKit.Core.Tests;

/// <summary>
/// Contract fixture shared with TypeScript and future database providers.
/// This verifies the canonical observable statuses, not database behavior:
/// database adapter tests must separately verify every transactional promise.
/// </summary>
public sealed class TransactionalProviderContractTests
{
    [Fact]
    public void SharedFixtureEnumeratesEveryTransactionalOutcome()
    {
        using var fixture = OpenFixture();
        Assert.Equal(1, fixture.RootElement.GetProperty("version").GetInt32());
        var statuses = fixture.RootElement.GetProperty("statuses");
        Assert.Equal(Enum.GetValues<TransactionalExecutionStatus>().Length,
            statuses.GetArrayLength());

        var names = new HashSet<TransactionalExecutionStatus>();
        foreach (var entry in statuses.EnumerateArray())
        {
            Assert.True(Enum.TryParse<TransactionalExecutionStatus>(
                entry.GetProperty("dotnet").GetString(), out var value));
            Assert.True(names.Add(value), "Fixture must not repeat a status.");
            Assert.Equal(
                value is TransactionalExecutionStatus.Executed
                    or TransactionalExecutionStatus.Replayed,
                entry.GetProperty("response").GetBoolean());

            Assert.Equal(
                value is TransactionalExecutionStatus.Executed ? "once" : "never",
                entry.GetProperty("callback").GetString());
        }

        Assert.Equal(Enum.GetValues<TransactionalExecutionStatus>().Length, names.Count);
    }

    [Fact]
    public void SharedFixtureNeverClaimsCrossDatabaseOrExternalAtomicity()
    {
        using var fixture = OpenFixture();
        var invariants = fixture.RootElement.GetProperty("invariants");

        foreach (var name in new[]
        {
            "businessAndResponseOneTransaction",
            "driverSessionProvidedToCallback",
            "validateResponseBeforeCommit",
            "noAutomaticCallbackRetryOnAmbiguousCommit",
            "completedReplayRequiresFreshAuthorization",
            "sqlDialectAndConcurrencyProviderSpecific"
        })
            Assert.True(invariants.GetProperty(name).GetBoolean(), name);
    }

    private static JsonDocument OpenFixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName,
                "spec", "fixtures", "transactional-provider-v1.json");
            if (File.Exists(path))
                return JsonDocument.Parse(File.ReadAllText(path));
        }

        throw new FileNotFoundException("Shared transactional-provider fixture is missing.");
    }
}
