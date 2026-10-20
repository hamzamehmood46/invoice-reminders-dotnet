namespace Nudge.Engine;

/// <summary>A clock that only moves when told to, for demos and tests.</summary>
public sealed class SimulatedClock : TimeProvider
{
    private DateTimeOffset _now;

    public SimulatedClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
