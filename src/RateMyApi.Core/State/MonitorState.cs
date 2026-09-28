namespace RateMyApi.Core.State;

public enum MonitorState
{
    Ok,
    Degraded,
    Down,
    Resolved,
}

public static class MonitorStateNames
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Down = "down";
    public const string Resolved = "resolved";

    /// <summary>The lowercase name stored in <c>incidents.state</c> and used on the wire.</summary>
    public static string ToName(this MonitorState state) => state switch
    {
        MonitorState.Ok => Ok,
        MonitorState.Degraded => Degraded,
        MonitorState.Down => Down,
        MonitorState.Resolved => Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static MonitorState Parse(string name) => name switch
    {
        Ok => MonitorState.Ok,
        Degraded => MonitorState.Degraded,
        Down => MonitorState.Down,
        Resolved => MonitorState.Resolved,
        _ => throw new FormatException($"Unknown monitor state '{name}'."),
    };

    /// <summary>Degraded and down are the states with an open (unresolved) incident.</summary>
    public static bool IsOpen(this MonitorState state) => state is MonitorState.Degraded or MonitorState.Down;
}
