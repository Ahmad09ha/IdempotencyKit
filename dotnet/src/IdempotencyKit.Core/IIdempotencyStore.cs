namespace IdempotencyKit.Core;

/// <summary>Result of an atomic claim attempt against a shared store.</summary>
public enum IdempotencyClaimStatus
{
    Acquired,
    InProgress,
    Completed,
    FingerprintMismatch,
    Uncertain
}

/// <summary>
/// The owner token is returned only for a new claim; the stored response only
/// for a completed, matching request. Callers must authorize before invoking.
/// </summary>
public sealed record IdempotencyClaimResult(
    IdempotencyClaimStatus Status,
    Guid? OwnerToken = null,
    StoredHttpResponse? Response = null);

/// <summary>
/// Standard-mode storage only. It does NOT make application business writes
/// atomic with result recording; see spec/GUARANTEES.md.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically create a processing claim or inspect an existing one.
    /// An expired processing lease is moved to UNCERTAIN and is never reclaimed
    /// automatically, because its business effect may already have committed.
    /// </summary>
    Task<IdempotencyClaimResult> TryClaimAsync(
        string scopeKeyHash, string requestFingerprint,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete only if the matching processing record is still owned by the
    /// token and its lease is unexpired. Only bounded 2xx JSON is supported.
    /// </summary>
    Task<bool> TryCompleteAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        StoredHttpResponse response, TimeSpan retention,
        CancellationToken cancellationToken = default);

    /// <summary>Extend an unexpired lease only for its current owner.</summary>
    Task<bool> TryExtendLeaseAsync(
        string scopeKeyHash, Guid ownerToken, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Preserve an unknown outcome under the current owner token. This operation
    /// is safe even after a lease expires if this owner still owns the processing
    /// row; it never permits another execution.
    /// </summary>
    Task<bool> TryMarkUncertainAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete at most batchSize completed records beyond their retention window.
    /// Processing and uncertain records must never be purged automatically.
    /// </summary>
    Task<int> PurgeExpiredCompletedAsync(
        int batchSize, CancellationToken cancellationToken = default);
}
