using System;
using FtpClient.Enums;

namespace FtpClient.Models;

public class FtpItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public FtpItemType Type { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedTime { get; set; }
    public string Permissions { get; set; } = string.Empty;

    public bool IsDirectory => Type == FtpItemType.Directory;
    public string DisplaySize => IsDirectory ? "<DIR>" : FormatFileSize(Size);

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
