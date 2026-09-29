using System.CommandLine;
using System.IO.Compression;
using Encore.CLI;
using Microsoft.Extensions.Options;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.CLI;

public class PatchAddCommandTask(
    IOptionsMonitor<PatchOptions> patchOptions,
    IOptions<FtpOptions> ftpOptions,
    ConfigFile configFile
) : ICommandLineTask
{
    private record PatchType(string Name, string Label, string VersionKey, Func<PatchOptions, Version> GetVersion);

    private record ContentEntry(string Name, string? File, byte[]? Package, bool IsPackage);

    private record ContentInput(string Source, string Destination, List<ContentEntry>? Entries);

    private static readonly PatchType Launcher  = new("launcher", "launcher", nameof(PatchOptions.LauncherVersion), o => o.LauncherVersion);
    private static readonly PatchType Patcher   = new("patcher", "patcher", nameof(PatchOptions.PatchClientVersion), o => o.PatchClientVersion);
    private static readonly PatchType MusicList = new("music-list", "music list", nameof(PatchOptions.MusicListVersion), o => o.MusicListVersion);
    private static readonly PatchType Content   = new("content", "content", nameof(PatchOptions.GameVersion), o => o.GameVersion);

    public static string Name => "patch:add";
    public static string Description => "Publish the launcher, patcher, music list or content and raise their versions";

    public void ConfigureCommand(Command command)
    {
        var filesArgument = new Argument<string[]>("files")
        {
            Description = "Publish specified files or directories: O2Jam.exe as launcher, O2JamPatchClient.exe as patcher, " +
                          "*.dat as music list, anything else as content",
            Arity = ArgumentArity.ZeroOrMore
        };
        var fileOptions = new[] { Launcher, Patcher, MusicList }.ToDictionary(t => t,
            t => new Option<string?>($"--{t.Name}") { Description = $"Publish specified file as {t.Label}", HelpName = "file" });
        var contentOption = new Option<string[]>("--content")
        {
            HelpName = "path",
            Description = "Publish specified directory or file as content. " +
                          ".opi and .opa directories are packed, and .opi and .opa files moved, into <Parent>/Temp/<Name>_<Version>.<opi|opa>"
        };
        var contentNameOption = new Option<string?>("--content-name")
        {
            HelpName = "name",
            Description = "Set published content name"
        };
        var versionOption = new Option<string?>("--version")
        {
            HelpName = "version",
            Description = "Set version, single type only"
        };
        var versionOptions = new[] { Launcher, Patcher, MusicList, Content }.ToDictionary(t => t,
            t => new Option<string?>($"--{t.Name}-version")
            {
                HelpName = "version",
                Description = t == Content ? "Set game version" : $"Set {t.Label} version"
            });
        var skipVersionOption = new Option<bool>("--skip-version")
        {
            Description = "Publish without raising versions, content goes to current game version"
        };
        var skipPackingOption = new Option<bool>("--skip-packing")
        {
            Description = "Do not pack or move .opi and .opa"
        };

        command.Arguments.Add(filesArgument);
        foreach (var option in fileOptions.Values)
            command.Options.Add(option);
        command.Options.Add(contentOption);
        command.Options.Add(contentNameOption);
        command.Options.Add(versionOption);
        foreach (var option in versionOptions.Values)
            command.Options.Add(option);
        command.Options.Add(skipVersionOption);
        command.Options.Add(skipPackingOption);

        command.SetAction(parseResult => Execute(
            parseResult.GetValue(filesArgument) ?? [],
            fileOptions.ToDictionary(o => o.Key, o => parseResult.GetValue(o.Value)),
            parseResult.GetValue(contentOption) ?? [],
            parseResult.GetValue(contentNameOption),
            parseResult.GetValue(versionOption),
            versionOptions.ToDictionary(o => o.Key, o => parseResult.GetValue(o.Value)),
            parseResult.GetValue(skipVersionOption),
            parseResult.GetValue(skipPackingOption)));
    }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Use the overload of Execute instead");
    }

    private int Execute(string[] paths, Dictionary<PatchType, string?> typedPaths, string[] contentPaths, string? contentName,
        string? versionText, Dictionary<PatchType, string?> versionTexts, bool skipVersion, bool skipPacking)
    {
        try
        {
            var patch = patchOptions.CurrentValue;
            var files = new PatchFiles(patch, ftpOptions.Value);

            var sources = new Dictionary<PatchType, string>();
            var contents = new List<string>();
            foreach (string path in paths)
            {
                string source = CommandLinePath.GetFullPath(path);
                if (!Directory.Exists(source) && !File.Exists(source))
                    return Fail($"{source} not found");

                var type = Detect(source);
                if (type == MusicList && files.IsManifest(Path.GetFileName(source)))
                    return Fail($"{source} is a manifest file");

                if (type == Content)
                    contents.Add(source);
                else if (!sources.TryAdd(type, source))
                    return Fail($"more than one {type.Label} given");
            }

            foreach (var (type, path) in typedPaths)
            {
                if (path == null)
                    continue;

                string source = CommandLinePath.GetFullPath(path);
                if (!File.Exists(source))
                    return Fail($"{source} not found");

                if (!sources.TryAdd(type, source))
                    return Fail($"more than one {type.Label} given");
            }

            foreach (string path in contentPaths)
            {
                string source = CommandLinePath.GetFullPath(path);
                if (!Directory.Exists(source) && !File.Exists(source))
                    return Fail($"{source} not found");

                contents.Add(source);
            }

            var types = sources.Keys.ToList();
            if (contents.Count > 0)
                types.Add(Content);

            if (types.Count == 0)
                return Fail("nothing to publish");

            var versions = new Dictionary<PatchType, int>();
            foreach (var (type, text) in versionTexts)
            {
                if (text == null)
                    continue;

                if (!types.Contains(type))
                    return Fail($"--{type.Name}-version given without {type.Label}");

                versions[type] = Version.Parse(text).ToHundredths();
            }

            if (versionText != null)
            {
                if (types.Count != 1)
                    return Fail($"--version applies to a single type, use {string.Join(", ", types.Select(t => $"--{t.Name}-version"))}");

                if (versions.ContainsKey(types[0]))
                    return Fail($"--version and --{types[0].Name}-version both given");

                versions[types[0]] = Version.Parse(versionText).ToHundredths();
            }

            if (skipVersion && versions.Count > 0)
                return Fail("--skip-version cannot be combined with a version");

            if (contentName != null && contents.Count != 1)
                return Fail("--content-name requires a single content");

            if (contentName != null && (contentName != Path.GetFileName(contentName) || contentName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                return Fail($"invalid archive name ({contentName})");

            var published = sources.Select(s => (Type: s.Key, Source: s.Value, Destination: Path.Combine(files.PatchDirectory, Path.GetFileName(s.Value))))
                .ToList();

            int current = patch.GameVersion.ToHundredths();
            int target = versions.TryGetValue(Content, out int contentVersion) ? contentVersion : skipVersion ? current : current + 1;
            int first = current;
            var manifests = new SortedDictionary<int, PatchManifest>();
            var archives = new List<PatchArchive>();
            var inputs = new List<ContentInput>();

            if (contents.Count > 0)
            {
                manifests = files.ReadManifests();
                archives = PatchFiles.GetArchives(manifests);

                if (archives.FirstOrDefault(a => a.Version <= 0) is { } unknown)
                    return Fail($"unknown game version of {unknown.Name} in the manifests");

                first = patch.MinimumGameVersion != null
                    ? patch.MinimumGameVersion.ToHundredths()
                    : manifests.Count > 0 ? manifests.Keys.First() : current;

                if (target <= first)
                    return Fail($"{PatchFiles.Format(target)} must be higher than {PatchFiles.Format(first)}");

                foreach (string source in contents)
                {
                    bool isDirectory = Directory.Exists(source);
                    bool isZip = !isDirectory && Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase);

                    string name = contentName != null
                        ? contentName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? contentName : contentName + ".zip"
                        : isDirectory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source)) + ".zip"
                        : isZip ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source) + ".zip";
                    string destination = Path.Combine(files.ArchiveDirectory, name);

                    // Manifest records are whitespace separated
                    if (name.Any(char.IsWhiteSpace))
                        return Fail($"archive name cannot contain whitespace ({name})");

                    if (File.Exists(destination) || inputs.Any(i => i.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)) ||
                        archives.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        return Fail($"archive {name} already exists");

                    var entries = isDirectory ? ReadEntries(source, target, skipPacking)
                        : isZip ? null
                        : [new ContentEntry(Path.GetFileName(source), source, null, false)];

                    if (entries?.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                        return Fail($"{duplicate.Key} is produced more than once in {name}");

                    inputs.Add(new ContentInput(source, destination, entries));
                }

                if (FindPackageConflict(files, archives, inputs, target) is { } conflict)
                    return Fail(conflict);
            }

            var destinations = published.Select(p => p.Destination).Concat(inputs.Select(i => i.Destination)).ToList();
            if (destinations.GroupBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } clash)
                return Fail($"{clash.Key} would be written more than once");

            foreach (var (type, source, destination) in published)
            {
                Directory.CreateDirectory(files.PatchDirectory);
                if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                    File.Copy(source, destination, overwrite: true);

                Console.WriteLine($"Published {destination}");

                if (!skipVersion)
                {
                    int next = versions.TryGetValue(type, out int version) ? version : type.GetVersion(patch).ToHundredths() + 1;
                    configFile.Set(PatchOptions.Section, type.VersionKey, PatchFiles.Format(next));
                    Console.WriteLine($"{type.VersionKey}: {PatchFiles.Format(next)}");
                }
            }

            if (inputs.Count > 0)
            {
                Directory.CreateDirectory(files.ArchiveDirectory);
                foreach (var (source, destination, entries) in inputs)
                {
                    if (entries != null)
                        CreateArchive(destination, entries);
                    else
                        File.Copy(source, destination);

                    archives.Add(new PatchArchive(Path.GetFileName(destination), new FileInfo(destination).Length, target));
                    Console.WriteLine($"Published {destination}");

                    foreach (var package in entries?.Where(e => e.IsPackage) ?? [])
                    {
                        Console.WriteLine(package.File != null
                            ? $"  Moved {GetEntryName(source, package.File)} -> {package.Name}"
                            : $"  Packed {package.Name}");
                    }
                }

                int latest = Math.Max(current, target);
                for (int installed = first; installed < latest; installed++)
                {
                    files.WriteManifest(installed, latest, archives.Where(a => a.Version > installed));
                    Console.WriteLine($"Updated {files.GetManifestPath(installed)}");
                }

                if (latest != current)
                {
                    configFile.Set(PatchOptions.Section, Content.VersionKey, PatchFiles.Format(latest));
                    Console.WriteLine($"{Content.VersionKey}: {PatchFiles.Format(latest)}");
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or
                                       UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.WriteLine($"Failed to add patch: {message}");
        return 1;
    }

    private static PatchType Detect(string source)
    {
        string name = Path.GetFileName(source);
        if (Directory.Exists(source))
            return Content;

        if (name.Equals("O2Jam.exe", StringComparison.OrdinalIgnoreCase))
            return Launcher;

        if (name.Equals("O2JamPatchClient.exe", StringComparison.OrdinalIgnoreCase))
            return Patcher;

        return Path.GetExtension(name).Equals(".dat", StringComparison.OrdinalIgnoreCase) ? MusicList : Content;
    }

    private static List<ContentEntry> ReadEntries(string directory, int version, bool skipPacking)
    {
        var packages = skipPacking
            ? []
            : Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Where(OpiArchive.IsPackage).ToList();

        var entries = new List<ContentEntry>();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (packages.Any(p => file.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Packages already in Temp are prebuilt patches
            bool move = !skipPacking && OpiArchive.IsPackage(file) &&
                        !Path.GetFileName(Path.GetDirectoryName(file))!.Equals("Temp", StringComparison.OrdinalIgnoreCase);

            entries.Add(move
                ? new ContentEntry(GetEntryName(directory, GetTempPath(file, version)), file, null, true)
                : new ContentEntry(GetEntryName(directory, file), file, null, false));
        }

        foreach (string package in packages)
            entries.Add(new ContentEntry(GetEntryName(directory, GetTempPath(package, version)), null, OpiArchive.Pack(package), true));

        return entries;
    }

    private static string GetTempPath(string package, int version)
    {
        string name = $"{Path.GetFileNameWithoutExtension(package)}_{version}{Path.GetExtension(package)}";
        return Path.Combine(Path.GetDirectoryName(package)!, "Temp", name);
    }

    // Another archive of the same version would replace the package on extraction instead of merging it
    private static string? FindPackageConflict(PatchFiles files, List<PatchArchive> archives, List<ContentInput> inputs, int version)
    {
        var owners = new List<(string Archive, string Entry)>();
        foreach (var archive in archives.Where(a => a.Version == version))
        {
            string path = Path.Combine(files.ArchiveDirectory, archive.Name);
            if (File.Exists(path))
                owners.AddRange(ReadZipEntries(path).Select(e => (archive.Name, e)));
        }

        foreach (var (source, destination, entries) in inputs)
        {
            var names = entries?.Select(e => e.Name) ?? ReadZipEntries(source);
            owners.AddRange(names.Select(e => (Path.GetFileName(destination), e)));
        }

        foreach (var (_, destination, entries) in inputs)
        {
            foreach (var package in entries?.Where(e => e.IsPackage) ?? [])
            {
                var owner = owners.FirstOrDefault(o => o.Entry.Equals(package.Name, StringComparison.OrdinalIgnoreCase) &&
                                                       o.Archive != Path.GetFileName(destination));
                if (owner != default)
                    return $"{package.Name} is also in {owner.Archive}";
            }
        }

        return null;
    }

    private static List<string> ReadZipEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
    }

    private static string GetEntryName(string directory, string path)
    {
        return Path.GetRelativePath(directory, path).Replace('\\', '/');
    }

    private static void CreateArchive(string destination, List<ContentEntry> entries)
    {
        using var stream = File.Create(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var entry in entries)
        {
            if (entry.File != null)
            {
                archive.CreateEntryFromFile(entry.File, entry.Name);
                continue;
            }

            using var output = archive.CreateEntry(entry.Name).Open();
            output.Write(entry.Package);
        }
    }
}
