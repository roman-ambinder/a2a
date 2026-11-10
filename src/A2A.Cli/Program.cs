using System.Text;

namespace A2A.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No console attached: keep the defaults.
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // The first Ctrl+C stops the work cleanly. A second one ends the process the usual way.
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var env = new CliEnvironment(Console.Out, Console.Error, Console.In, Environment.GetEnvironmentVariable);
            return await CliApp.RunAsync(args, env, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}