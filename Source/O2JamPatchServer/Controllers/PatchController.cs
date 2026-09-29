using Encore.Server;
using Encore.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using O2JamPatchServer.Messages.Responses;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.Controllers;

public class PatchController(
    TcpSession session,
    IOptionsMonitor<PatchOptions> patchOptions,
    IOptionsMonitor<MusicOptions> musicOptions,
    IOptionsMonitor<List<GatewayOptions>> gatewayOptions,
    ILogger<PatchController> logger
) : CommandController<TcpSession>(session)
{
    // e-Games patchers stop reading after the version strings
    [CommandHandler(RequestCommand.GetVersion)]
    public PatchInfoResponse GetPatchInfo()
    {
        var patch = patchOptions.CurrentValue;
        var music = musicOptions.CurrentValue;
        logger.LogInformation((int)RequestCommand.GetVersion, "Get patch info");

        return new PatchInfoResponse
        {
            VersionResult      = 0,
            GameVersion        = patch.GameVersion.ToPatchString(),
            PatchClientVersion = patch.PatchClientVersion.ToPatchString(),
            LauncherVersion    = patch.LauncherVersion.ToPatchString(),
            MusicListVersion   = patch.MusicListVersion.ToPatchString(),
            LocationsResult    = 0,
            FtpHost            = patch.Address,
            FtpRoot            = patch.FtpRoot,
            MusicServer        = music.EndPoint,
            MusicPath          = music.FtpPath,
            ServersResult      = 0,
            Servers            = GameServers,
            MinimumGameVersion = patch.MinimumGameVersion.ToPatchString()
        };
    }

    [CommandHandler(RequestCommand.GetDownloadLocations)]
    public DownloadLocationsResponse GetDownloadLocations()
    {
        var patch = patchOptions.CurrentValue;
        var music = musicOptions.CurrentValue;
        logger.LogInformation((int)RequestCommand.GetDownloadLocations, "Get download locations");

        return new DownloadLocationsResponse
        {
            Result      = 0,
            FtpHost     = patch.Address,
            FtpRoot     = patch.FtpRoot,
            MusicServer = music.EndPoint,
            MusicPath   = music.FtpPath
        };
    }

    [CommandHandler(RequestCommand.GetGameServers)]
    public GameServersResponse GetGameServers()
    {
        logger.LogInformation((int)RequestCommand.GetGameServers, "Get game servers");

        return new GameServersResponse
        {
            Result  = 0,
            Servers = GameServers
        };
    }

    private List<GameServer> GameServers => gatewayOptions.CurrentValue
        .Select(g => new GameServer { Host = g.Address, Port = g.Port })
        .ToList();
}
