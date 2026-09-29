using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace O2JamPatchServer.Options;

public enum FtpTlsMode
{
    None,
    Explicit,
    Implicit
}

public class FtpOptions
{
    public const string Section = "Ftp";

    public string Address { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 21;
    public int MaxConnections { get; init; } = (int)SocketOptionName.MaxConnections;

    public string Root { get; init; } = "Public";
    public string Greeting { get; init; } = "Service ready for new user";

    public string? PassiveAddress { get; init; }
    public int PassivePortMin { get; init; }
    public int PassivePortMax { get; init; }

    public FtpTlsMode Tls { get; init; } = FtpTlsMode.None;
    public string? Certificate { get; init; }
    public string? CertificateKey { get; init; }
    public string? CertificatePassword { get; init; }

    public string GetFullRoot()
        => Path.GetFullPath(Root, AppContext.BaseDirectory);
}

public class FtpOptionsValidator : IValidateOptions<FtpOptions>
{
    public ValidateOptionsResult Validate(string? name, FtpOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Root))
            errors.Add($"{FtpOptions.Section}:Root is required");

        if (!IPAddress.TryParse(options.Address, out _))
            errors.Add($"{FtpOptions.Section}:Address must be an IP address ('{options.Address}')");

        if (!string.IsNullOrEmpty(options.PassiveAddress) &&
            (!IPAddress.TryParse(options.PassiveAddress, out var passive) || passive.AddressFamily != AddressFamily.InterNetwork))
            errors.Add($"{FtpOptions.Section}:PassiveAddress must be an IPv4 address ('{options.PassiveAddress}')");

        if (options.PassivePortMin != 0 || options.PassivePortMax != 0)
        {
            if (options.PassivePortMin is < 1 or > 65535 || options.PassivePortMax is < 1 or > 65535 ||
                options.PassivePortMin > options.PassivePortMax)
                errors.Add($"{FtpOptions.Section}:PassivePortMin and PassivePortMax must form a valid port range");
        }

        if (options.Tls != FtpTlsMode.None && string.IsNullOrEmpty(options.Certificate))
            errors.Add($"{FtpOptions.Section}:Certificate is required when Tls is {options.Tls}");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
