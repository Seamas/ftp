using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FtpClient.Models;

namespace FtpClient.Services;

public class FtpClientService : IDisposable
{
    private TcpClient? _controlClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string _currentDirectory = "/";
    private IPEndPoint? _passiveEndpoint;
    private readonly Encoding _encoding = Encoding.UTF8;
    private bool _disposed;

    public bool IsConnected => _controlClient?.Connected ?? false;
    public string CurrentDirectory => _currentDirectory;

    public async Task<bool> ConnectAsync(string host, int port, string username, string password)
    {
        try
        {
            _controlClient = new TcpClient();
            await _controlClient.ConnectAsync(host, port);

            var stream = _controlClient.GetStream();
            _reader = new StreamReader(stream, Encoding.ASCII);
            _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

            var response = await ReadResponseAsync();
            if (!response.StartsWith("220")) return false;

            // Send USER
            await SendCommandAsync($"USER {username}");
            response = await ReadResponseAsync();
            if (!response.StartsWith("331")) return false;

            // Send PASS
            await SendCommandAsync($"PASS {password}");
            response = await ReadResponseAsync();
            if (!response.StartsWith("230")) return false;

            // Set binary mode
            await SendCommandAsync("TYPE I");
            response = await ReadResponseAsync();

            // Enable UTF8
            await SendCommandAsync("OPTS UTF8 ON");
            response = await ReadResponseAsync();

            _currentDirectory = "/";
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_controlClient?.Connected == true)
        {
            await SendCommandAsync("QUIT");
            await ReadResponseAsync();
        }
        Dispose();
    }

