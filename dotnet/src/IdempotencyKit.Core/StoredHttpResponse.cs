namespace IdempotencyKit.Core;

/// <summary>
/// Proposed bounded HTTP replay representation. No serializer or replay implementation exists yet.
/// </summary>
public sealed record StoredHttpResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);
