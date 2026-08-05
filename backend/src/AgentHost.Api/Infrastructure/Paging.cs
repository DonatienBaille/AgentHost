namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Pagination guards for every list endpoint. Client-supplied <c>take</c>/<c>skip</c> values are
/// never passed to SQL unclamped: an unbounded <c>?take=100000000</c> is a trivial memory/CPU
/// denial-of-service against both the API process and Postgres.
/// </summary>
public static class Paging
{
    /// <summary>Hard upper bound on any single page, regardless of what the client asks for.</summary>
    public const int MaxTake = 200;

    /// <summary>Clamps a requested page size into [1, <paramref name="max"/>].</summary>
    public static int ClampTake(int take, int max = MaxTake)
    {
        if (max > MaxTake) max = MaxTake;
        if (take < 1) return 1;
        return take > max ? max : take;
    }

    /// <summary>Floors a requested offset at 0 (a negative OFFSET is a Postgres error).</summary>
    public static int ClampSkip(int skip) => skip < 0 ? 0 : skip;
}
