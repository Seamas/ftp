using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FtpServer;

class Program
{
    static async Task Main(string[] args)
    {
        var rootPath = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "ftp_root");
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 21;

        using var server = new FtpServer(rootPath, port);

        // Add default user
        server.AddUser("admin", "admin");
        server.AddUser("user", "password");

        Console.WriteLine("FTP Server");
        Console.WriteLine("==========");
        Console.WriteLine("Default users:");
        Console.WriteLine("  Username: admin, Password: admin");
        Console.WriteLine("  Username: user, Password: password");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop the server");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await server.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Server stopped.");
        }
    }
}