    public async Task<List<FtpItem>> ListDirectoryAsync(string path = "")
    {
        var items = new List<FtpItem>();
        var targetPath = string.IsNullOrEmpty(path) ? _currentDirectory : path;

        try
        {
            // Enter passive mode
            if (!await EnterPassiveModeAsync())
                return items;

            await SendCommandAsync($"LIST {targetPath}");
            var response = await ReadResponseAsync();

            if (!response.StartsWith("150") && !response.StartsWith("125"))
                return items;

            // Connect data connection
            using var dataClient = new TcpClient();
            await dataClient.ConnectAsync(_passiveEndpoint!.Address, _passiveEndpoint.Port);
            using var dataStream = dataClient.GetStream();
            using var dataReader = new StreamReader(dataStream, _encoding);

            // Read directory listing
            string line;
            while ((line = await dataReader.ReadLineAsync()) != null)
            {
                var item = ParseListLine(line, targetPath);
                if (item != null)
                    items.Add(item);
            }

            response = await ReadResponseAsync();
            if (!response.StartsWith("226"))
                Console.WriteLine($"List completed with warning: {response}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"List error: {ex.Message}");
        }

        return items;
    }

    public async Task<bool> ChangeDirectoryAsync(string path)
    {
        try
        {
            await SendCommandAsync($"CWD {path}");
            var response = await ReadResponseAsync();

            if (response.StartsWith("250"))
            {
                _currentDirectory = path.StartsWith("/") ? path : $"{_currentDirectory.TrimEnd('/')}/{path}";
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CWD error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CreateDirectoryAsync(string path)
    {
        try
        {
            await SendCommandAsync($"MKD {path}");
            var response = await ReadResponseAsync();
            return response.StartsWith("257");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MKD error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeleteDirectoryAsync(string path)
    {
        try
        {
            await SendCommandAsync($"RMD {path}");
            var response = await ReadResponseAsync();
            return response.StartsWith("250");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RMD error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeleteFileAsync(string path)
    {
        try
        {
            await SendCommandAsync($"DELE {path}");
            var response = await ReadResponseAsync();
            return response.StartsWith("250");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DELE error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RenameAsync(string fromPath, string toPath)
    {
        try
        {
            await SendCommandAsync($"RNFR {fromPath}");
            var response = await ReadResponseAsync();

            if (!response.StartsWith("350"))
                return false;

            await SendCommandAsync($"RNTO {toPath}");
            response = await ReadResponseAsync();
            return response.StartsWith("250");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Rename error: {ex.Message}");
            return false;
        }
    }

    public async Task<long> GetFileSizeAsync(string path)
    {
        try
        {
            await SendCommandAsync($"SIZE {path}");
            var response = await ReadResponseAsync();

            if (response.StartsWith("213"))
            {
                var parts = response.Split(' ');
                if (parts.Length >= 2 && long.TryParse(parts[1], out var size))
                    return size;
            }
            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SIZE error: {ex.Message}");
            return -1;
        }
    }

    public async Task<bool> DownloadFileAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await DownloadFileAsync(remotePath, localPath, 0, progress, cancellationToken);
    }

    public async Task<bool> DownloadFileAsync(string remotePath, string localPath, long resumePosition, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(remotePath);
        var transferProgress = new TransferProgress
        {
            FileName = fileName,
            SourcePath = remotePath,
            DestinationPath = localPath,
            Direction = TransferDirection.Download,
            Status = TransferStatus.InProgress
        };

        try
        {
            var fileSize = await GetFileSizeAsync(remotePath);
            transferProgress.TotalBytes = fileSize;

            // Enter passive mode
            if (!await EnterPassiveModeAsync())
            {
                transferProgress.Status = TransferStatus.Failed;
                transferProgress.ErrorMessage = "Failed to enter passive mode";
                progress?.Report(transferProgress);
                return false;
            }

            // Set resume position if needed
            if (resumePosition > 0)
            {
                await SendCommandAsync($"REST {resumePosition}");
                var response = await ReadResponseAsync();
                if (!response.StartsWith("350"))
                {
                    resumePosition = 0;
                }
            }

            await SendCommandAsync($"RETR {remotePath}");
            var retrResponse = await ReadResponseAsync();

            if (!retrResponse.StartsWith("150") && !retrResponse.StartsWith("125"))
            {
                transferProgress.Status = TransferStatus.Failed;
                transferProgress.ErrorMessage = "RETR command failed";
                progress?.Report(transferProgress);
                return false;
            }

            // Connect data connection
            using var dataClient = new TcpClient();
            await dataClient.ConnectAsync(_passiveEndpoint!.Address, _passiveEndpoint.Port);
            using var dataStream = dataClient.GetStream();

            // Open local file
            var mode = resumePosition > 0 ? FileMode.Append : FileMode.Create;
            using var fileStream = new FileStream(localPath, mode, FileAccess.Write, FileShare.None);

            // Copy with progress
            var buffer = new byte[8192];
            long totalRead = resumePosition;
            int read;

            while ((read = await dataStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                totalRead += read;
                transferProgress.UpdateProgress(totalRead);
                progress?.Report(transferProgress);
            }

            var finalResponse = await ReadResponseAsync();
            if (finalResponse.StartsWith("226"))
            {
                transferProgress.Status = TransferStatus.Completed;
                transferProgress.UpdateProgress(totalRead);
                progress?.Report(transferProgress);
                return true;
            }
            else
            {
                transferProgress.Status = TransferStatus.Failed;
                transferProgress.ErrorMessage = finalResponse;
                progress?.Report(transferProgress);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            transferProgress.Status = TransferStatus.Cancelled;
            progress?.Report(transferProgress);
            return false;
        }
        catch (Exception ex)
        {
            transferProgress.Status = TransferStatus.Failed;
            transferProgress.ErrorMessage = ex.Message;
            progress?.Report(transferProgress);
            return false;
        }
    }

    public async Task<bool> UploadFileAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await UploadFileAsync(localPath, remotePath, false, progress, cancellationToken);
    }

    public async Task<bool> UploadFileAsync(string localPath, string remotePath, bool append, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(localPath);
        var fileInfo = new FileInfo(localPath);
        var transferProgress = new TransferProgress
        {
            FileName = fileName,
            SourcePath = localPath,
            DestinationPath = remotePath,
            TotalBytes = fileInfo.Length,
            Direction = TransferDirection.Upload,
            Status = TransferStatus.InProgress
        };

        try
        {
            // Enter passive mode
            if (!await EnterPassiveModeAsync())
            {
                transferProgress.Status = TransferStatus.Failed;
                transferProgress.ErrorMessage = "Failed to enter passive mode";
                progress?.Report(transferProgress);
                return false;
            }

            var command = append ? $"APPE {remotePath}" : $"STOR {remotePath}";
            await SendCommandAsync(command);
            var response = await ReadResponseAsync();

            if (!response.StartsWith("150") && !response.StartsWith("125"))
            {
                transferProgress.Status = TransferStatus.Failed;
                transferProgress.ErrorMessage = "STOR/APPE command failed";
                progress?.Report(transferProgress);
                return false;
            }

            // Connect data connection
            using var dataClient = new TcpClient();
            await dataClient.ConnectAsync(_passiveEndpoint!.Address, _passiveEndpoint.Port);
            using var dataStream = dataClient.GetStream();

            // Open local file
            using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Copy with progress
            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await dataStream.WriteAsync(buffer, 0, read, cancellationToken);
                totalRead += read;
                transferProgress.UpdateProgress(totalRead);
                progress?.Report(transferProgress);
            }

            await dataStream.FlushAsync(cancellationToken);
            dataClient.Close();

            var finalResponse = await ReadResponseAsync();
            if (finalResponse.StartsWith("226"))
            {
                transferProgress.Status = TransferStatus.Completed;
                transferProgress.UpdateProgress(totalRead);
                progress?.Report(transferProgress);
                return true;
            }
            else
            {
                transferProgress.Status = TransferStatus.Failed;
                transferProgress.ErrorMessage = finalResponse;
                progress?.Report(transferProgress);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            transferProgress.Status = TransferStatus.Cancelled;
            progress?.Report(transferProgress);
            return false;
        }
        catch (Exception ex)
        {
            transferProgress.Status = TransferStatus.Failed;
            transferProgress.ErrorMessage = ex.Message;
            progress?.Report(transferProgress);
            return false;
        }
    }

    private async Task<bool> EnterPassiveModeAsync()
    {
        try
        {
            await SendCommandAsync("PASV");
            var response = await ReadResponseAsync();

            if (!response.StartsWith("227"))
                return false;

            // Parse PASV response: 227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)
            var match = Regex.Match(response, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
            if (!match.Success)
                return false;

            var ip = string.Join(".", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
            var port = int.Parse(match.Groups[5].Value) * 256 + int.Parse(match.Groups[6].Value);

            _passiveEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PASV error: {ex.Message}");
            return false;
        }
    }

    private async Task SendCommandAsync(string command)
    {
        if (_writer == null) throw new InvalidOperationException("Not connected");
        Console.WriteLine($"Client: {command}");
        await _writer.WriteLineAsync(command);
    }

    private async Task<string> ReadResponseAsync()
    {
        if (_reader == null) throw new InvalidOperationException("Not connected");

        var response = new StringBuilder();
        string line;

        do
        {
            line = await _reader.ReadLineAsync();
            if (line == null) break;
            Console.WriteLine($"Server: {line}");
            response.AppendLine(line);
        } while (line.Length >= 4 && line[3] == '-');

        return response.ToString().Trim();
    }

    private FtpItem? ParseListLine(string line, string parentPath)
    {
        try
        {
            // Unix style: drwxr-xr-x 1 owner group 1234 Jan 01 12:00 filename
            // Windows style: 01-01-24 12:00AM <DIR> dirname
            //                 01-01-24 12:00AM 1234 filename

            FtpItem item;

            if (line.Contains("<DIR>"))
            {
                // Windows directory
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return null;

                item = new FtpItem
                {
                    Type = FtpItemType.Directory,
                    Name = string.Join(" ", parts.Skip(3)),
                    FullPath = $"{parentPath.TrimEnd('/')}/{string.Join(" ", parts.Skip(3))}",
                    ModifiedTime = DateTime.Now
                };
            }
            else if (line.StartsWith("d") || line.StartsWith("-"))
            {
                // Unix style
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 9) return null;

                var isDirectory = parts[0].StartsWith("d");
                var size = isDirectory ? 0 : long.Parse(parts[4]);
                var name = string.Join(" ", parts.Skip(8));

                item = new FtpItem
                {
                    Type = isDirectory ? FtpItemType.Directory : FtpItemType.File,
                    Name = name,
                    FullPath = $"{parentPath.TrimEnd('/')}/{name}",
                    Size = size,
                    Permissions = parts[0],
                    ModifiedTime = ParseUnixDate(parts[5], parts[6], parts[7])
                };
            }
            else
            {
                // Windows file
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return null;

                var size = long.Parse(parts[2]);
                var name = string.Join(" ", parts.Skip(3));

                item = new FtpItem
                {
                    Type = FtpItemType.File,
                    Name = name,
                    FullPath = $"{parentPath.TrimEnd('/')}/{name}",
                    Size = size,
                    ModifiedTime = DateTime.Now
                };
            }

            return item;
        }
        catch
        {
            return null;
        }
    }

    private DateTime ParseUnixDate(string month, string day, string timeOrYear)
    {
        try
        {
            var monthNum = Array.IndexOf(new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" }, month) + 1;
            var dayNum = int.Parse(day);

            int year;
            int hour = 0, minute = 0;

            if (timeOrYear.Contains(":"))
            {
                var timeParts = timeOrYear.Split(':');
                hour = int.Parse(timeParts[0]);
                minute = int.Parse(timeParts[1]);
                year = DateTime.Now.Year;
            }
            else
            {
                year = int.Parse(timeOrYear);
            }

            return new DateTime(year, monthNum, dayNum, hour, minute, 0);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reader?.Dispose();
        _writer?.Dispose();
        _controlClient?.Dispose();
    }
}
