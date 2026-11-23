namespace IdempotencyKit.AspNetCore;

/// <summary>
/// Build-time marker only: this project does not install middleware or protect requests.
/// </summary>
public static class AspNetCoreScaffoldInfo
{
    public const string Status = "scaffold-only";
}
