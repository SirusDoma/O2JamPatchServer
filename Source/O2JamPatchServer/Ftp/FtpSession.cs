using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using O2JamPatchServer.Options;

namespace O2JamPatchServer.Ftp;

internal sealed class FtpSession(
    FtpServer server,
    TcpClient client,
    X509Certificate2? certificate,
    FtpFileSystem fileSystem,
    ILogger logger
) : IAsyncDisposable
{
    private const int MaxLineLength = 1024;
    private static readonly TimeSpan IdleTimeout    = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly IPAddress _remoteAddress = Unmap(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
    private readonly IPAddress _localAddress  = Unmap(((IPEndPoint)client.Client.LocalEndPoint!).Address);

    private Stream _stream = client.GetStream();
    private StreamReader _reader = null!;
    private TcpListener? _passive;

    private bool _secure;
    private bool _protected;
    private string? _user;
    private bool _authenticated;
    private string _directory = "/";
    private long _offset;

    public async Task Run(CancellationToken cancellationToken)
    {
        if (server.Options.Tls == FtpTlsMode.Implicit)
        {
            await Secure(cancellationToken);
            _protected = true;
        }

        _reader = new StreamReader(_stream, Encoding.UTF8, false, MaxLineLength, leaveOpen: true);
        await Reply(220, server.Options.Greeting, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);

                line = await ReadLine(idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Reply(421, "Idle timeout", cancellationToken);
                return;
            }

            if (line == null)
                return;

            int separator = line.IndexOf(' ');
            string command  = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
            string argument = separator < 0 ? string.Empty : line[(separator + 1)..];

            logger.LogDebug("{Command} {Argument}", command, command == "PASS" ? "****" : argument);

            try
            {
                if (!await Execute(command, argument, cancellationToken))
                    return;
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException)
            {
                logger.LogDebug(ex, "Failed to execute {Command}", command);
                await Reply(550, "Requested action not taken", cancellationToken);
            }
        }
    }

    private async Task<bool> Execute(string command, string argument, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "QUIT":
                await Reply(221, "Goodbye", cancellationToken);
                return false;
            case "NOOP":
                await Reply(200, "OK", cancellationToken);
                return true;
            case "SYST":
                await Reply(215, "UNIX Type: L8", cancellationToken);
                return true;
            case "FEAT":
                await Features(cancellationToken);
                return true;
            case "OPTS":
                await Reply(argument.Equals("UTF8 ON", StringComparison.OrdinalIgnoreCase) ? 200 : 501,
                    "OPTS", cancellationToken);
                return true;
            case "AUTH":
                await Authenticate(argument, cancellationToken);
                return true;
            case "PBSZ":
                await Reply(_secure ? 200 : 503, _secure ? "PBSZ=0" : "Use AUTH first", cancellationToken);
                return true;
            case "PROT":
                await Protect(argument, cancellationToken);
                return true;
            case "USER":
                _user = argument;
                _authenticated = false;
                await Reply(331, "Password required", cancellationToken);
                return true;
            case "PASS":
                _authenticated = _user != null;
                await Reply(_authenticated ? 230 : 503, _authenticated ? "Logged in" : "Use USER first",
                    cancellationToken);
                return true;
        }

        if (!_authenticated)
        {
            await Reply(530, "Please login with USER and PASS", cancellationToken);
            return true;
        }

        switch (command)
        {
            case "PWD":
            case "XPWD":
                await Reply(257, $"\"{_directory.Replace("\"", "\"\"")}\" is the current directory", cancellationToken);
                break;
            case "CWD":
            case "XCWD":
                await ChangeDirectory(argument, cancellationToken);
                break;
            case "CDUP":
            case "XCUP":
                await ChangeDirectory("..", cancellationToken);
                break;
            case "TYPE":
                bool supported = argument.ToUpperInvariant() is "A" or "A N" or "I" or "L 8";
                await Reply(supported ? 200 : 504, supported ? $"Type set to {argument}" : "Unsupported type",
                    cancellationToken);
                break;
            case "MODE":
            case "STRU":
                bool stream = argument.ToUpperInvariant() is "S" or "F";
                await Reply(stream ? 200 : 504, stream ? "OK" : "Unsupported parameter", cancellationToken);
                break;
            case "PASV":
                await Passive(extended: false, cancellationToken);
                break;
            case "EPSV":
                if (argument.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                    await Reply(200, "EPSV ALL accepted", cancellationToken);
                else
                    await Passive(extended: true, cancellationToken);
                break;
            case "REST":
                bool valid = long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out _offset);
                await Reply(valid ? 350 : 501, valid ? $"Restarting at {_offset}" : "Invalid offset", cancellationToken);
                break;
            case "SIZE":
                if (Find(argument) is { IsDirectory: false } sized)
                    await Reply(213, sized.Length.ToString(CultureInfo.InvariantCulture), cancellationToken);
                else
                    await Reply(550, "File not found", cancellationToken);
                break;
            case "MDTM":
                if (Find(argument) is { IsDirectory: false } dated)
                    await Reply(213, dated.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                        cancellationToken);
                else
                    await Reply(550, "File not found", cancellationToken);
                break;
            case "RETR":
                await Retrieve(argument, cancellationToken);
                break;
            case "LIST":
                await List(argument, namesOnly: false, cancellationToken);
                break;
            case "NLST":
                await List(argument, namesOnly: true, cancellationToken);
                break;
            case "ABOR":
                await Reply(226, "No transfer to abort", cancellationToken);
                break;
            case "PORT":
            case "EPRT":
                await Reply(502, "Active mode is not supported, use PASV or EPSV", cancellationToken);
                break;
            default:
                await Reply(502, "Command not implemented", cancellationToken);
                break;
        }

        return true;
    }

    private async Task Features(CancellationToken cancellationToken)
    {
        var features = new StringBuilder("211-Features:\r\n");
        if (certificate != null && server.Options.Tls == FtpTlsMode.Explicit)
            features.Append(" AUTH TLS\r\n");

        if (certificate != null)
            features.Append(" PBSZ\r\n PROT\r\n");

        features.Append(" EPSV\r\n PASV\r\n MDTM\r\n SIZE\r\n REST STREAM\r\n UTF8\r\n211 End\r\n");
        await Write(features.ToString(), cancellationToken);
    }

    private async Task Authenticate(string mechanism, CancellationToken cancellationToken)
    {
        if (certificate == null || server.Options.Tls != FtpTlsMode.Explicit)
        {
            await Reply(502, "TLS is not enabled", cancellationToken);
            return;
        }

        if (_secure)
        {
            await Reply(503, "Connection is already secured", cancellationToken);
            return;
        }

        if (mechanism.ToUpperInvariant() is not ("TLS" or "TLS-C" or "SSL"))
        {
            await Reply(504, "Unsupported security mechanism", cancellationToken);
            return;
        }

        await Reply(234, $"AUTH {mechanism} successful", cancellationToken);
        await Secure(cancellationToken);

        _reader.Dispose();
        _reader = new StreamReader(_stream, Encoding.UTF8, false, MaxLineLength, leaveOpen: true);
        _user = null;
        _authenticated = false;
        _protected = true;
    }

    private async Task Protect(string level, CancellationToken cancellationToken)
    {
        if (!_secure)
        {
            await Reply(503, "Use AUTH first", cancellationToken);
            return;
        }

        switch (level.ToUpperInvariant())
        {
            case "P":
                _protected = true;
                await Reply(200, "Protection level set to Private", cancellationToken);
                break;
            case "C":
                await Reply(534, "Clear data channel is not allowed on a secured connection", cancellationToken);
                break;
            default:
                await Reply(536, "Unsupported protection level", cancellationToken);
                break;
        }
    }

    private async Task Secure(CancellationToken cancellationToken)
    {
        _stream = await Authenticate(client.GetStream(), cancellationToken);
        _secure = true;
    }

    private async Task<SslStream> Authenticate(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate
            }, timeout.Token);

            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    private async Task ChangeDirectory(string path, CancellationToken cancellationToken)
    {
        string target = FtpFileSystem.Normalize(_directory, path);
        if (fileSystem.Find(target) is { IsDirectory: true })
        {
            _directory = target;
            await Reply(250, $"Directory changed to {target}", cancellationToken);
        }
        else
            await Reply(550, "Directory not found", cancellationToken);
    }

    private async Task Passive(bool extended, CancellationToken cancellationToken)
    {
        var address = string.IsNullOrEmpty(server.Options.PassiveAddress)
            ? _localAddress
            : IPAddress.Parse(server.Options.PassiveAddress);

        if (!extended && address.AddressFamily != AddressFamily.InterNetwork)
        {
            await Reply(425, "PASV requires IPv4, use EPSV", cancellationToken);
            return;
        }

        _passive?.Stop();
        _passive = null;

        try
        {
            _passive = server.CreatePassiveListener(_localAddress);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to open passive port");
            await Reply(425, "Can't open passive connection", cancellationToken);
            return;
        }

        int port = ((IPEndPoint)_passive.LocalEndpoint).Port;
        if (extended)
            await Reply(229, $"Entering Extended Passive Mode (|||{port}|)", cancellationToken);
        else
        {
            string host = address.ToString().Replace('.', ',');
            await Reply(227, $"Entering Passive Mode ({host},{port >> 8},{port & 0xFF})", cancellationToken);
        }
    }

    private async Task Retrieve(string path, CancellationToken cancellationToken)
    {
        long offset = _offset;
        _offset = 0;

        if (Find(path) is not { IsDirectory: false, Open: not null } file)
        {
            await Reply(550, "File not found", cancellationToken);
            return;
        }

        logger.LogInformation("Sending {Path} ({Length} bytes)", FtpFileSystem.Normalize(_directory, path), file.Length);
        await Transfer($"Opening BINARY mode data connection for {file.Name} ({file.Length} bytes)", async data =>
        {
            await using var content = file.Open();
            if (offset > 0)
                content.Seek(offset, SeekOrigin.Begin);

            await content.CopyToAsync(data, cancellationToken);
        }, cancellationToken);
    }

    private async Task List(string argument, bool namesOnly, CancellationToken cancellationToken)
    {
        while (argument.StartsWith('-'))
        {
            int separator = argument.IndexOf(' ');
            argument = separator < 0 ? string.Empty : argument[(separator + 1)..].TrimStart();
        }

        string target = FtpFileSystem.Normalize(_directory, argument);
        var (directory, name) = FtpFileSystem.Split(target);

        IEnumerable<FtpEntry> entries = name.AsSpan().IndexOfAny('*', '?') >= 0
            ? fileSystem.List(directory, name)
            : fileSystem.Find(target) switch
            {
                { IsDirectory: true } => fileSystem.List(target),
                { } entry             => [entry],
                null                  => []
            };

        var listing = new StringBuilder();
        foreach (var entry in entries)
            listing.Append(namesOnly ? $"{entry.Name}\r\n" : FormatEntry(entry));

        byte[] content = Encoding.UTF8.GetBytes(listing.ToString());
        await Transfer("Opening data connection for directory listing",
            data => data.WriteAsync(content, cancellationToken).AsTask(), cancellationToken);
    }

    private async Task Transfer(string message, Func<Stream, Task> send, CancellationToken cancellationToken)
    {
        if (_passive == null)
        {
            await Reply(425, "Use PASV or EPSV first", cancellationToken);
            return;
        }

        var listener = _passive;
        _passive = null;

        try
        {
            await Reply(150, message, cancellationToken);

            using var connection = await Accept(listener, cancellationToken);
            if (connection == null)
            {
                await Reply(425, "Can't open data connection", cancellationToken);
                return;
            }

            await using Stream data = _protected
                ? await Authenticate(connection.GetStream(), cancellationToken)
                : connection.GetStream();

            await send(data);
            if (data is SslStream ssl)
                await ssl.ShutdownAsync();

            await Reply(226, "Transfer complete", cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
                                       OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Data connection failed");
            await Reply(426, "Connection closed; transfer aborted", cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<TcpClient?> Accept(TcpListener listener, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        while (true)
        {
            TcpClient connection;
            try
            {
                connection = await listener.AcceptTcpClientAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            // Only the peer that owns the control connection may use the data port
            if (Unmap(((IPEndPoint)connection.Client.RemoteEndPoint!).Address).Equals(_remoteAddress))
                return connection;

            logger.LogWarning("Rejected data connection from {Address}", connection.Client.RemoteEndPoint);
            connection.Dispose();
        }
    }

    private FtpEntry? Find(string path)
        => fileSystem.Find(FtpFileSystem.Normalize(_directory, path));

    private static string FormatEntry(FtpEntry entry)
    {
        var time = entry.LastWriteTimeUtc;
        string date = time > DateTime.UtcNow.AddDays(-180) && time < DateTime.UtcNow.AddDays(1)
            ? time.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
            : time.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);

        string mode = entry.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--";
        return $"{mode} 1 ftp ftp {entry.Length,12} {date} {entry.Name}\r\n";
    }

    private async Task<string?> ReadLine(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[1];

        while (await _reader.ReadAsync(buffer, cancellationToken) > 0)
        {
            if (buffer[0] == '\n')
                return line.ToString().TrimEnd('\r');

            if (line.Length >= MaxLineLength)
                throw new InvalidDataException("Command exceeds maximum length");

            line.Append(buffer[0]);
        }

        return null;
    }

    private Task Reply(int code, string message, CancellationToken cancellationToken)
        => Write($"{code} {message}\r\n", cancellationToken);

    private async Task Write(string text, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private static IPAddress Unmap(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public async ValueTask DisposeAsync()
    {
        _passive?.Stop();
        _reader?.Dispose();
        await _stream.DisposeAsync();
    }
}
