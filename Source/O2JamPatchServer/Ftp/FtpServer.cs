using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using O2JamPatchServer.CLI;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.Ftp;

public sealed class FtpServer(
    IOptions<FtpOptions> options,
    IOptionsMonitor<PatchOptions> patchOptions,
    IOptionsMonitor<MusicOptions> musicOptions,
    FtpFileSystem fileSystem,
    ILogger<FtpServer> logger
) : BackgroundService
{
    private TcpListener _listener = null!;
    private X509Certificate2? _certificate;
    private int _connections;
    private int _passivePort;

    public FtpOptions Options => options.Value;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Bind before the background loop so a bad port or certificate fails the startup
        _certificate = LoadCertificate(Options);
        _listener = new TcpListener(IPAddress.Parse(Options.Address), Options.Port);
        _listener.Start();

        using (logger.BeginScope("System"))
        {
            logger.LogInformation("FTP: Listening @ {EndPoint} (TLS: {Tls})", LocalEndPoint, Options.Tls);
            logger.LogInformation("[!] FTP root: {Directory}", fileSystem.RootDirectory);
            CreateDirectories();
        }

        return base.StartAsync(cancellationToken);
    }

    private void CreateDirectories()
    {
        var files = new PatchFiles(patchOptions.CurrentValue, Options);
        var music = musicOptions.CurrentValue;

        (string Name, Func<string> Resolve)[] directories =
        [
            ("FTP root", () => fileSystem.RootDirectory),
            ("Patch", () => files.PatchDirectory),
            ("Archive", () => files.ArchiveDirectory),
            ("Manifest", () => files.ManifestDirectory),
            ("Music", () => Path.GetFullPath(Path.Combine(fileSystem.RootDirectory, music.Path.TrimStart('/', '\\'))))
        ];

        foreach (var (name, resolve) in directories)
        {
            try
            {
                Directory.CreateDirectory(resolve());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                           NotSupportedException or FormatException)
            {
                logger.LogWarning("{Name} directory does not exist", name);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleAsync(client, _certificate, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _listener?.Dispose();
        _certificate?.Dispose();
    }

    private async Task HandleAsync(TcpClient client, X509Certificate2? certificate, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint;
        using var scope = logger.BeginScope("{Client}", remote);

        try
        {
            if (Interlocked.Increment(ref _connections) > Options.MaxConnections)
            {
                logger.LogWarning("Rejected connection: Too many connections");
                await client.GetStream().WriteAsync("421 Too many connections\r\n"u8.ToArray(), cancellationToken);
                return;
            }

            await using var session = new FtpSession(this, client, certificate, fileSystem, logger);
            await session.Run(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or
                                       InvalidDataException or System.Security.Authentication.AuthenticationException)
        {
            logger.LogDebug("Connection closed: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred during FTP session");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            client.Dispose();
        }
    }

    internal TcpListener CreatePassiveListener(IPAddress address)
    {
        if (Options.PassivePortMin == 0)
            return StartListener(address, 0) ?? throw new IOException("Failed to open passive port");

        int count = Options.PassivePortMax - Options.PassivePortMin + 1;
        for (int i = 0; i < count; i++)
        {
            int port = Options.PassivePortMin + (int)((uint)Interlocked.Increment(ref _passivePort) % count);
            if (StartListener(address, port) is { } listener)
                return listener;
        }

        throw new IOException("No passive port available");
    }

    private static TcpListener? StartListener(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start(1);
            return listener;
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;
        }
    }

    private static X509Certificate2? LoadCertificate(FtpOptions options)
    {
        if (options.Tls == FtpTlsMode.None)
            return null;

        string path = Path.GetFullPath(options.Certificate!, AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(options.CertificateKey))
            return X509CertificateLoader.LoadPkcs12FromFile(path, options.CertificatePassword);

        string key = Path.GetFullPath(options.CertificateKey, AppContext.BaseDirectory);
        using var pem = string.IsNullOrEmpty(options.CertificatePassword)
            ? X509Certificate2.CreateFromPemFile(path, key)
            : X509Certificate2.CreateFromEncryptedPemFile(path, options.CertificatePassword, key);

        // SChannel cannot use the ephemeral key of a PEM certificate
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }
}
