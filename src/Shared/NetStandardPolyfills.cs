// Polyfills for netstandard2.0. Compiled into A2A.Protocol and A2A.Client for netstandard2.0 only.
// Every member keeps the meaning of the net8+ API it stands in for. All types are internal.
#if NETSTANDARD2_0
#nullable enable
#pragma warning disable CA1812 // Internal types may be unused in one of the two assemblies.

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace System.Diagnostics.CodeAnalysis
{
    // PolySharp skips this type because it is internal in a referenced package, so it is written here.
    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0003,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None,
    }

    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
        AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method |
        AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct,
        Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) => MemberTypes = memberTypes;

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }
}

namespace System.Buffers
{
    internal delegate void SpanAction<T, in TArg>(Span<T> span, TArg arg);

    internal sealed class ArrayBufferWriter<T> : IBufferWriter<T>
    {
        private T[] _buffer;
        private int _index;

        public ArrayBufferWriter(int initialCapacity = 256)
        {
            if (initialCapacity <= 0)
            {
                throw new ArgumentException("Capacity must be positive.", nameof(initialCapacity));
            }

            _buffer = new T[initialCapacity];
        }

        public ReadOnlyMemory<T> WrittenMemory => _buffer.AsMemory(0, _index);

        public ReadOnlySpan<T> WrittenSpan => _buffer.AsSpan(0, _index);

        public int WrittenCount => _index;

        public void Clear()
        {
            _buffer.AsSpan(0, _index).Clear();
            _index = 0;
        }

        public void Advance(int count)
        {
            if (count < 0 || _index > _buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _index += count;
        }

        public Memory<T> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsMemory(_index);
        }

        public Span<T> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsSpan(_index);
        }

        private void Grow(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            if (sizeHint > _buffer.Length - _index)
            {
                var newSize = checked(_buffer.Length + Math.Max(sizeHint, _buffer.Length));
                Array.Resize(ref _buffer, newSize);
            }
        }
    }
}

namespace System.Numerics
{
    internal static class BitOperations
    {
        public static int PopCount(uint value)
        {
            var count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }

            return count;
        }
    }
}

namespace System.Text.Unicode
{
    internal static class Utf8
    {
        // Strict check: overlong forms, surrogates and truncated sequences are invalid.
        public static bool IsValid(ReadOnlySpan<byte> value)
        {
            try
            {
                _ = Strict.GetCharCount(value.ToArray());
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }
}

namespace System.Text
{
    // The subset of System.Text.Rune that the SDK uses. Same results as net8+ for valid scalar values;
    // an unpaired surrogate in a string becomes U+FFFD, as on net8+.
    internal readonly struct Rune : IEquatable<Rune>
    {
        private readonly int _value;

        public Rune(int value)
        {
            if (value is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _value = value;
        }

        public int Value => _value;

        public static bool IsWhiteSpace(Rune value) => value._value <= 0xFFFF && char.IsWhiteSpace((char)value._value);

        public static UnicodeCategory GetUnicodeCategory(Rune value) =>
            value._value <= 0xFFFF
                ? CharUnicodeInfo.GetUnicodeCategory((char)value._value)
                : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(value._value), 0);

        public static bool IsLetterOrDigit(Rune value) =>
            GetUnicodeCategory(value) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber;

        public static Rune ToLowerInvariant(Rune value)
        {
            if (value._value <= 0xFFFF)
            {
                return new Rune(char.ToLowerInvariant((char)value._value));
            }

            var lower = char.ConvertFromUtf32(value._value).ToLowerInvariant();
            return new Rune(char.ConvertToUtf32(lower, 0));
        }

        public override string ToString() => char.ConvertFromUtf32(_value);

        public bool Equals(Rune other) => _value == other._value;

        public override bool Equals(object? obj) => obj is Rune other && Equals(other);

        public override int GetHashCode() => _value;
    }

    internal static class RuneExtensions
    {
        public static IEnumerable<Rune> EnumerateRunes(this string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    yield return new Rune(char.ConvertToUtf32(c, value[i + 1]));
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    yield return new Rune(0xFFFD);
                }
                else
                {
                    yield return new Rune(c);
                }
            }
        }
    }
}

namespace System.Security.Cryptography
{
    internal static class CryptographicOperations
    {
        // Compares every byte, so the time does not depend on where the first difference is.
        // Different lengths return false at once, as on net8+.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < left.Length; i++)
            {
                difference |= left[i] ^ right[i];
            }

            return difference == 0;
        }
    }
}

