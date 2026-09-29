using System.CommandLine;
using Encore.CLI;
using Microsoft.Extensions.Options;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.CLI;

public class PatchStatusCommandTask(IOptionsMonitor<PatchOptions> patchOptions, IOptions<FtpOptions> ftpOptions) : ICommandLineTask
{
    public static string Name => "patch:status";
    public static string Description => "Display the published patches and report inconsistencies";

    public void ConfigureCommand(Command command)
    {
        command.SetAction(_ => Execute());
    }

    public int Execute()
    {
        try
        {
            var patch = patchOptions.CurrentValue;
            var files = new PatchFiles(patch, ftpOptions.Value);
            var manifests = files.ReadManifests();
            var archives = PatchFiles.GetArchives(manifests);
            int game = patch.GameVersion.ToHundredths();
            List<string> problems = [];

            Console.WriteLine($"Game version:        {patch.GameVersion.ToPatchString()}");
            Console.WriteLine($"Launcher version:    {patch.LauncherVersion.ToPatchString()}");
            Console.WriteLine($"Patcher version:     {patch.PatchClientVersion.ToPatchString()}");
            Console.WriteLine($"Music list version:  {patch.MusicListVersion.ToPatchString()}");
            Console.WriteLine();

            Console.WriteLine("Files:");
            var patchDirectory = new DirectoryInfo(files.PatchDirectory);
            if (patchDirectory.Exists)
            {
                foreach (var file in patchDirectory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!archives.Any(a => Path.Combine(files.ArchiveDirectory, a.Name).Equals(file.FullName, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"  {file.Name} ({file.Length} bytes)");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Manifests:");
            foreach (var (version, manifest) in manifests)
            {
                Console.WriteLine($"  {PatchFiles.Format(version)} -> {PatchFiles.Format(manifest.Target)} ({manifest.Archives.Count} archives)");
                if (manifest.Target != game)
                    problems.Add($"Manifest for {PatchFiles.Format(version)} ends at {PatchFiles.Format(manifest.Target)} instead of the game version");
            }

            int first = patch.MinimumGameVersion != null
                ? patch.MinimumGameVersion.ToHundredths()
                : manifests.Count > 0 ? manifests.Keys.First() : game;

            for (int version = first; version < game; version++)
            {
                if (!manifests.ContainsKey(version))
                    problems.Add($"Manifest for {PatchFiles.Format(version)} not found: {files.GetManifestPath(version)}");
            }

            Console.WriteLine();
            Console.WriteLine("Archives:");
            foreach (var archive in archives)
            {
                var file = new FileInfo(Path.Combine(files.ArchiveDirectory, archive.Name));
                Console.WriteLine($"  {(archive.Version > 0 ? PatchFiles.Format(archive.Version) : "?.??")} {archive.Name} ({archive.Size} bytes)");

                if (archive.Version <= 0)
                    problems.Add($"Unknown game version of {archive.Name}");

                if (!file.Exists)
                    problems.Add($"Archive not found: {file.FullName}");
                else if (file.Length != archive.Size)
                    problems.Add($"Archive size mismatch: {file.FullName} is {file.Length} bytes, the manifests list {archive.Size}");
            }

            Console.WriteLine();
            foreach (string problem in problems)
                Console.WriteLine($"[!] {problem}");

            if (problems.Count == 0)
                Console.WriteLine("No problems found");

            return problems.Count > 0 ? 1 : 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or
                                       UnauthorizedAccessException or InvalidDataException)
        {
            Console.WriteLine($"Failed to read patches: {ex.Message}");
            return 1;
        }
    }
}
