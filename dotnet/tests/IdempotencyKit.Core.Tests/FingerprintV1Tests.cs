using System.Text.Json;
using IdempotencyKit.Core;
using Xunit;

namespace IdempotencyKit.Core.Tests;

/// <summary>Shared golden vectors also executed by the TypeScript test suite.</summary>
public sealed class FingerprintV1Tests
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void AllScopeGoldenVectorsMatch()
    {
        using var document = LoadFixtures();
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var item in document.RootElement.GetProperty("scopeCases").EnumerateArray())
        {
            var input = item.GetProperty("scope").Deserialize<IdempotencyRequestScope>(Options)!;
            Assert.Equal(item.GetProperty("expected").GetString(), RequestIdentityHasher.ComputeScopeKey(input));
        }
    }

    [Fact]
    public void AllRequestFingerprintGoldenVectorsMatch()
    {
        using var document = LoadFixtures();
        foreach (var item in document.RootElement.GetProperty("fingerprintCases").EnumerateArray())
        {
            var input = item.GetProperty("request").Deserialize<RequestFingerprintInput>(Options)!;
            Assert.Equal(item.GetProperty("expected").GetString(), RequestFingerprinter.Compute(input));
        }
    }

    [Fact]
    public void InvalidRequestsAndKeysAreRejected()
    {
        using var document = LoadFixtures();
        foreach (var item in document.RootElement.GetProperty("invalidCases").EnumerateArray())
        {
            if (item.TryGetProperty("scope", out var scope))
            {
                var input = scope.Deserialize<IdempotencyRequestScope>(Options)!;
                Assert.ThrowsAny<Exception>(() => RequestIdentityHasher.ComputeScopeKey(input));
            }
            else
            {
                var input = item.GetProperty("request").Deserialize<RequestFingerprintInput>(Options)!;
                Assert.ThrowsAny<Exception>(() => RequestFingerprinter.Compute(input));
            }
        }
    }

    private static JsonDocument LoadFixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var fixture = Path.Combine(dir.FullName, "spec", "test-vectors", "fingerprint-v1.json");
            if (File.Exists(fixture))
                return JsonDocument.Parse(File.ReadAllText(fixture));
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate spec/test-vectors/fingerprint-v1.json.");
    }
}