namespace System
{
    internal static class NetStandardPolyfills
    {
        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }
            }
        }

        extension(ObjectDisposedException)
        {
            public static void ThrowIf(bool condition, object instance)
            {
                if (condition)
                {
                    throw new ObjectDisposedException(instance?.GetType().FullName);
                }
            }
        }

        extension(ArgumentException)
        {
            public static void ThrowIfNullOrEmpty(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }

                if (argument.Length == 0)
                {
                    throw new ArgumentException("The value cannot be an empty string.", paramName);
                }
            }

            public static void ThrowIfNullOrWhiteSpace(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }

                if (string.IsNullOrWhiteSpace(argument))
                {
                    throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
                }
            }
        }

        extension(ArgumentOutOfRangeException)
        {
            public static void ThrowIfLessThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be greater than or equal to {other}.");
                }
            }

            public static void ThrowIfGreaterThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) > 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be less than or equal to {other}.");
                }
            }

            public static void ThrowIfLessThanOrEqual<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be greater than {other}.");
                }
            }

            public static void ThrowIfGreaterThanOrEqual<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) >= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be less than {other}.");
                }
            }

            public static void ThrowIfNegative<T>(T value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : struct, IComparable<T>
            {
                if (value.CompareTo(default) < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be non-negative.");
                }
            }

            public static void ThrowIfNegativeOrZero<T>(T value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : struct, IComparable<T>
            {
                if (value.CompareTo(default) <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be positive.");
                }
            }
        }

        extension(SHA256)
        {
            public static int HashSizeInBytes => 32;

            public static byte[] HashData(byte[] source)
            {
                ArgumentNullException.ThrowIfNull(source);
                using var sha = SHA256.Create();
                return sha.ComputeHash(source);
            }

            public static byte[] HashData(ReadOnlySpan<byte> source)
            {
                using var sha = SHA256.Create();
                return sha.ComputeHash(source.ToArray());
            }

            public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination)
            {
                if (destination.Length < 32)
                {
                    throw new ArgumentException("Destination is too short.", nameof(destination));
                }

                var hash = SHA256.HashData(source);
                hash.CopyTo(destination);
                return hash.Length;
            }
        }

        extension(HMACSHA256)
        {
            public static byte[] HashData(byte[] key, byte[] source)
            {
                ArgumentNullException.ThrowIfNull(key);
                ArgumentNullException.ThrowIfNull(source);
                using var hmac = new HMACSHA256(key);
                return hmac.ComputeHash(source);
            }

            public static byte[] HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source)
            {
                using var hmac = new HMACSHA256(key.ToArray());
                return hmac.ComputeHash(source.ToArray());
            }

            public static int HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination)
            {
                if (destination.Length < 32)
                {
                    throw new ArgumentException("Destination is too short.", nameof(destination));
                }

                var hash = HMACSHA256.HashData(key, source);
                hash.CopyTo(destination);
                return hash.Length;
            }
        }

        extension(Convert)
        {
            public static string ToBase64String(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes.ToArray());

            public static string ToHexString(byte[] inArray)
            {
                ArgumentNullException.ThrowIfNull(inArray);
                return Convert.ToHexString((ReadOnlySpan<byte>)inArray);
            }

            public static string ToHexString(ReadOnlySpan<byte> bytes)
            {
                const string digits = "0123456789ABCDEF";
                var chars = new char[bytes.Length * 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    chars[2 * i] = digits[bytes[i] >> 4];
                    chars[(2 * i) + 1] = digits[bytes[i] & 0xF];
                }

                return new string(chars);
            }

            // Same contract as net8+: false when the text is not valid base64 or the destination is too small.
            public static bool TryFromBase64String(string s, Span<byte> bytes, out int bytesWritten)
            {
                ArgumentNullException.ThrowIfNull(s);
                bytesWritten = 0;
                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(s);
                }
                catch (FormatException)
                {
                    return false;
                }

                if (decoded.Length > bytes.Length)
                {
                    return false;
                }

                decoded.CopyTo(bytes);
                bytesWritten = decoded.Length;
                return true;
            }
        }

        extension<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            public void Deconstruct(out TKey key, out TValue value)
            {
                key = pair.Key;
                value = pair.Value;
            }
        }

        extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
            where TKey : notnull
        {
            public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
            {
                if (dictionary.TryGetValue(key, out value))
                {
                    dictionary.Remove(key);
                    return true;
                }

                value = default;
                return false;
            }

            public bool TryAdd(TKey key, TValue value)
            {
                if (dictionary.ContainsKey(key)) return false;
                dictionary.Add(key, value);
                return true;
            }
        }

        extension(double)
        {
            public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        }

        extension(Enum)
        {
            public static TEnum[] GetValues<TEnum>()
                where TEnum : struct, Enum
                => (TEnum[])Enum.GetValues(typeof(TEnum));

            public static bool IsDefined<TEnum>(TEnum value)
                where TEnum : struct, Enum
                => Enum.IsDefined(typeof(TEnum), value);
        }

        extension(string)
        {
            public static string Create(IFormatProvider? provider, FormattableString text)
            {
                ArgumentNullException.ThrowIfNull(text);
                return text.ToString(provider);
            }

            public static string Create<TState>(int length, TState state, SpanAction<char, TState> action)
            {
                ArgumentNullException.ThrowIfNull(action);
                if (length < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(length));
                }

                if (length == 0)
                {
                    return string.Empty;
                }

                var buffer = new char[length];
                action(buffer, state);
                return new string(buffer);
            }
        }

        extension(char)
        {
            public static bool IsAscii(char c) => c < 0x80;

            public static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

            public static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

            public static bool IsAsciiLetterLower(char c) => c is >= 'a' and <= 'z';

            public static bool IsAsciiLetterUpper(char c) => c is >= 'A' and <= 'Z';

            public static bool IsAsciiLetterOrDigit(char c) => char.IsAsciiLetter(c) || char.IsAsciiDigit(c);

            public static bool IsAsciiHexDigit(char c) => char.IsAsciiDigit(c) || (uint)((c | 0x20) - 'a') <= 'f' - 'a';
        }

        extension(ValueTask)
        {
            public static ValueTask CompletedTask => default;

            public static ValueTask<TResult> FromResult<TResult>(TResult result) => new(result);
        }

        extension(Encoding encoding)
        {
            public string GetString(ReadOnlySpan<byte> bytes) => encoding.GetString(bytes.ToArray());

            public int GetBytes(string s, Span<byte> bytes)
            {
                var encoded = encoding.GetBytes(s);
                if (encoded.Length > bytes.Length)
                {
                    throw new ArgumentException("Destination is too short.", nameof(bytes));
                }

                encoded.CopyTo(bytes);
                return encoded.Length;
            }
        }

        extension(string)
        {
            public static string Join(char separator, IEnumerable<string?> values) => string.Join(separator.ToString(), values);
        }

        extension<T>(IEnumerable<T> source)
        {
            public IOrderedEnumerable<T> Order(IComparer<T>? comparer) => source.OrderBy(static x => x, comparer);
        }

        extension(Task)
        {
            // Waits on the given time provider, so a test clock can drive it.
            public static async Task Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(timeProvider);
                if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(delay));
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (delay == TimeSpan.Zero)
                {
                    return;
                }

                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = timeProvider.CreateTimer(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), done, delay, Timeout.InfiniteTimeSpan);
                using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), done))
                {
                    await done.Task.ConfigureAwait(false);
                }
            }
        }

        extension(long)
        {
            public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out long result) =>
                long.TryParse(s.ToString(), style, provider, out result);
        }

        extension(int)
        {
            public static int Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) =>
                int.Parse(s.ToString(), style, provider);
        }

        extension(IncrementalHash hash)
        {
            public void AppendData(ReadOnlySpan<byte> data) => hash.AppendData(data.ToArray());
        }

        extension<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary)
            where TKey : notnull
        {
            // Removes only when the stored value equals the given one, in one atomic step.
            public bool TryRemove(KeyValuePair<TKey, TValue> item) =>
                ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(item);
        }

        extension(Random)
        {
            public static Random Shared => SharedRandom.Instance;
        }

        extension(string value)
        {
            public bool Contains(char c) => value.IndexOf(c) >= 0;

            public bool Contains(char c, StringComparison comparison) => value.IndexOf(c.ToString(), comparison) >= 0;

            public bool StartsWith(char c) => value.Length > 0 && value[0] == c;

            public bool EndsWith(char c) => value.Length > 0 && value[value.Length - 1] == c;

            public bool Contains(string other, StringComparison comparison) => value.IndexOf(other, comparison) >= 0;

            public string Replace(string oldValue, string? newValue, StringComparison comparison)
            {
                ArgumentNullException.ThrowIfNull(oldValue);
                if (oldValue.Length == 0)
                {
                    throw new ArgumentException("The value cannot be an empty string.", nameof(oldValue));
                }

                var index = value.IndexOf(oldValue, comparison);
                if (index < 0)
                {
                    return value;
                }

                var builder = new System.Text.StringBuilder();
                var start = 0;
                while (index >= 0)
                {
                    builder.Append(value, start, index - start).Append(newValue);
                    start = index + oldValue.Length;
                    index = value.IndexOf(oldValue, start, comparison);
                }

                return builder.Append(value, start, value.Length - start).ToString();
            }

            public string[] Split(char separator, StringSplitOptions options = StringSplitOptions.None) =>
                value.Split(new[] { separator }, options);

            public string[] Split(char separator, int count, StringSplitOptions options = StringSplitOptions.None) =>
                value.Split(new[] { separator }, count, options);
        }

        // HttpContent: the net8+ overloads take a token. These throw when the token is already cancelled
        // and stop waiting when it fires.
        extension(HttpContent content)
        {
            public Task<string> ReadAsStringAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return content.ReadAsStringAsync().WaitAsync(cancellationToken);
            }

            public Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return content.ReadAsByteArrayAsync().WaitAsync(cancellationToken);
            }

            public Task<Stream> ReadAsStreamAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return content.ReadAsStreamAsync().WaitAsync(cancellationToken);
            }
        }

        extension(Task task)
        {
            public Task WaitAsync(CancellationToken cancellationToken)
            {
                if (task.IsCompleted || !cancellationToken.CanBeCanceled)
                {
                    return task;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return WaitCore(task, cancellationToken);
            }
        }

        extension<T>(Task<T> task)
        {
            public Task<T> WaitAsync(CancellationToken cancellationToken)
            {
                if (task.IsCompleted || !cancellationToken.CanBeCanceled)
                {
                    return task;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return WaitCore(task, cancellationToken);
            }
        }

        extension(Stream stream)
        {
            // Reads at least minimumBytes unless the stream ends first (then throws or returns what it read).
            public int ReadAtLeast(Span<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true)
            {
                if (minimumBytes < 0 || buffer.Length < minimumBytes)
                {
                    throw new ArgumentOutOfRangeException(nameof(minimumBytes));
                }

                var temp = new byte[buffer.Length];
                var total = 0;
                while (total < minimumBytes)
                {
                    var read = stream.Read(temp, total, buffer.Length - total);
                    if (read == 0)
                    {
                        if (throwOnEndOfStream)
                        {
                            throw new EndOfStreamException();
                        }

                        break;
                    }

                    total += read;
                }

                temp.AsSpan(0, total).CopyTo(buffer);
                return total;
            }
        }

        extension(CancellationTokenSource source)
        {
            public Task CancelAsync()
            {
                source.Cancel();
                return Task.CompletedTask;
            }
        }

        // .NET Framework streams may ignore the token. This stops waiting when it fires;
        // the caller then disposes the stream.
        extension(Stream stream)
        {
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!cancellationToken.CanBeCanceled)
                {
                    return MemoryMarshal.TryGetArray<byte>(buffer, out var segment) && segment.Array is not null
                        ? new ValueTask<int>(stream.ReadAsync(segment.Array, segment.Offset, segment.Count, CancellationToken.None))
                        : new ValueTask<int>(ReadViaCopyAsync(stream, buffer, cancellationToken));
                }

                return new ValueTask<int>(ReadViaCopyAsync(stream, buffer, cancellationToken));
            }
        }

        // A read that ignores the token can finish after we stop waiting, so it gets a private array, never a pooled
        // or caller-owned one. Bytes are copied out only when the read finished before cancellation.
        private static async Task<int> ReadViaCopyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var own = new byte[buffer.Length];
            var read = await stream.ReadAsync(own, 0, own.Length, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            own.AsMemory(0, read).CopyTo(buffer);
            return read;
        }

        private static async Task WaitCore(Task task, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                {
                    // Observe a later failure so it does not surface as unobserved.
                    _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            await task.ConfigureAwait(false);
        }

        private static async Task<T> WaitCore<T>(Task<T> task, CancellationToken cancellationToken)
        {
            await WaitCore((Task)task, cancellationToken).ConfigureAwait(false);
            return task.Result;
        }
    }

    internal static class HmacPolyfills
    {
        extension(HMACSHA256)
        {
            public static int HashSizeInBytes => 32;
        }
    }

    internal sealed class SharedRandom : Random
    {
        public static readonly SharedRandom Instance = new();

        [ThreadStatic]
        private static Random? t_random;

        private static Random Local => t_random ??= new Random(Guid.NewGuid().GetHashCode());

        public override int Next() => Local.Next();

        public override int Next(int maxValue) => Local.Next(maxValue);

        public override int Next(int minValue, int maxValue) => Local.Next(minValue, maxValue);

        public override double NextDouble() => Local.NextDouble();

        public override void NextBytes(byte[] buffer) => Local.NextBytes(buffer);
    }
}
#endif
