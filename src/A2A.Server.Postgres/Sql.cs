using Npgsql;
using NpgsqlTypes;

namespace A2A.Server.Postgres;

/// <summary>Small helpers shared by the stores.</summary>
internal static class Sql
{
    public static NpgsqlParameter<string> TextParam(string value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter<long> Long(long value) => new() { TypedValue = value };

    public static NpgsqlParameter NullableLong(long? value) =>
        new() { Value = value is { } v ? v : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    /// <summary><paramref name="ticks"/> + <paramref name="span"/>, clamped to <see cref="DateTimeOffset.MaxValue"/> (an "infinite" span never overflows).</summary>
    public static long AddClamped(long ticks, TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return ticks;
        var max = DateTimeOffset.MaxValue.UtcTicks;
        return span.Ticks >= max - ticks ? max : ticks + span.Ticks;
    }
}
