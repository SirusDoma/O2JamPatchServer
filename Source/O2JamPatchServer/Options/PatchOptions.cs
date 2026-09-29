using Microsoft.Extensions.Options;

namespace O2JamPatchServer.Options;

public class PatchOptions
{
    public const string Section = "Patch";

    public string Address { get; init; } = "127.0.0.1";
    public string Path    { get; init; } = "O2Jam/";

    public string Manifest { get; init; } = "Patch/PatchInfo/FileList_{GameVersion:D3}.dat";
    public string Patch    { get; init; } = "Patch";
    public string Archive  { get; init; } = "Patch";

    public required Version GameVersion         { get; init; }
    public required Version PatchClientVersion  { get; init; }
    public required Version LauncherVersion     { get; init; }
    public required Version MusicListVersion    { get; init; }
    public Version?         MinimumGameVersion { get; init; }

    public string FtpRoot => Path.TrimEnd('/');

}

public class PatchOptionsValidator : IValidateOptions<PatchOptions>
{
    public ValidateOptionsResult Validate(string? name, PatchOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Address))
            errors.Add($"{PatchOptions.Section}:Address is required");

        void Version(string key, Version? version)
        {
            if (version is { Minor: > 99 } or { Build: >= 0 })
                errors.Add($"{PatchOptions.Section}:{key} must be x.yy ('{version}')");
        }

        Version(nameof(options.GameVersion), options.GameVersion);
        Version(nameof(options.PatchClientVersion), options.PatchClientVersion);
        Version(nameof(options.LauncherVersion), options.LauncherVersion);
        Version(nameof(options.MusicListVersion), options.MusicListVersion);
        Version(nameof(options.MinimumGameVersion), options.MinimumGameVersion);

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
