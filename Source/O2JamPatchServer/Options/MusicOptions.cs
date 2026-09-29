using Microsoft.Extensions.Options;

namespace O2JamPatchServer.Options;

public class MusicOptions
{
    public const string Section = "Music";

    public string Address { get; init; } = "127.0.0.1";
    public int? Port { get; init; }
    public string Path { get; init; } = "O2Jam/";

    public string EndPoint => Port is > 0 ? $"{Address}:{Port}" : Address;

    public string FtpPath => Path.TrimEnd('/') + "/";
}

public class MusicOptionsValidator : IValidateOptions<MusicOptions>
{
    public ValidateOptionsResult Validate(string? name, MusicOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Address))
            errors.Add($"{MusicOptions.Section}:Address is required");

        if (options.Port is < 0 or > 65535)
            errors.Add($"{MusicOptions.Section}:Port must be a valid port ('{options.Port}')");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
