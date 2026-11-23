namespace IdempotencyKit.Core;

/// <summary>Server-derived scope plus client-provided logical operation key.</summary>
public sealed record IdempotencyRequestScope(
    string Namespace,
    string OperationId,
    string PrincipalId,
    string ClientKey,
    string? TenantId = null);
