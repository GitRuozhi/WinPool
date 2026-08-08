using System.Collections.ObjectModel;

namespace WinPool.Execution;

/// <summary>
/// Resolves the exact files authorized for one file-based test run.
/// </summary>
/// <remarks>
/// This type performs path validation only and never creates, opens, writes, or
/// deletes a file. Callers must resolve a path again immediately before use
/// because filesystem paths can change after validation.
/// </remarks>
public sealed class AuthorizedTestWorkspace
{
    private static readonly char[] DirectorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "CONIN$",
            "CONOUT$",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _registeredPathSet;

    public AuthorizedTestWorkspace(
        string rootDirectory,
        IEnumerable<string> registeredRelativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(registeredRelativePaths);

        RootDirectory = NormalizeRootDirectory(rootDirectory);
        ValidateRoot();

        _registeredPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in registeredRelativePaths)
        {
            var normalizedRelativePath = NormalizeRelativePath(relativePath);
            if (_registeredPathSet.Add(normalizedRelativePath))
            {
                ValidateTargetPath(
                    Path.GetFullPath(
                        Path.Combine(RootDirectory, normalizedRelativePath)));
            }
        }

        RegisteredRelativePaths = new ReadOnlyCollection<string>(
            _registeredPathSet.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Gets the normalized absolute test root selected for this run.
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Gets the normalized, exact relative file paths registered for this run.
    /// </summary>
    public IReadOnlyList<string> RegisteredRelativePaths { get; }

    /// <summary>
    /// Resolves a registered relative file path and revalidates the existing
    /// filesystem chain for reparse points.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// The path is rooted, traverses outside the root, is not registered, names
    /// the root itself, or its existing path chain contains a reparse point.
    /// </exception>
    public string ResolveRegisteredPath(string relativePath)
    {
        ValidateRoot();

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (!_registeredPathSet.Contains(normalizedRelativePath))
        {
            throw new UnauthorizedAccessException(
                $"The test file is not registered: '{relativePath}'.");
        }

        var absolutePath = Path.GetFullPath(
            Path.Combine(RootDirectory, normalizedRelativePath));
        ValidateTargetPath(absolutePath);
        return absolutePath;
    }

    private static string NormalizeRootDirectory(string rootDirectory)
    {
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException(
                "The authorized test root must be a fully qualified path.",
                nameof(rootDirectory));
        }

        var normalizedRoot = Path.GetFullPath(rootDirectory);
        if (normalizedRoot.StartsWith(@"\\?\", StringComparison.Ordinal)
            || normalizedRoot.StartsWith(@"\\.\", StringComparison.Ordinal)
            || normalizedRoot.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Windows device and extended path namespaces cannot be test roots.");
        }

        return Path.TrimEndingDirectorySeparator(normalizedRoot);
    }

    private string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException(
                $"A registered test file path must be relative: '{relativePath}'.");
        }

        var segments = relativePath.Split(
            DirectorySeparators,
            StringSplitOptions.None);
        if (segments.Any(IsUnsafePathSegment))
        {
            throw new UnauthorizedAccessException(
                $"The registered test file path is invalid or traverses directories: '{relativePath}'.");
        }

        var absolutePath = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        if (!IsDescendantOfRoot(absolutePath))
        {
            throw new UnauthorizedAccessException(
                $"The registered test file path escapes the authorized root: '{relativePath}'.");
        }

        var normalizedRelativePath = Path.GetRelativePath(RootDirectory, absolutePath);
        if (normalizedRelativePath is "." or "")
        {
            throw new UnauthorizedAccessException(
                "The authorized test root cannot be registered as a test file.");
        }

        return normalizedRelativePath;
    }

    private static bool IsUnsafePathSegment(string segment)
    {
        if (segment.Length == 0
            || segment is "." or ".."
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || segment.EndsWith(' ')
            || segment.EndsWith('.'))
        {
            return true;
        }

        var firstDot = segment.IndexOf('.');
        var deviceBaseName = firstDot < 0 ? segment : segment[..firstDot];
        return ReservedWindowsNames.Contains(deviceBaseName);
    }

    private void ValidateRoot()
    {
        if (!Directory.Exists(RootDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The authorized test root does not exist: '{RootDirectory}'.");
        }

        for (DirectoryInfo? directory = new(RootDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            RejectReparsePoint(directory.FullName, attributes);
        }
    }

    private void ValidateTargetPath(string absolutePath)
    {
        if (!IsDescendantOfRoot(absolutePath))
        {
            throw new UnauthorizedAccessException(
                $"The test file path escapes the authorized root: '{absolutePath}'.");
        }

        var relativePath = Path.GetRelativePath(RootDirectory, absolutePath);
        var segments = relativePath.Split(
            DirectorySeparators,
            StringSplitOptions.RemoveEmptyEntries);
        var currentPath = RootDirectory;

        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            RejectReparsePoint(currentPath, attributes);

            var isLastSegment = index == segments.Length - 1;
            if (!isLastSegment && !attributes.HasFlag(FileAttributes.Directory))
            {
                throw new UnauthorizedAccessException(
                    $"A test file path component is not a directory: '{currentPath}'.");
            }

            if (isLastSegment && attributes.HasFlag(FileAttributes.Directory))
            {
                throw new UnauthorizedAccessException(
                    $"A registered test file resolves to a directory: '{currentPath}'.");
            }
        }
    }

    private bool IsDescendantOfRoot(string absolutePath)
    {
        var rootPrefix = Path.EndsInDirectorySeparator(RootDirectory)
            ? RootDirectory
            : RootDirectory + Path.DirectorySeparatorChar;

        return absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoint(
        string path,
        FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"Reparse points are not allowed in an authorized test path: '{path}'.");
        }
    }
}
