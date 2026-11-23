namespace IdempotencyKit.Core;

/// <summary>A decoded query entry. Duplicate values must be preserved.</summary>
public sealed record QueryParameter(string Name, string Value);

/// <summary>
/// Input for a v1 request fingerprint. BodyJson must be the original JSON text:
/// null means the body is absent, whereas the literal "null" means JSON null.
/// </summary>
public sealed record RequestFingerprintInput(
    string Method,
    string OperationId,
    string Path,
    IReadOnlyList<QueryParameter>? Query = null,
    string? BodyJson = null);
