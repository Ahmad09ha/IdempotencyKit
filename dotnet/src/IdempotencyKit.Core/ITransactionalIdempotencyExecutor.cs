using System.Data.Common;

namespace IdempotencyKit.Core;

/// <summary>
/// Provider-independent observable outcome of an atomic business operation.
/// These states have the same semantics in .NET and TypeScript.
/// </summary>
public enum TransactionalExecutionStatus
{
    Executed,
    Replayed,
    InProgress,
    FingerprintMismatch,
    Uncertain
}

/// <summary>
/// A replay response exists only for Executed and Replayed. No response means
/// the business callback must NOT be invoked by the caller on its own.
/// </summary>
public sealed record TransactionalExecutionResult(
    TransactionalExecutionStatus Status,
    StoredHttpResponse? Response = null);

/// <summary>
/// A relational transaction executor with provider-typed connection and
/// transaction. All business SQL inside the callback MUST enlist in these
/// exact objects. The executor owns BEGIN/COMMIT/ROLLBACK and returns success
/// only after business SQL + replay response are committed together.
/// </summary>
/// <remarks>
/// TConnection/TTransaction are deliberately provider-specific; the public
/// contract is neutral but it does not pretend Npgsql/SqlClient/SQLite use
/// identical query syntax or locking. This interface does NOT support external
/// API side effects, multiple databases or independent connections.
/// </remarks>
public interface ITransactionalIdempotencyExecutor<TConnection, TTransaction>
    where TConnection : DbConnection
    where TTransaction : DbTransaction
{
    Task<TransactionalExecutionResult> ExecuteAsync(
        string scopeKeyHash,
        string requestFingerprint,
        Func<TConnection, TTransaction, CancellationToken,
            Task<StoredHttpResponse>> operation,
        TimeSpan retention,
        CancellationToken cancellationToken = default);
}
