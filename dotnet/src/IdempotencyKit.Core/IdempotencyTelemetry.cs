using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IdempotencyKit.Core;

/// <summary>Built-in, privacy-safe metrics for IdempotencyKit HTTP adapter stages.</summary>
public static class IdempotencyTelemetry
{
    /// <summary>Meter name to subscribe to from a metrics collector.</summary>
    public const string MeterName = "IdempotencyKit";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Stages = Meter.CreateCounter<long>(
        "idempotencykit.stage.count", description: "Number of idempotency adapter stages.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "idempotencykit.stage.duration", unit: "ms",
        description: "Elapsed time spent in idempotency adapter stages.");

    /// <summary>Measure an HTTP Standard-mode store claim without changing its outcome.</summary>
    public static Task<IdempotencyClaimResult> ObserveClaimAsync(
        Func<Task<IdempotencyClaimResult>> claim) =>
        ObserveAsync("standard.claim", claim, result => result.Status switch
        {
            IdempotencyClaimStatus.Acquired when result.OwnerToken is not null => "acquired",
            IdempotencyClaimStatus.Completed when result.Response is not null => "replayed",
            IdempotencyClaimStatus.InProgress => "in_progress",
            IdempotencyClaimStatus.FingerprintMismatch => "fingerprint_mismatch",
            IdempotencyClaimStatus.Uncertain => "uncertain",
            _ => "error"
        });

    /// <summary>Measure an HTTP Transactional-mode executor call without changing its outcome.</summary>
    public static Task<TransactionalExecutionResult> ObserveTransactionAsync(
        Func<Task<TransactionalExecutionResult>> execute) =>
        ObserveAsync("transactional.execute", execute, result => result.Status switch
        {
            TransactionalExecutionStatus.Executed when result.Response is not null => "executed",
            TransactionalExecutionStatus.Replayed when result.Response is not null => "replayed",
            TransactionalExecutionStatus.InProgress => "in_progress",
            TransactionalExecutionStatus.FingerprintMismatch => "fingerprint_mismatch",
            TransactionalExecutionStatus.Uncertain => "uncertain",
            _ => "error"
        });

    /// <summary>Record a newly claimed Standard-mode HTTP handler's persistence outcome.</summary>
    public static void RecordStandardExecution(long startedAt, bool persisted) =>
        Record("standard.execute", persisted ? "completed" : "unconfirmed", startedAt);

    private static async Task<T> ObserveAsync<T>(
        string stage, Func<Task<T>> action, Func<T, string> classify) where T : class
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            var result = await action().ConfigureAwait(false);
            if (result is not null) outcome = classify(result);
            return result!;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            Record(stage, outcome, startedAt);
        }
    }

    private static void Record(string stage, string outcome, long startedAt)
    {
        if (!Stages.Enabled && !Duration.Enabled) return;
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var tags = new TagList
        {
            { "idempotency.stage", stage },
            { "idempotency.outcome", outcome }
        };
        // Synchronous MeterListener callbacks belong to the application. A
        // misconfigured collector cannot affect storage or the user response.
        try { Stages.Add(1, tags); }
        catch (Exception) { }
        try { Duration.Record(elapsed, tags); }
        catch (Exception) { }
    }
}
