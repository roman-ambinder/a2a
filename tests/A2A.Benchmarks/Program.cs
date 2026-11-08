using BenchmarkDotNet.Running;

namespace A2A.Benchmarks;

/// <summary>Entry point. Pass <c>--job short</c> for the short-run job used for the committed baseline.</summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
