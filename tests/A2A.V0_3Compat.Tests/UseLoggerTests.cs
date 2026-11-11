using A2A.Client.V0_3Compat;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.V0_3Compat.Tests;

/// <summary>
///     <see cref="V03CompatClientFactory.UseLogger" /> sets a process-wide logger (the first call wins), so this test only
///     checks the argument and that a call is accepted. The warnings themselves are tested with explicit loggers in
///     <c>CoreTests</c>, which do not depend on this setting.
/// </summary>
public class UseLoggerTests
{
    [Fact]
    public void UseLogger_refuses_null() =>
        Assert.Throws<ArgumentNullException>(() => V03CompatClientFactory.UseLogger(null!));

    [Fact]
    public void UseLogger_accepts_a_logger_and_a_second_call() =>
        Assert.Null(Record.Exception(() =>
        {
            V03CompatClientFactory.UseLogger(NullLogger.Instance);
            V03CompatClientFactory.UseLogger(NullLogger.Instance); // the first call wins; a later one is not an error
        }));
}
