namespace IdempotencyKit.Core;

/// <summary>
/// Bounded JSON replay representation shared by all store and transactional providers.
/// </summary>
public sealed record StoredHttpResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);
