namespace IdempotencyKit.Core;

/// <summary>
/// Computes a versioned request fingerprint. This is comparison only, NOT idempotency middleware.
/// </summary>
public static class RequestFingerprinter
{
    public static string Compute(RequestFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Method) ||
            input.Method.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')))
            throw new ArgumentException("Method must contain only ASCII letters.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.OperationId))
            throw new ArgumentException("Operation ID is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Path) ||
            !input.Path.StartsWith("/", StringComparison.Ordinal) ||
            input.Path.Contains('?') || input.Path.Contains('#'))
            throw new ArgumentException("Path must begin with /, without query or fragment.", nameof(input));

        var query = (input.Query ?? Array.Empty<QueryParameter>())
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => "[" + CanonicalJson.Quote(x.Name) + "," + CanonicalJson.Quote(x.Value) + "]");
        string queryPreimage = "[" + string.Join(",", query) + "]";
        string bodyPreimage = input.BodyJson is null
            ? "[\"absent\"]"
            : "[\"json\"," + CanonicalJson.Canonicalize(input.BodyJson) + "]";

        var preimage = "[" + string.Join(",", new[]
        {
            CanonicalJson.Quote("idempotencykit.request.v1"),
            CanonicalJson.Quote(input.Method.ToUpperInvariant()),
            CanonicalJson.Quote(input.OperationId),
            CanonicalJson.Quote(input.Path),
            queryPreimage,
            bodyPreimage
        }) + "]";
        return CanonicalJson.HashV1(preimage);
    }

    public static string CanonicalizeJson(string bodyJson) => CanonicalJson.Canonicalize(bodyJson);
}
