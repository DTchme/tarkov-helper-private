using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace TarkovHelper.Services.Map;

/// <summary>
/// EFT 스크린샷 폴더의 PNG 파일을 안전하게 정리합니다.
/// 하위 폴더와 다른 형식의 파일은 건드리지 않으며, 삭제 대신 휴지통으로 이동합니다.
/// </summary>
public static class ScreenshotFolderCleanupService
{
    private static readonly TimeSpan RecentFileProtection = TimeSpan.FromSeconds(10);

    public static bool TryCreatePreview(
        string? folderPath,
        out ScreenshotCleanupPreview? preview,
        out string errorMessage,
        DateTime? utcNow = null)
    {
        preview = null;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            errorMessage = "스크린샷 폴더를 먼저 선택해주세요.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        }
        catch (Exception)
        {
            errorMessage = "스크린샷 폴더 경로가 올바르지 않습니다.";
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            errorMessage = "선택한 스크린샷 폴더가 존재하지 않습니다.";
            return false;
        }

        var directory = new DirectoryInfo(fullPath);
        if (!directory.Name.Equals("Screenshots", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "안전을 위해 이름이 'Screenshots'인 폴더에서만 정리할 수 있습니다.";
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || PathsEqual(fullPath, root))
        {
            errorMessage = "드라이브 최상위 폴더는 정리할 수 없습니다.";
            return false;
        }

        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            errorMessage = "연결 또는 바로가기 폴더는 안전을 위해 정리할 수 없습니다.";
            return false;
        }

        var protectedFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (protectedFolders.Any(path => !string.IsNullOrWhiteSpace(path) && PathsEqual(fullPath, path)))
        {
            errorMessage = "사용자 기본 폴더 전체는 정리할 수 없습니다.";
            return false;
        }

        try
        {
            var cutoffUtc = (utcNow ?? DateTime.UtcNow) - RecentFileProtection;
            var candidates = new List<string>();
            long totalBytes = 0;
            var skippedRecentCount = 0;

            foreach (var filePath in Directory.EnumerateFiles(fullPath, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                if (!Path.GetExtension(filePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    continue;

                var resolvedFilePath = Path.GetFullPath(filePath);
                if (!PathsEqual(Path.GetDirectoryName(resolvedFilePath), fullPath))
                    continue;

                var fileInfo = new FileInfo(resolvedFilePath);
                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                if (fileInfo.LastWriteTimeUtc > cutoffUtc)
                {
                    skippedRecentCount++;
                    continue;
                }

                candidates.Add(resolvedFilePath);
                totalBytes += fileInfo.Length;
            }

            preview = new ScreenshotCleanupPreview(fullPath, candidates, totalBytes, skippedRecentCount);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"스크린샷 파일을 확인하지 못했습니다: {ex.Message}";
            return false;
        }
    }

    public static Task<ScreenshotCleanupResult> MoveToRecycleBinAsync(
        ScreenshotCleanupPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return Task.Run(() =>
        {
            var deletedCount = 0;
            var failedFiles = new List<string>();
            var cleanupDirectory = new DirectoryInfo(preview.FolderPath);

            // 확인 창이 열린 사이 폴더가 연결 폴더로 바뀌는 경우에도 작업을 중단합니다.
            if (!cleanupDirectory.Exists ||
                !cleanupDirectory.Name.Equals("Screenshots", StringComparison.OrdinalIgnoreCase) ||
                cleanupDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("스크린샷 폴더가 변경되어 안전하게 정리할 수 없습니다.");
            }

            var recentCutoffUtc = DateTime.UtcNow - RecentFileProtection;

            foreach (var filePath in preview.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var resolvedFilePath = Path.GetFullPath(filePath);
                    if (!PathsEqual(Path.GetDirectoryName(resolvedFilePath), preview.FolderPath) ||
                        !Path.GetExtension(resolvedFilePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(resolvedFilePath) ||
                        File.GetAttributes(resolvedFilePath).HasFlag(FileAttributes.ReparsePoint) ||
                        File.GetLastWriteTimeUtc(resolvedFilePath) > recentCutoffUtc)
                    {
                        continue;
                    }

                    FileSystem.DeleteFile(
                        resolvedFilePath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.DoNothing);
                    deletedCount++;
                }
                catch (Exception)
                {
                    failedFiles.Add(Path.GetFileName(filePath));
                }
            }

            return new ScreenshotCleanupResult(deletedCount, failedFiles);
        }, cancellationToken);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ScreenshotCleanupPreview(
    string FolderPath,
    IReadOnlyList<string> Files,
    long TotalBytes,
    int SkippedRecentCount);

public sealed record ScreenshotCleanupResult(int MovedToRecycleBinCount, IReadOnlyList<string> FailedFiles);
