namespace IdempotencyKit.Core;

/// <summary>
/// Describes a stored operation's lifecycle. This scaffold does not implement the transitions.
/// </summary>
public enum IdempotencyRecordState
{
    Processing = 0,
    Completed = 1,
    Uncertain = 2
}

public static class IdempotencyRecordStateExtensions
{
    /// <summary>
    /// Indicates a completed record; replay also requires matching fingerprint and authorization.
    /// </summary>
    public static bool IsCompleted(this IdempotencyRecordState state) =>
        state == IdempotencyRecordState.Completed;
}
