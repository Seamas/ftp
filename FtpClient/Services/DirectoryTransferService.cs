using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FtpClient.Models;

namespace FtpClient.Services;

public class DirectoryTransferService
{
    private readonly FtpClientService _ftpClient;

    public DirectoryTransferService(FtpClientService ftpClient)
    {
        _ftpClient = ftpClient;
    }

    public async Task<bool> DownloadDirectoryAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(localPath);

            var items = await _ftpClient.ListDirectoryAsync(remotePath);

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                var localItemPath = Path.Combine(localPath, item.Name);
                var remoteItemPath = item.FullPath;

                if (item.IsDirectory)
                {
                    var success = await DownloadDirectoryAsync(remoteItemPath, localItemPath, progress, cancellationToken);
                    if (!success) return false;
                }
                else
                {
                    // Check if file exists and get resume position
                    long resumePosition = 0;
                    if (File.Exists(localItemPath))
                    {
                        var existingFile = new FileInfo(localItemPath);
                        var remoteSize = await _ftpClient.GetFileSizeAsync(remoteItemPath);
                        if (existingFile.Length < remoteSize)
                        {
                            resumePosition = existingFile.Length;
                        }
                        else if (existingFile.Length == remoteSize)
                        {
                            // File already complete
                            continue;
                        }
                    }

                    var success = await _ftpClient.DownloadFileAsync(remoteItemPath, localItemPath, resumePosition, progress, cancellationToken);
                    if (!success) return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Download directory error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UploadDirectoryAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Create remote directory
            await _ftpClient.CreateDirectoryAsync(remotePath);

            var entries = Directory.GetFileSystemEntries(localPath);

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                var name = Path.GetFileName(entry);
                var remoteItemPath = $"{remotePath.TrimEnd('/')}/{name}";

                if (Directory.Exists(entry))
                {
                    var success = await UploadDirectoryAsync(entry, remoteItemPath, progress, cancellationToken);
                    if (!success) return false;
                }
                else
                {
                    // Check if file exists on server for resume
                    var localFile = new FileInfo(entry);
                    var remoteSize = await _ftpClient.GetFileSizeAsync(remoteItemPath);
                    
                    if (remoteSize > 0 && remoteSize < localFile.Length)
                    {
                        // Resume upload - we need to use APPEND mode
                        // Note: Standard FTP doesn't support resume for uploads well
                        // We'll just overwrite for now, or skip if sizes match
                        if (remoteSize == localFile.Length)
                        {
                            continue; // File already complete
                        }
                    }

                    var success = await _ftpClient.UploadFileAsync(entry, remoteItemPath, false, progress, cancellationToken);
                    if (!success) return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Upload directory error: {ex.Message}");
            return false;
        }
    }

    public async Task<List<string>> GetDirectoryStructureAsync(string remotePath)
    {
        var structure = new List<string>();
        await GetDirectoryStructureRecursiveAsync(remotePath, structure);
        return structure;
    }

    private async Task GetDirectoryStructureRecursiveAsync(string remotePath, List<string> structure)
    {
        var items = await _ftpClient.ListDirectoryAsync(remotePath);

        foreach (var item in items)
        {
            structure.Add(item.FullPath);

            if (item.IsDirectory)
            {
                await GetDirectoryStructureRecursiveAsync(item.FullPath, structure);
            }
        }
    }
}
