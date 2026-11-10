namespace A2A.Server;

/// <summary>Shared checks for the configured time limits.</summary>
internal static class TimeLimits
{
    /// <summary>
    ///     True for a usable timer value. Zero, negative (including <see cref="Timeout.InfiniteTimeSpan" />) and huge values
    ///     (<see cref="int.MaxValue" /> milliseconds, about 24.8 days, or more) turn the limit off.
    /// </summary>
    public static bool IsTimeLimit(TimeSpan value)
    {
        return value > TimeSpan.Zero && value.TotalMilliseconds < int.MaxValue;
    }
}