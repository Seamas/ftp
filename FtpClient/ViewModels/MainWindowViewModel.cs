using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FtpClient.Enums;
using FtpClient.Models;
using FtpClient.Services;

namespace FtpClient.ViewModels;

public interface IStorageProviderAccessor
{
    IStorageProvider? GetStorageProvider();
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FtpClientService _ftpClient;
    private readonly DirectoryTransferService _directoryTransfer;
    private CancellationTokenSource? _transferCts;

    [ObservableProperty]
    private string _serverAddress = "localhost";

    [ObservableProperty]
    private int _serverPort = 21;

    [ObservableProperty]
    private string _username = "admin";

    [ObservableProperty]
    private string _password = "admin";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    private string _localDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    private string _remoteDirectory = "/";

    [ObservableProperty]
    private ObservableCollection<FtpItem> _localItems = new();

    [ObservableProperty]
    private ObservableCollection<FtpItem> _remoteItems = new();

    [ObservableProperty]
    private FtpItem? _selectedLocalItem;

    [ObservableProperty]
    private FtpItem? _selectedRemoteItem;

    [ObservableProperty]
    private ObservableCollection<TransferProgress> _transfers = new();

    [ObservableProperty]
    private TransferProgress? _selectedTransfer;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public MainWindowViewModel()
    {
        _ftpClient = new FtpClientService();
        _directoryTransfer = new DirectoryTransferService(_ftpClient);
        RefreshLocalDirectory();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            ConnectionStatus = "Connecting...";
            StatusMessage = $"Connecting to {ServerAddress}:{ServerPort}...";

            var success = await _ftpClient.ConnectAsync(ServerAddress, ServerPort, Username, Password);

            if (success)
            {
                IsConnected = true;
                ConnectionStatus = "Connected";
                StatusMessage = "Connected successfully";
                await RefreshRemoteDirectoryAsync();
            }
            else
            {
                ConnectionStatus = "Connection failed";
                StatusMessage = "Failed to connect";
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Connection error";
            StatusMessage = $"Connection error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _ftpClient.DisconnectAsync();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            StatusMessage = "Disconnected";
            RemoteItems.Clear();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Disconnect error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BrowseLocalDirectoryAsync()
    {
        try
        {
            var storageProvider = GetStorageProvider();
            if (storageProvider == null) return;

            var options = new FolderPickerOpenOptions
            {
                Title = "Select Local Directory",
                AllowMultiple = false
            };

            var result = await storageProvider.OpenFolderPickerAsync(options);
            if (result.Count > 0)
            {
                LocalDirectory = result[0].Path.LocalPath;
                RefreshLocalDirectory();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Browse error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NavigateLocalUp()
    {
        var parent = Directory.GetParent(LocalDirectory);
        if (parent != null)
        {
            LocalDirectory = parent.FullName;
            RefreshLocalDirectory();
        }
    }

    [RelayCommand]
    private void NavigateLocalTo(FtpItem item)
    {
        if (item.IsDirectory)
        {
            LocalDirectory = Path.Combine(LocalDirectory, item.Name);
            RefreshLocalDirectory();
        }
    }

    [RelayCommand]
    private async Task NavigateRemoteUpAsync()
    {
        if (RemoteDirectory == "/") return;

        var parent = RemoteDirectory.TrimEnd('/');
        var lastSlash = parent.LastIndexOf('/');
        if (lastSlash > 0)
        {
            RemoteDirectory = parent.Substring(0, lastSlash);
        }
        else
        {
            RemoteDirectory = "/";
        }

        await RefreshRemoteDirectoryAsync();
    }

    [RelayCommand]
    private async Task NavigateRemoteToAsync(FtpItem item)
    {
        if (item.IsDirectory)
        {
            RemoteDirectory = item.FullPath;
            await RefreshRemoteDirectoryAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshRemoteDirectoryAsync()
    {
        if (!IsConnected) return;

        try
        {
            StatusMessage = $"Refreshing {RemoteDirectory}...";
            var items = await _ftpClient.ListDirectoryAsync(RemoteDirectory);
            
            RemoteItems.Clear();
            foreach (var ftpItem in items)
            {
                RemoteItems.Add(ftpItem);
            }
            // RemoteItems = new ObservableCollection<FtpItem>(items);
            StatusMessage = "Directory refreshed";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshLocalDirectory()
    {
        try
        {
            var items = new ObservableCollection<FtpItem>();

            // Add parent directory entry
            if (Directory.GetParent(LocalDirectory) != null)
            {
                items.Add(new FtpItem
                {
                    Name = "..",
                    FullPath = Directory.GetParent(LocalDirectory)?.FullName ?? "",
                    Type = FtpItemType.Directory
                });
            }

            // Add directories
            foreach (var dir in Directory.GetDirectories(LocalDirectory))
            {
                var info = new DirectoryInfo(dir);
                items.Add(new FtpItem
                {
                    Name = info.Name,
                    FullPath = dir,
                    Type = FtpItemType.Directory,
                    ModifiedTime = info.LastWriteTime
                });
            }

            // Add files
            foreach (var file in Directory.GetFiles(LocalDirectory))
            {
                var info = new FileInfo(file);
                items.Add(new FtpItem
                {
                    Name = info.Name,
                    FullPath = file,
                    Type = FtpItemType.File,
                    Size = info.Length,
                    ModifiedTime = info.LastWriteTime
                });
            }

            LocalItems = items;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Local refresh error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (SelectedLocalItem == null || !IsConnected) return;

        _transferCts = new CancellationTokenSource();

        try
        {
            var remotePath = $"{RemoteDirectory.TrimEnd('/')}/{SelectedLocalItem.Name}";

            if (SelectedLocalItem.IsDirectory)
            {
                var progress = new TransferProgress
                {
                    FileName = SelectedLocalItem.Name,
                    SourcePath = SelectedLocalItem.FullPath,
                    DestinationPath = remotePath,
                    Direction = TransferDirection.Upload,
                    Status = TransferStatus.InProgress
                };
                Transfers.Add(progress);

                var progressReporter = new Progress<TransferProgress>(p =>
                {
                    progress.UpdateProgress(p.TransferredBytes);
                    progress.Status = p.Status;
                });

                StatusMessage = $"Uploading directory {SelectedLocalItem.Name}...";
                await _directoryTransfer.UploadDirectoryAsync(SelectedLocalItem.FullPath, remotePath, progressReporter, _transferCts.Token);
            }
            else
            {
                var progress = new TransferProgress
                {
                    FileName = SelectedLocalItem.Name,
                    SourcePath = SelectedLocalItem.FullPath,
                    DestinationPath = remotePath,
                    TotalBytes = SelectedLocalItem.Size,
                    Direction = TransferDirection.Upload,
                    Status = TransferStatus.InProgress
                };
                Transfers.Add(progress);

                var progressReporter = new Progress<TransferProgress>(p =>
                {
                    progress.UpdateProgress(p.TransferredBytes);
                    progress.Status = p.Status;
                });

                StatusMessage = $"Uploading {SelectedLocalItem.Name}...";
                await _ftpClient.UploadFileAsync(SelectedLocalItem.FullPath, remotePath, progressReporter, _transferCts.Token);
            }

            await RefreshRemoteDirectoryAsync();
            StatusMessage = "Upload complete";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Upload error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedRemoteItem == null || !IsConnected) return;

        _transferCts = new CancellationTokenSource();

        try
        {
            var localPath = Path.Combine(LocalDirectory, SelectedRemoteItem.Name);

            if (SelectedRemoteItem.IsDirectory)
            {
                var progress = new TransferProgress
                {
                    FileName = SelectedRemoteItem.Name,
                    SourcePath = SelectedRemoteItem.FullPath,
                    DestinationPath = localPath,
                    Direction = TransferDirection.Download,
                    Status = TransferStatus.InProgress
                };
                Transfers.Add(progress);

                var progressReporter = new Progress<TransferProgress>(p =>
                {
                    progress.UpdateProgress(p.TransferredBytes);
                    progress.Status = p.Status;
                });

                StatusMessage = $"Downloading directory {SelectedRemoteItem.Name}...";
                await _directoryTransfer.DownloadDirectoryAsync(SelectedRemoteItem.FullPath, localPath, progressReporter, _transferCts.Token);
            }
            else
            {
                // Check for resume
                long resumePosition = 0;
                if (File.Exists(localPath))
                {
                    var existingFile = new FileInfo(localPath);
                    if (existingFile.Length < SelectedRemoteItem.Size)
                    {
                        resumePosition = existingFile.Length;
                        StatusMessage = $"Resuming download of {SelectedRemoteItem.Name} from {resumePosition} bytes...";
                    }
                    else
                    {
                        StatusMessage = $"Downloading {SelectedRemoteItem.Name}...";
                    }
                }
                else
                {
                    StatusMessage = $"Downloading {SelectedRemoteItem.Name}...";
                }

                var progress = new TransferProgress
                {
                    FileName = SelectedRemoteItem.Name,
                    SourcePath = SelectedRemoteItem.FullPath,
                    DestinationPath = localPath,
                    TotalBytes = SelectedRemoteItem.Size,
                    Direction = TransferDirection.Download,
                    Status = TransferStatus.InProgress
                };
                Transfers.Add(progress);

                var progressReporter = new Progress<TransferProgress>(p =>
                {
                    progress.UpdateProgress(p.TransferredBytes);
                    progress.Status = p.Status;
                });

                await _ftpClient.DownloadFileAsync(SelectedRemoteItem.FullPath, localPath, resumePosition, progressReporter, _transferCts.Token);
            }

            RefreshLocalDirectory();
            StatusMessage = "Download complete";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
        StatusMessage = "Transfer cancelled";
    }

    [RelayCommand]
    private async Task DeleteRemoteAsync()
    {
        if (SelectedRemoteItem == null || !IsConnected) return;

        try
        {
            bool success;
            if (SelectedRemoteItem.IsDirectory)
            {
                success = await _ftpClient.DeleteDirectoryAsync(SelectedRemoteItem.FullPath);
            }
            else
            {
                success = await _ftpClient.DeleteFileAsync(SelectedRemoteItem.FullPath);
            }

            if (success)
            {
                await RefreshRemoteDirectoryAsync();
                StatusMessage = "Delete successful";
            }
            else
            {
                StatusMessage = "Delete failed";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteLocal()
    {
        if (SelectedLocalItem == null) return;

        try
        {
            if (SelectedLocalItem.IsDirectory)
            {
                Directory.Delete(SelectedLocalItem.FullPath, true);
            }
            else
            {
                File.Delete(SelectedLocalItem.FullPath);
            }

            RefreshLocalDirectory();
            StatusMessage = "Delete successful";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete error: {ex.Message}";
        }
    }

    public IStorageProviderAccessor? StorageProviderAccessor { get; set; }

    private IStorageProvider? GetStorageProvider()
    {
        return StorageProviderAccessor?.GetStorageProvider();
    }
}
