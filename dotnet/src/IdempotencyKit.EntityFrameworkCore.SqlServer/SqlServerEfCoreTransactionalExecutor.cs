using IdempotencyKit.Core;
using IdempotencyKit.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace IdempotencyKit.EntityFrameworkCore.SqlServer;

/// <summary>
/// Explicit EF Core bridge to the SqlServerTransactionalExecutor. Constructs
/// a NEW DbContext using the executor's existing SqlConnection, enlists it
/// in the SAME SqlTransaction, and never opens a second database connection.
/// </summary>
/// <remarks>
/// <para>
/// The factory must configure a fresh, provider-backed DbContext with
/// <c>new DbContextOptionsBuilder&lt;TContext&gt;().UseMicrosoft.Data.SqlClient(connection).Options</c>.
/// The bridge verifies the actual connection object before invoking the callback.
/// </para>
/// <para>
/// Call <c>SaveChangesAsync</c> within the callback before reading generated
/// keys or building the response, if needed. Remaining tracked changes are
/// saved by this bridge before the response and transaction commit.
/// All business SQL must use this DbContext or the same supplied transaction.
/// </para>
/// <para>
/// Never wrap this executor in Standard-mode HTTP idempotency middleware for
/// the same scoped key, and never perform external irreversible side effects
/// assuming SQL Server can roll them back.
/// </para>
/// </remarks>
public sealed class SqlServerEfCoreTransactionalExecutor<TContext>
    where TContext : DbContext
{
    private readonly SqlServerTransactionalExecutor _executor;
    private readonly Func<SqlConnection, TContext> _createContext;

    public SqlServerEfCoreTransactionalExecutor(
        SqlServerTransactionalExecutor executor,
        Func<SqlConnection, TContext> createContext)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
    }

    /// <summary>
    /// Execute business writes and result persistence in one SQL Server
    /// transaction. The operation is invoked only for a newly acquired key.
    /// The DbContext is created/disposed by this bridge; do not cache it.
    /// </summary>
    public Task<TransactionalExecutionResult> ExecuteAsync(
        string scopeKeyHash,
        string requestFingerprint,
        Func<TContext, CancellationToken, Task<StoredHttpResponse>> operation,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return _executor.ExecuteAsync(
            scopeKeyHash, requestFingerprint,
            async (connection, transaction, ct) =>
            {
                await using var context = _createContext(connection)
                    ?? throw new InvalidOperationException("DbContext factory returned null.");

                // Fail closed before domain effects if the factory disregards
                // the transaction's connection. A DbContext using a second pool
                // would silently break our atomicity contract.
                if (!ReferenceEquals(context.Database.GetDbConnection(), connection))
                    throw new InvalidOperationException(
                        "EF Core DbContext must use the exact provided SqlConnection.");

                if (context.Database.CurrentTransaction is not null)
                    throw new InvalidOperationException(
                        "EF Core DbContext must not already have a transaction.");

                await context.Database.UseTransactionAsync(transaction, ct);
                var result = await operation(context, ct);

                // Include remaining tracked entities in the very same
                // transaction. SaveChangesAsync may also be called explicitly
                // inside operation when generated identifiers are needed.
                await context.SaveChangesAsync(ct);
                return result;
            },
            retention, cancellationToken);
    }
}
