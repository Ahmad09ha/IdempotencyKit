using IdempotencyKit.Core;
using Xunit;

namespace IdempotencyKit.Core.Tests;

public sealed class IdempotencyRecordStateTests
{
    [Theory]
    [InlineData(IdempotencyRecordState.Processing, false)]
    [InlineData(IdempotencyRecordState.Completed, true)]
    [InlineData(IdempotencyRecordState.Uncertain, false)]
    public void OnlyCompletedStateIsMarkedCompleted(
        IdempotencyRecordState state,
        bool expected)
    {
        Assert.Equal(expected, state.IsCompleted());
    }

    [Fact]
    public void RequestIdentityPreservesItsScope()
    {
        var identity = new IdempotencyRequestIdentity(
            Namespace: "orders",
            OperationId: "orders.create.v1",
            PrincipalId: "user-1",
            ClientKey: "request-123",
            RequestFingerprint: "sha256:example",
            TenantId: "tenant-1");

        Assert.Equal("tenant-1", identity.TenantId);
        Assert.Equal("orders.create.v1", identity.OperationId);
    }
}
