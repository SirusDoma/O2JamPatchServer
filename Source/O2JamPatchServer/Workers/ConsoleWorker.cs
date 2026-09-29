using System.CommandLine.Parsing;
using Encore.CLI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using O2JamPatchServer.CLI;

namespace O2JamPatchServer.Workers;

public class ConsoleWorker(ServerConsole console, CommandLineTaskProcessor processor, ILogger<ConsoleWorker> logger)
    : BackgroundService
{
    private const string Hint = "Type [help] to list the commands";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (console.IsInteractive)
            console.Placeholder = Hint;
        else
            using (logger.BeginScope("System"))
                logger.LogInformation("[?] " + Hint);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Console reads cannot be cancelled, the pending read is abandoned on shutdown
                string? line = await Task.Run(console.ReadLine, CancellationToken.None).WaitAsync(cancellationToken);
                if (line == null)
                    break;

                string[] args = CommandLineParser.SplitCommandLine(line).ToArray();
                if (args.Length == 0)
                    continue;

                if (args is ["help"] or ["?"])
                    args = ["--help"];

                try
                {
                    await processor.InvokeAsync(args);
                }
                catch (Exception ex)
                {
                    using (logger.BeginScope("System"))
                        logger.LogError(ex, "Failed to execute [{Command}]", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            console.Close();
        }
    }
}
