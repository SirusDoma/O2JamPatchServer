using System.Net.Sockets;
using Encore.Server;
using Encore.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace O2JamPatchServer.Workers;

public class DefaultWorker(ITcpServer server, TcpSessionManager manager, ILogger<DefaultWorker> logger, IHostEnvironment env)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using (logger.BeginScope("System"))
        {
            try
            {
                logger.LogInformation("O2JamPatchServer: Version {Version}", Program.Version);

                server.Start(server.Options.MaxConnections);

                logger.LogInformation("TCP: Listening @ {EndPoint}", server.Socket.LocalEndPoint);
                logger.LogInformation("[!] Environment: {Env}", env.EnvironmentName);
                logger.LogInformation("[?] Press [CTRL+C] to shut down");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Failed to start the application");
                throw;
            }
        }

        manager.Stopped += (_, args) =>
        {
            using (logger.BeginScope("System"))
                logger.LogInformation("Session [{Client}] stopped", args.Session.Socket?.RemoteEndPoint);
        };

        manager.Error += (_, args) =>
        {
            using (logger.BeginScope("System"))
            using (logger.BeginScope("Session"))
            {
                var address = args.Session.Socket?.RemoteEndPoint;
                switch (args.Exception)
                {
                    case EndOfStreamException or IOException { InnerException: SocketException }:
                        logger.LogInformation("Session [{Client}] disconnected", address);
                        break;
                    case OperationCanceledException:
                        logger.LogInformation("Session [{Client}] terminated by server", address);
                        break;
                    default:
                        logger.LogError(args.Exception, "An unexpected error occurred during session [{Client}] execution",
                            address);
                        break;
                }
            }
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpSession session;
            using (logger.BeginScope("System"))
            using (logger.BeginScope("Session"))
            {
                try
                {
                    session = await server.AcceptSession(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to accept session");
                    continue;
                }

                logger.LogInformation("Session [{Client}] started", session.Socket.RemoteEndPoint);
            }

            manager.StartSession(session);
        }

        using (logger.BeginScope("System"))
            logger.LogInformation("[!] Shutting down application..");

        await manager.ClearSessions();
    }
}
