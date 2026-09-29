namespace O2JamPatchServer;

public static class VersionExtensions
{
    public static string ToPatchString(this Version? version)
    {
        return version == null ? string.Empty : $"{version.Major}.{version.Minor:D2}";
    }

    public static int ToHundredths(this Version version)
    {
        return version.Major * 100 + version.Minor;
    }
}
