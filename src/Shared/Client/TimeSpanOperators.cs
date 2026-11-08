// netstandard2.0 only, compiled into A2A.Client only: with a copy in A2A.Protocol too, the compiler reports the operators as ambiguous.
#if NETSTANDARD2_0
#nullable enable

namespace System
{
    internal static class TimeSpanOperatorPolyfills
    {
        extension(TimeSpan)
        {
            public static TimeSpan operator *(TimeSpan timeSpan, double factor) =>
                TimeSpan.FromTicks(checked((long)(timeSpan.Ticks * factor)));

            public static TimeSpan operator *(double factor, TimeSpan timeSpan) =>
                TimeSpan.FromTicks(checked((long)(timeSpan.Ticks * factor)));

            public static TimeSpan operator /(TimeSpan timeSpan, double divisor) =>
                TimeSpan.FromTicks(checked((long)(timeSpan.Ticks / divisor)));
        }
    }
}
#endif
