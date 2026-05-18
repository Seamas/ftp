using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FtpClient.Enums;

namespace FtpClient.Models;


public partial class TransferProgress : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _transferredBytes;

    [ObservableProperty]
    private TransferStatus _status = TransferStatus.Pending;

    [ObservableProperty]
    private TransferDirection _direction;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public double ProgressPercentage => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;

    public string DisplayProgress => $"{FormatFileSize(TransferredBytes)} / {FormatFileSize(TotalBytes)} ({ProgressPercentage:F1}%)";

    public void UpdateProgress(long bytesTransferred)
    {
        TransferredBytes = bytesTransferred;
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(DisplayProgress));
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
