namespace IdempotencyKit.Core;

/// <summary>Deterministic v1 scope identity; does not claim or persist requests.</summary>
public static class RequestIdentityHasher
{
    public static string ComputeScopeKey(IdempotencyRequestScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(scope.Namespace, nameof(scope.Namespace));
        Require(scope.OperationId, nameof(scope.OperationId));
        Require(scope.PrincipalId, nameof(scope.PrincipalId));

        var key = scope.ClientKey;
        if (key is null || key.Length is < 1 or > 255)
            throw new ArgumentException("Idempotency key must be 1-255 ASCII characters.");
        foreach (char c in key)
        {
            bool allowed = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-' or '_' or '.' or ':';
            if (!allowed) throw new ArgumentException("Invalid idempotency key character.");
        }

        string preimage = "[" + string.Join(",", new[]
        {
            CanonicalJson.Quote("idempotencykit.scope.v1"),
            CanonicalJson.Quote(scope.Namespace),
            CanonicalJson.Quote(scope.OperationId),
            scope.TenantId is null ? "null" : CanonicalJson.Quote(scope.TenantId),
            CanonicalJson.Quote(scope.PrincipalId),
            CanonicalJson.Quote(key)
        }) + "]";
        return CanonicalJson.HashV1(preimage);
    }

    private static void Require(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Scope field cannot be empty.", name);
    }
}
