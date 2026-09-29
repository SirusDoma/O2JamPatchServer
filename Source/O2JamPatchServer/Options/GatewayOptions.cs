using Microsoft.Extensions.Options;

namespace O2JamPatchServer.Options;

public class GatewayOptions
{
    public const string Section = "Gateway";

    public string Address { get; init; } = string.Empty;
    public ushort Port    { get; init; }
}

public class GatewayOptionsValidator : IValidateOptions<List<GatewayOptions>>
{
    public ValidateOptionsResult Validate(string? name, List<GatewayOptions> options)
    {
        List<string> errors = [];

        for (int i = 0; i < options.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(options[i].Address))
                errors.Add($"{GatewayOptions.Section}:{i}:Address is required");

            if (options[i].Port == 0)
                errors.Add($"{GatewayOptions.Section}:{i}:Port is required");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
