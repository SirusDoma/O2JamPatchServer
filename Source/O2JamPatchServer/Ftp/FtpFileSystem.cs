using Microsoft.Extensions.Options;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.Ftp;

public sealed record FtpEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc,
    Func<Stream>? Open = null);

public sealed class FtpFileSystem(IOptions<FtpOptions> options)
{
    private static readonly EnumerationOptions Enumeration = new() { MatchCasing = MatchCasing.CaseInsensitive };

    public string RootDirectory => options.Value.GetFullRoot();

    public static string Normalize(string cwd, string path)
    {
        path = path.Replace('\\', '/');

        var segments = path.StartsWith('/') ? [] : cwd.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
                segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    public static (string Directory, string Name) Split(string path)
    {
        int index = path.LastIndexOf('/');
        return (index <= 0 ? "/" : path[..index], path[(index + 1)..]);
    }

    public FtpEntry? Find(string path)
        => Resolve(path) is { } info ? ToEntry(info) : null;

    public IEnumerable<FtpEntry> List(string directory, string pattern = "*")
    {
        if (Resolve(directory) is not DirectoryInfo info)
            return [];

        return info.EnumerateFileSystemInfos(pattern, Enumeration).Select(ToEntry).ToList();
    }

    private FileSystemInfo? Resolve(string path)
    {
        FileSystemInfo current = new DirectoryInfo(RootDirectory);
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // Only follow names returned by the enumeration, never the raw client input
            if (current is not DirectoryInfo directory || segment.AsSpan().IndexOfAny('*', '?') >= 0)
                return null;

            var matches = directory.EnumerateFileSystemInfos(segment, Enumeration).ToList();
            var next = matches.FirstOrDefault(m => m.Name == segment) ?? matches.FirstOrDefault();
            if (next == null)
                return null;

            current = next;
        }

        return current.Exists ? current : null;
    }

    private static FtpEntry ToEntry(FileSystemInfo info) => info switch
    {
        FileInfo file => new FtpEntry(file.Name, false, file.Length, file.LastWriteTimeUtc, file.OpenRead),
        _             => new FtpEntry(info.Name, true, 0, info.LastWriteTimeUtc)
    };
}
