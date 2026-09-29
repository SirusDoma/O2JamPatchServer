using System.Net.Sockets;
using Encore.CLI;
using Encore.Hosting.Extensions;
using Encore.Messaging;
using Encore.Server;
using Encore.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using O2JamPatchServer.CLI;
using O2JamPatchServer.Controllers;
using O2JamPatchServer.Controllers.Filters;
using O2JamPatchServer.Ftp;
using O2JamPatchServer.Options;
using O2JamPatchServer.Workers;

namespace O2JamPatchServer;

public class Program
{
    public static Version Version => new(1, 0, 0);

    private static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:Address"]          = "127.0.0.1",
                ["Server:Port"]             = "15050",
                ["Server:MaxConnections"]   = ((int)SocketOptionName.MaxConnections).ToString(),
                ["Server:PacketBufferSize"] = "4096"
            })
            .AddIniFile("config.ini", true, true)
            .AddCommandLine(args)
            .Build();

        var hostBuilder = CreateHostBuilder(args, config);

        // Execute custom command if any
        int? code = await ExecuteCommandLine(hostBuilder, args);
        if (code != null)
            return code.Value;

        // Otherwise configure the rest of server and start it
        var console = ServerConsole.Attach();
        hostBuilder = hostBuilder
            .ConfigureTcpFramer((context, builder) =>
            {
                builder.AddFramerFactory<SizePrefixedMessageFramer<short>>();
            })
            .ConfigureTcpSessions((context, provider) =>
            {
                provider.UseTcpSession<TcpSession>()
                    .AddFactory<SessionFactory>()
                    .AddManager<TcpSessionManager>();
            })
            .ConfigureFilters((context, builder) =>
            {
                builder.AddExceptionHandler<DefaultExceptionHandler>()
                    .AddExceptionLogger<DefaultExceptionLogger>()
                    .AddFilter<ControllerLoggerFilter>();
            })
            .ConfigureRoutes((context, provider) =>
            {
                provider.UseCodec<DefaultMessageCodec>()
                    .Map<PatchController>();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<ITcpServer, TcpServer>()
                    .AddSingleton<FtpFileSystem>()
                    .AddSingleton<FtpServer>()
                    .AddSingleton(console)
                    .AddSingleton(provider => CommandLineTaskProcessor.CreateProcessor(provider)
                        .ConfigureCommandTasks(ConfigureCommandTasks));

                services.AddHostedService<DefaultWorker>()
                    .AddHostedService(provider => provider.GetRequiredService<FtpServer>())
                    .AddHostedService<ConsoleWorker>();
            });

        await hostBuilder.Build().RunAsync();
        return 0;
    }

    private static async Task<int?> ExecuteCommandLine(IHostBuilder hostBuilder, string[] args)
    {
        return await CommandLineTaskProcessor.CreateDefaultProcessor(hostBuilder)
            .ConfigureCommandTasks(ConfigureCommandTasks)
            .ExecuteAsync(args);
    }

    private static void ConfigureCommandTasks(ICommandLineTasksBuilder builder)
    {
        builder.AddCommandLineTask<PatchAddCommandTask>()
            .AddCommandLineTask<PatchStatusCommandTask>();
    }

    public static IHostBuilder CreateHostBuilder(string[] args, IConfiguration config)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, builder) =>
            {
                builder
                    .AddConfiguration(config)
                    .AddIniFile($"config.{context.HostingEnvironment.EnvironmentName}.ini", true, true);
            })
            .ConfigureLogging((context, builder) =>
            {
                builder.ClearProviders()
                    .AddConsole(options => options.FormatterName = "EncoreLoggerFormatter")
                    .AddConsoleFormatter<EncoreConsoleFormatter, EncoreConsoleFormatterOptions>()
                    .AddFilter("Microsoft.*", LogLevel.None)
                    .SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                // Configurations
                services.AddOptions<TcpOptions>()
                    .BindConfiguration(TcpOptions.Section);
                services.AddOptions<PatchOptions>()
                    .BindConfiguration(PatchOptions.Section)
                    .ValidateOnStart();
                services.AddOptions<MusicOptions>()
                    .BindConfiguration(MusicOptions.Section)
                    .ValidateOnStart();
                services.AddOptions<List<GatewayOptions>>()
                    .BindConfiguration(GatewayOptions.Section)
                    .ValidateOnStart();
                services.AddOptions<FtpOptions>()
                    .BindConfiguration(FtpOptions.Section)
                    .ValidateOnStart();

                services.AddSingleton<IValidateOptions<PatchOptions>, PatchOptionsValidator>()
                    .AddSingleton<IValidateOptions<MusicOptions>, MusicOptionsValidator>()
                    .AddSingleton<IValidateOptions<List<GatewayOptions>>, GatewayOptionsValidator>()
                    .AddSingleton<IValidateOptions<FtpOptions>, FtpOptionsValidator>();

                services.AddSingleton(new ConfigFile(config));
            });
    }
}
