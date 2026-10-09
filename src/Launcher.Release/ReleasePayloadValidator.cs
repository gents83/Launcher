using System.IO;

namespace Launcher.Release;

internal sealed record ReleasePayloadFile(string FullPath, string RelativePath);

internal static class ReleasePayloadValidator
{
    public static IReadOnlyList<ReleasePayloadFile> EnumerateFiles(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var canonicalRoot = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(canonicalRoot))
        {
            throw new DirectoryNotFoundException("The release payload directory does not exist.");
        }

        var pendingDirectories = new Stack<string>();
        var payloadFiles = new List<ReleasePayloadFile>();
        pendingDirectories.Push(canonicalRoot);
        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The release payload contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entryPath);
                    continue;
                }

                var fullPath = Path.GetFullPath(entryPath);
                var relativePath = Path.GetRelativePath(canonicalRoot, fullPath);
                EnsureContainedPath(canonicalRoot, fullPath);
                payloadFiles.Add(new ReleasePayloadFile(fullPath, relativePath));
            }
        }

        return payloadFiles
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static void ValidateFiles(IEnumerable<ReleasePayloadFile> payloadFiles)
    {
        foreach (var file in payloadFiles)
        {
            if (IsForbiddenPath(file.RelativePath))
            {
                throw new InvalidDataException($"The release payload contains a forbidden file: {file.RelativePath}");
            }
        }
    }

    public static bool IsForbiddenPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(relativePath);
        var normalizedFileName = fileName.ToLowerInvariant();
        var extension = Path.GetExtension(fileName);
        var segments = normalizedPath.Split('/');

        return segments.Any(static segment =>
                   segment.Equals("projects", StringComparison.OrdinalIgnoreCase)
                   || segment.StartsWith(".p4", StringComparison.OrdinalIgnoreCase)
                   || segment.Equals("p4config", StringComparison.OrdinalIgnoreCase)
                   || segment.Equals("p4tickets", StringComparison.OrdinalIgnoreCase)
                   || segment.Equals("tests", StringComparison.OrdinalIgnoreCase))
               || normalizedFileName is "projects.json" or "theme.json"
               || normalizedFileName.StartsWith(".env", StringComparison.Ordinal)
               || normalizedFileName.Contains("secret", StringComparison.Ordinal)
               || normalizedFileName.Contains("credential", StringComparison.Ordinal)
               || normalizedFileName.Contains("token", StringComparison.Ordinal)
               || normalizedFileName.Contains(".tests.", StringComparison.Ordinal)
               || extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".p7b", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".key", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".pem", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureContainedPath(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A release payload file escapes its source root.");
        }
    }
}
