using Microsoft.Extensions.Configuration;

namespace O2JamPatchServer.CLI;

public class ConfigFile(IConfiguration configuration)
{
    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "config.ini");

    public void Set(string section, string key, string value)
    {
        string text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
        string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newLine).ToList();
        string line = $"{key}={value}";

        int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            lines.InsertRange(lines.Count - 1, [$"[{section}]", line]);
        }
        else
        {
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0)
                end = lines.Count;

            int existing = lines.FindIndex(start + 1, end - start - 1, l => IsKey(l, key));
            if (existing >= 0)
            {
                lines[existing] = line;
            }
            else
            {
                while (end > start + 1 && string.IsNullOrWhiteSpace(lines[end - 1]))
                    end--;

                lines.Insert(end, line);
            }
        }

        File.WriteAllText(FilePath, string.Join(newLine, lines));

        // Apply the change right away instead of waiting for the file watcher
        (configuration as IConfigurationRoot)?.Reload();
    }

    private static bool IsKey(string line, string key)
    {
        int separator = line.IndexOf('=');
        return separator > 0 && line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }
}
