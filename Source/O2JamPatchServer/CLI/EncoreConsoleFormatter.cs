using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Encore.CLI;

public class EncoreConsoleFormatterOptions : ConsoleFormatterOptions
{
    public int CategoryWidth { get; init; } = 16;
    public int StateWidth { get; init; } = 21;
    public ConsoleColor CategoryColor { get; init; } = ConsoleColor.Cyan;
    public ConsoleColor StateColor { get; init; } = ConsoleColor.Yellow;
}

public class EncoreConsoleFormatter : ConsoleFormatter
{
    private readonly EncoreConsoleFormatterOptions _options;

    public EncoreConsoleFormatter(IOptionsMonitor<EncoreConsoleFormatterOptions> options)
        : base("EncoreLoggerFormatter")
    {
        _options = options.CurrentValue;
    }

    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (string.IsNullOrEmpty(message))
            return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");

        string levelText = GetColoredLogLevel(logEntry.LogLevel);


        string category = PadAndColorText(logEntry.Category[(logEntry.Category.LastIndexOf('.') + 1)..], _options.CategoryWidth, _options.CategoryColor);

        string state = GetFormattedState(scopeProvider, _options.StateWidth, _options.StateColor);

        textWriter.WriteLine($"{timestamp} {levelText} [ {category} ] [ {state} ]: {message}");

        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }

    private static string GetColoredLogLevel(LogLevel logLevel)
    {
        var levelText = GetLogLevelText(logLevel);
        var colorCode = GetLogLevelColorCode(logLevel);

        return $"\u001b[{colorCode}m{levelText}\u001b[0m";
    }

    private static string GetLogLevelText(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.None        => string.Empty,
            LogLevel.Trace       => "TRACE",
            LogLevel.Debug       => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "FATAL",
            _                    => logLevel.ToString().ToUpper()
        };
    }

    private static string GetLogLevelColorCode(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace       => "37", // Gray
            LogLevel.Debug       => "37", // Gray
            LogLevel.Information => "32", // Green
            LogLevel.Warning     => "33", // Yellow
            LogLevel.Error       => "31", // Red
            LogLevel.Critical    => "35", // Magenta
            _                    => "37"  // White
        };
    }

    private static string PadAndColorText(string text, int padding, ConsoleColor color)
    {
        string paddedText = text.Length > padding ? text : text.PadRight(padding);
        return paddedText.WithConsoleColor(color);
    }

    private static string GetFormattedState(IExternalScopeProvider? scopeProvider, int padding, ConsoleColor color)
    {
        // Scopes are reported outermost first, only the innermost one is printed
        string state = string.Empty;
        scopeProvider?.ForEachScope<object?>((scope, _) =>
        {
            string? name = scope?.ToString();
            if (!string.IsNullOrEmpty(name))
                state = name;
        }, null);

        return PadAndColorText(state, padding, color);
    }
}
