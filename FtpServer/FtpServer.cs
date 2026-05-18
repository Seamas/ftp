using System.Net;
using System.Net.Sockets;

namespace FtpServer;

public class FtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, string> _users;
    private readonly string _rootPath;
    private readonly List<FtpSession> _sessions = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private bool _isDisposed;

    public FtpServer(string rootPath, int port = 21)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
        _users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void AddUser(string username, string password)
    {
        _users[username] = password;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            return;
        }
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _isRunning = true;
        Console.WriteLine($"FTP Server started on port {((IPEndPoint)_listener.LocalEndpoint).Port}");
        Console.WriteLine($"Root directory: {_rootPath}");

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var session = new FtpSession(client, _rootPath, _users);
        lock (_sessions)
        {
            _sessions.Add(session);
        }

        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            lock (_sessions)
            {
                _sessions.Remove(session);
            }
            session.Dispose();
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        _listener?.Stop();

        lock (_sessions)
        {
            foreach (var session in _sessions)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        
        _isDisposed = true;
        
        Stop();
        _listener.Dispose();
        _cts?.Dispose();
    }
}
