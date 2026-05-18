using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FtpServer;

public class FtpSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _rootPath;
    private readonly Dictionary<string, string> _users;
    private string _currentDirectory = "/";
    private string _username;
    private bool _isAuthenticated;
    private TcpListener _passiveListener;
    private TcpClient _dataClient;
    private IPEndPoint _dataEndpoint;
    private TransferMode _transferMode = TransferMode.Active;
    private long _restartPosition;
    private Encoding _encoding = Encoding.UTF8;
    private bool _disposed;

    public FtpSession(TcpClient client, string rootPath, Dictionary<string, string> users)
    {
        _client = client;
        _rootPath = rootPath;
        _users = users;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendResponseAsync(220, "Welcome to C# FTP Server");

            while (!cancellationToken.IsCancellationRequested && _client.Connected)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null) break;

                Console.WriteLine($"[{_username ?? "anonymous"}] {line}");
                await ProcessCommandAsync(line, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Session error: {ex.Message}");
        }
        finally
        {
            Dispose();
        }
    }

    private async Task ProcessCommandAsync(string line, CancellationToken cancellationToken)
    {
        var parts = line.Split(new[] { ' ' }, 2);
        var command = parts[0].ToUpperInvariant();
        var argument = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "USER":
                await HandleUserAsync(argument);
                break;
            case "PASS":
                await HandlePassAsync(argument);
                break;
            case "QUIT":
                await HandleQuitAsync();
                break;
            case "PWD":
                await HandlePwdAsync();
                break;
            case "CWD":
                await HandleCwdAsync(argument);
                break;
            case "CDUP":
                await HandleCdupAsync();
                break;
            case "TYPE":
                await HandleTypeAsync(argument);
                break;
            case "PASV":
                await HandlePasvAsync();
                break;
            case "PORT":
                await HandlePortAsync(argument);
                break;
            case "LIST":
                await HandleListAsync(argument, cancellationToken);
                break;
            case "NLST":
                await HandleNlstAsync(argument, cancellationToken);
                break;
            case "RETR":
                await HandleRetrAsync(argument, cancellationToken);
                break;
            case "STOR":
                await HandleStorAsync(argument, cancellationToken);
                break;
            case "APPE":
                await HandleAppeAsync(argument, cancellationToken);
                break;
            case "DELE":
                await HandleDeleAsync(argument);
                break;
            case "RMD":
                await HandleRmdAsync(argument);
                break;
            case "MKD":
                await HandleMkdAsync(argument);
                break;
            case "RNFR":
                await HandleRnfrAsync(argument);
                break;
            case "RNTO":
                await HandleRntoAsync(argument);
                break;
            case "REST":
                await HandleRestAsync(argument);
                break;
            case "SIZE":
                await HandleSizeAsync(argument);
                break;
            case "SYST":
                await SendResponseAsync(215, "UNIX Type: L8");
                break;
            case "FEAT":
                await HandleFeatAsync();
                break;
            case "OPTS":
                await HandleOptsAsync(argument);
                break;
            case "NOOP":
                await SendResponseAsync(200, "OK");
                break;
            default:
                await SendResponseAsync(502, "Command not implemented");
                break;
        }
    }

    private async Task HandleUserAsync(string username)
    {
        _username = username;
        await SendResponseAsync(331, "Please specify the password");
    }

    private async Task HandlePassAsync(string password)
    {
        if (_username == null)
        {
            await SendResponseAsync(503, "Login with USER first");
            return;
        }

        if (_users.TryGetValue(_username, out var expectedPassword) && expectedPassword == password)
        {
            _isAuthenticated = true;
            await SendResponseAsync(230, "Login successful");
        }
        else
        {
            await SendResponseAsync(530, "Login incorrect");
        }
    }

    private async Task HandleQuitAsync()
    {
        await SendResponseAsync(221, "Goodbye");
        _client.Close();
    }

    private async Task HandlePwdAsync()
    {
        if (!CheckAuthentication()) return;
        await SendResponseAsync(257, $"\"{_currentDirectory}\" is current directory");
    }

    private async Task HandleCwdAsync(string path)
    {
        if (!CheckAuthentication()) return;

        var newPath = ResolvePath(path);
        var fullPath = GetFullPath(newPath);

        if (Directory.Exists(fullPath))
        {
            _currentDirectory = newPath;
            await SendResponseAsync(250, "Directory successfully changed");
        }
        else
        {
            await SendResponseAsync(550, "Failed to change directory");
        }
    }

    private async Task HandleCdupAsync()
    {
        if (!CheckAuthentication()) return;
        await HandleCwdAsync("..");
    }

    private async Task HandleTypeAsync(string type)
    {
        if (!CheckAuthentication()) return;

        switch (type.ToUpper())
        {
            case "I":
                await SendResponseAsync(200, "Switching to Binary mode");
                break;
            case "A":
                await SendResponseAsync(200, "Switching to ASCII mode");
                break;
            default:
                await SendResponseAsync(504, "Command not implemented for that parameter");
                break;
        }
    }

    private async Task HandlePasvAsync()
    {
        if (!CheckAuthentication()) return;

        _passiveListener?.Stop();
        _passiveListener = new TcpListener(IPAddress.Any, 0);
        _passiveListener.Start();

        var endpoint = (IPEndPoint)_passiveListener.LocalEndpoint;
        var ip = _client.Client.LocalEndPoint is IPEndPoint localEp 
            ? localEp.Address 
            : IPAddress.Parse("127.0.0.1");

        _transferMode = TransferMode.Passive;
        var response = $"Entering Passive Mode ({ip.GetAddressBytes()[0]},{ip.GetAddressBytes()[1]},{ip.GetAddressBytes()[2]},{ip.GetAddressBytes()[3]},{endpoint.Port / 256},{endpoint.Port % 256})";
        await SendResponseAsync(227, response);
    }

    private async Task HandlePortAsync(string argument)
    {
        if (!CheckAuthentication()) return;

        var parts = argument.Split(',');
        if (parts.Length != 6)
        {
            await SendResponseAsync(501, "Syntax error in parameters");
            return;
        }

        var ip = string.Join(".", parts[0], parts[1], parts[2], parts[3]);
        var port = int.Parse(parts[4]) * 256 + int.Parse(parts[5]);

        _dataEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        _transferMode = TransferMode.Active;
        await SendResponseAsync(200, "PORT command successful");
    }

    private async Task HandleListAsync(string argument, CancellationToken cancellationToken)
    {
        if (!CheckAuthentication()) return;

        var path = string.IsNullOrEmpty(argument) ? _currentDirectory : ResolvePath(argument);
        var fullPath = GetFullPath(path);

        if (!Directory.Exists(fullPath))
        {
            await SendResponseAsync(450, "Directory not found");
            return;
        }

        await SendResponseAsync(150, "Opening data connection");

        try
        {
            using var dataStream = await OpenDataConnectionAsync(cancellationToken);
            using var writer = new StreamWriter(dataStream, _encoding);

            var entries = Directory.GetFileSystemEntries(fullPath);
            foreach (var entry in entries)
            {
                var info = new FileInfo(entry);
                var isDirectory = Directory.Exists(entry);
                var permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--";
                var size = isDirectory ? 0 : info.Length;
                var date = info.LastWriteTime.ToString("MMM dd HH:mm");
                var name = Path.GetFileName(entry);

                await writer.WriteLineAsync($"{permissions} 1 owner group {size,8} {date} {name}");
            }

            await writer.FlushAsync(cancellationToken);
            await SendResponseAsync(226, "Transfer complete");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(426, $"Connection closed; transfer aborted: {ex.Message}");
        }
    }

    private async Task HandleNlstAsync(string argument, CancellationToken cancellationToken)
    {
        if (!CheckAuthentication()) return;

        var path = string.IsNullOrEmpty(argument) ? _currentDirectory : ResolvePath(argument);
        var fullPath = GetFullPath(path);

        if (!Directory.Exists(fullPath))
        {
            await SendResponseAsync(450, "Directory not found");
            return;
        }

        await SendResponseAsync(150, "Opening data connection");

        try
        {
            using var dataStream = await OpenDataConnectionAsync(cancellationToken);
            using var writer = new StreamWriter(dataStream, _encoding);

            var entries = Directory.GetFileSystemEntries(fullPath);
            foreach (var entry in entries)
            {
                await writer.WriteLineAsync(Path.GetFileName(entry));
            }

            await writer.FlushAsync();
            await SendResponseAsync(226, "Transfer complete");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(426, $"Connection closed; transfer aborted: {ex.Message}");
        }
    }

    private async Task HandleRetrAsync(string filename, CancellationToken cancellationToken)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(filename));

        if (!File.Exists(fullPath))
        {
            await SendResponseAsync(550, "File not found");
            return;
        }

        var fileInfo = new FileInfo(fullPath);
        await SendResponseAsync(150, $"Opening data connection for {filename} ({fileInfo.Length} bytes)");

        try
        {
            using var dataStream = await OpenDataConnectionAsync(cancellationToken);
            using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (_restartPosition > 0)
            {
                fileStream.Seek(_restartPosition, SeekOrigin.Begin);
                _restartPosition = 0;
            }

            await fileStream.CopyToAsync(dataStream, cancellationToken);
            await SendResponseAsync(226, "Transfer complete");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(426, $"Connection closed; transfer aborted: {ex.Message}");
        }
    }

    private async Task HandleStorAsync(string filename, CancellationToken cancellationToken)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(filename));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        await SendResponseAsync(150, "Opening data connection");

        try
        {
            using var dataStream = await OpenDataConnectionAsync(cancellationToken);

            var mode = _restartPosition > 0 ? FileMode.Append : FileMode.Create;
            using var fileStream = new FileStream(fullPath, mode, FileAccess.Write, FileShare.None);

            if (_restartPosition > 0)
            {
                fileStream.Seek(_restartPosition, SeekOrigin.Begin);
                _restartPosition = 0;
            }

            await dataStream.CopyToAsync(fileStream, cancellationToken);
            await SendResponseAsync(226, "Transfer complete");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(426, $"Connection closed; transfer aborted: {ex.Message}");
        }
    }

    private async Task HandleAppeAsync(string filename, CancellationToken cancellationToken)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(filename));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        await SendResponseAsync(150, "Opening data connection");

        try
        {
            using var dataStream = await OpenDataConnectionAsync(cancellationToken);
            using var fileStream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None);

            await dataStream.CopyToAsync(fileStream, cancellationToken);
            await SendResponseAsync(226, "Transfer complete");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(426, $"Connection closed; transfer aborted: {ex.Message}");
        }
    }

    private async Task HandleDeleAsync(string filename)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(filename));

        if (!File.Exists(fullPath))
        {
            await SendResponseAsync(550, "File not found");
            return;
        }

        try
        {
            File.Delete(fullPath);
            await SendResponseAsync(250, "Delete operation successful");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(450, $"Delete operation failed: {ex.Message}");
        }
    }

    private async Task HandleRmdAsync(string dirname)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(dirname));

        if (!Directory.Exists(fullPath))
        {
            await SendResponseAsync(550, "Directory not found");
            return;
        }

        try
        {
            Directory.Delete(fullPath);
            await SendResponseAsync(250, "Remove directory operation successful");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(450, $"Remove directory operation failed: {ex.Message}");
        }
    }

    private async Task HandleMkdAsync(string dirname)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(dirname));

        try
        {
            Directory.CreateDirectory(fullPath);
            await SendResponseAsync(257, $"\"{dirname}\" created");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(550, $"Create directory operation failed: {ex.Message}");
        }
    }

    private string _renameFrom;

    private async Task HandleRnfrAsync(string filename)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(filename));

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            await SendResponseAsync(550, "File or directory not found");
            return;
        }

        _renameFrom = fullPath;
        await SendResponseAsync(350, "Ready for RNTO");
    }

    private async Task HandleRntoAsync(string filename)
    {
        if (!CheckAuthentication()) return;

        if (_renameFrom == null)
        {
            await SendResponseAsync(503, "Send RNFR first");
            return;
        }

        var fullPath = GetFullPath(ResolvePath(filename));

        try
        {
            if (File.Exists(_renameFrom))
            {
                File.Move(_renameFrom, fullPath);
            }
            else
            {
                Directory.Move(_renameFrom, fullPath);
            }
            await SendResponseAsync(250, "Rename successful");
        }
        catch (Exception ex)
        {
            await SendResponseAsync(550, $"Rename failed: {ex.Message}");
        }
        finally
        {
            _renameFrom = null;
        }
    }

    private async Task HandleRestAsync(string position)
    {
        if (!CheckAuthentication()) return;

        if (long.TryParse(position, out var pos) && pos >= 0)
        {
            _restartPosition = pos;
            await SendResponseAsync(350, "Restart position accepted");
        }
        else
        {
            await SendResponseAsync(501, "Syntax error in parameters");
        }
    }

    private async Task HandleSizeAsync(string filename)
    {
        if (!CheckAuthentication()) return;

        var fullPath = GetFullPath(ResolvePath(filename));

        if (!File.Exists(fullPath))
        {
            await SendResponseAsync(550, "File not found");
            return;
        }

        var info = new FileInfo(fullPath);
        await SendResponseAsync(213, info.Length.ToString());
    }

    private async Task HandleFeatAsync()
    {
        await _writer.WriteLineAsync("211-Features:");
        await _writer.WriteLineAsync(" SIZE");
        await _writer.WriteLineAsync(" REST STREAM");
        await _writer.WriteLineAsync(" UTF8");
        await _writer.WriteLineAsync("211 End");
    }

    private async Task HandleOptsAsync(string argument)
    {
        if (argument.ToUpper().StartsWith("UTF8 "))
        {
            _encoding = Encoding.UTF8;
            await SendResponseAsync(200, "UTF8 mode enabled");
        }
        else
        {
            await SendResponseAsync(501, "Option not recognized");
        }
    }

    private async Task<Stream> OpenDataConnectionAsync(CancellationToken cancellationToken)
    {
        if (_transferMode == TransferMode.Passive)
        {
            var client = await _passiveListener.AcceptTcpClientAsync();
            _dataClient = client;
            return client.GetStream();
        }
        else
        {
            var client = new TcpClient();
            await client.ConnectAsync(_dataEndpoint.Address, _dataEndpoint.Port);
            _dataClient = client;
            return client.GetStream();
        }
    }

    private async Task SendResponseAsync(int code, string message)
    {
        var response = $"{code} {message}";
        Console.WriteLine($"Server: {response}");
        await _writer.WriteLineAsync(response);
    }

    private bool CheckAuthentication()
    {
        if (!_isAuthenticated)
        {
            _writer.WriteLineAsync("530 Please login with USER and PASS").Wait();
            return false;
        }
        return true;
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == ".")
            return _currentDirectory;

        if (path.StartsWith("/"))
            return path;

        var combined = Path.Combine(_currentDirectory, path);
        var resolved = Path.GetFullPath(Path.Combine(_rootPath, combined));
        var rootFullPath = Path.GetFullPath(_rootPath);

        if (!resolved.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
            return "/";

        return combined.Replace('\\', '/');
    }

    private string GetFullPath(string virtualPath)
    {
        var relativePath = virtualPath.TrimStart('/').Replace('/', '\\');
        return Path.Combine(_rootPath, relativePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dataClient?.Dispose();
        _passiveListener?.Stop();
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
    }

    private enum TransferMode
    {
        Active,
        Passive
    }
}
