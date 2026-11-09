using Npgsql;
using NpgsqlTypes;

namespace A2A.Client.Postgres;

/// <summary>Small helpers for the store's commands.</summary>
internal static class Sql
{
    public static NpgsqlParameter<string> TextParam(string value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter<long> Long(long value) => new() { TypedValue = value };

    public static NpgsqlParameter<int> Int(int value) => new() { TypedValue = value };

    public static NpgsqlParameter<bool> Bool(bool value) => new() { TypedValue = value };

    public static NpgsqlParameter<Guid> Uuid(Guid value) => new() { TypedValue = value };

    public static NpgsqlParameter<byte[]> Bytes(byte[] value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Bytea };

    public static NpgsqlParameter NullableText(string? value) =>
        new() { Value = value is null ? DBNull.Value : value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter NullableLong(long? value) =>
        new() { Value = value is { } v ? v : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    public static NpgsqlParameter NullableInt(int? value) =>
        new() { Value = value is { } v ? v : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    public static NpgsqlParameter NullableBytes(byte[]? value) =>
        new() { Value = value is null ? DBNull.Value : value, NpgsqlDbType = NpgsqlDbType.Bytea };

    /// <summary>
    /// A span as ticks for SQL arithmetic: zero for a zero or negative span (<see cref="Timeout.InfiniteTimeSpan"/> too), at most
    /// 100 years so adding it to a timestamp never overflows.
    /// </summary>
    public static NpgsqlParameter<long> Span(TimeSpan span) =>
        Long(span <= TimeSpan.Zero ? 0 : Math.Min(span.Ticks, TimeSpan.FromDays(36525).Ticks));
}

/// <summary>
/// The clock that decides deadlines. With <see cref="PostgresClientOutboxOptions.UseDatabaseClock"/> (the default) it is the database's
/// clock: a command passes a NULL "now" parameter and the SQL uses <see cref="Now"/>, which falls back to <c>statement_timestamp()</c>.
/// Otherwise the command passes the <see cref="TimeProvider"/>'s ticks. A small copy of the server package's helper, which this package must not reference.
/// </summary>
internal sealed class DbClock(TimeProvider? time, PostgresClientOutboxOptions? options)
{
    /// <summary>The database's time as .NET UTC ticks (microsecond precision).</summary>
    public const string NowTicksSql = "((extract(epoch from statement_timestamp()) * 10000000)::bigint + 621355968000000000)";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly bool _usesDatabase = (options ?? new()).UseDatabaseClock;

    /// <summary>A "now" parameter for <see cref="Now"/>: null (the database decides) or the instance's ticks.</summary>
    public NpgsqlParameter NowParam() => Sql.NullableLong(_usesDatabase ? null : _time.GetUtcNow().UtcTicks);

    /// <summary>SQL for "now" in ticks: positional parameter <paramref name="parameter"/>, or the database clock when it is NULL.</summary>
    public static string Now(int parameter) => $"COALESCE(${parameter}::bigint, {NowTicksSql})";
}
