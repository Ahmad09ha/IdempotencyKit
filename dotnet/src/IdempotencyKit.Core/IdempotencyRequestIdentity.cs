namespace IdempotencyKit.Core;

/// <summary>
/// Descriptive input for a scoped idempotency record. No persistence or fingerprinting is implemented.
/// </summary>
/// <param name="Namespace">Service namespace.</param>
/// <param name="OperationId">Stable logical operation identifier, including API version if necessary.</param>
/// <param name="PrincipalId">Authenticated principal or client scope.</param>
/// <param name="ClientKey">Key supplied by the client for one logical operation.</param>
/// <param name="RequestFingerprint">Versioned fingerprint of relevant request contents.</param>
/// <param name="TenantId">Optional tenant boundary.</param>
public sealed record IdempotencyRequestIdentity(
    string Namespace,
    string OperationId,
    string PrincipalId,
    string ClientKey,
    string RequestFingerprint,
    string? TenantId = null);
