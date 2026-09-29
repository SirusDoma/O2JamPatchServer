using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.CLI;

public record PatchArchive(string Name, long Size, int Version);

public record PatchManifest(int Target, IReadOnlyList<PatchArchive> Archives);

public partial class PatchFiles(PatchOptions patch, FtpOptions ftp)
{
    [GeneratedRegex(@"\{(?<name>\w+)(?::(?<format>[^}]+))?\}")]
    private static partial Regex Variable();

    public string PatchDirectory => GetPath(patch.Patch);

    public string ArchiveDirectory => GetPath(patch.Archive);

    public string ManifestDirectory
    {
        get
        {
            string directory = Path.GetDirectoryName(patch.Manifest) ?? string.Empty;
            if (directory.Contains('{'))
                throw new FormatException($"Variables are only supported in the file name of {PatchOptions.Section}:Manifest");

            return GetPath(directory);
        }
    }


    public static string Format(int hundredths)
        => new Version(hundredths / 100, hundredths % 100).ToPatchString();

    public string GetManifestPath(int version)
    {
        return GetPath(Variable().Replace(patch.Manifest, match =>
        {
            if (match.Groups["name"].Value != "GameVersion")
                throw new FormatException($"Unknown variable '{match.Value}' in {PatchOptions.Section}:Manifest");

            return match.Groups["format"].Success
                ? version.ToString(match.Groups["format"].Value, CultureInfo.InvariantCulture)
                : Format(version);
        }));
    }

    public bool IsManifest(string fileName)
    {
        return GetManifestPattern().IsMatch(fileName);
    }

    public SortedDictionary<int, PatchManifest> ReadManifests()
    {
        var manifests = new SortedDictionary<int, PatchManifest>();
        var manifestDirectory = new DirectoryInfo(ManifestDirectory);
        if (!manifestDirectory.Exists)
            return manifests;

        var regex = GetManifestPattern();
        foreach (var file in manifestDirectory.EnumerateFiles())
        {
            var match = regex.Match(file.Name);
            if (!match.Success)
                continue;

            int version = match.Groups["hundredths"].Success
                ? int.Parse(match.Groups["hundredths"].Value, CultureInfo.InvariantCulture)
                : ParseHundredths(match.Groups["version"].Value);

            manifests[version] = ReadManifest(file.FullName);
        }

        return manifests;
    }

    // Archives in installation order, which is the reverse of the manifest order
    public static List<PatchArchive> GetArchives(SortedDictionary<int, PatchManifest> manifests)
    {
        var archives = new List<PatchArchive>();
        foreach (var manifest in manifests.Values)
        {
            foreach (var archive in manifest.Archives.Reverse())
            {
                if (!archives.Any(a => a.Name.Equals(archive.Name, StringComparison.OrdinalIgnoreCase)))
                    archives.Add(archive);
            }
        }

        return archives;
    }

    public void WriteManifest(int version, int target, IEnumerable<PatchArchive> archives)
    {
        var content = new StringBuilder($"PATCH {Format(target)} END\r\n");
        foreach (var archive in archives.Reverse())
            content.Append($"Patch\\{archive.Name} {archive.Size} ZIP {archive.Version}\r\n");

        string path = GetManifestPath(version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ToString(), Encoding.ASCII);
    }

    private Regex GetManifestPattern()
    {
        string fileName = Path.GetFileName(patch.Manifest);
        var pattern = new StringBuilder("^");
        int index = 0;
        foreach (Match match in Variable().Matches(fileName))
        {
            pattern.Append(Regex.Escape(fileName[index..match.Index]));
            pattern.Append(match.Groups["format"].Success ? @"(?<hundredths>\d+)" : @"(?<version>\d+\.\d+)");
            index = match.Index + match.Length;
        }
        pattern.Append(Regex.Escape(fileName[index..])).Append('$');

        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase);
    }

    private string GetPath(string path)
        => Path.GetFullPath(Path.Combine(ftp.GetFullRoot(), patch.Path.TrimStart('/', '\\'), path.TrimStart('/', '\\')));

    private static int ParseHundredths(string text)
        => (int)Math.Round(decimal.Parse(text, CultureInfo.InvariantCulture) * 100);

    private static PatchManifest ReadManifest(string path)
    {
        string[] tokens = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
            throw new InvalidDataException($"Invalid manifest header: {path}");

        var archives = new List<PatchArchive>();
        for (int i = 3; i + 3 < tokens.Length; i += 4)
        {
            string name = tokens[i][(tokens[i].IndexOf('\\') + 1)..];
            archives.Add(new PatchArchive(name, long.Parse(tokens[i + 1], CultureInfo.InvariantCulture),
                int.TryParse(tokens[i + 3], CultureInfo.InvariantCulture, out int version) ? version : 0));
        }

        return new PatchManifest(ParseHundredths(tokens[1]), archives);
    }
}
