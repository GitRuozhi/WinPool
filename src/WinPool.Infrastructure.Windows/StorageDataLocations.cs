using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

public sealed record StorageLocationSwitchResult(bool Success, string? ErrorMessage = null);

public static class StorageDataLocations
{
    private const string PointerFileName = "storage-location.json";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions PointerJsonOptions =
        CreatePointerJsonOptions();
    private static StorageLocationMode? _cachedMode;

    public static string StandardRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPool");

    public static string PortableRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Data");

    // The Standard root is replaced during migration, so its authority lives
    // beside it. Older installations may still have the in-root pointer.
    public static string PointerPath => StandardRoot + "." + PointerFileName;

    public static StorageLocationMode Mode
    {
        get
        {
            lock (Sync)
            {
                _cachedMode ??= ResolveMode(PointerPath, StandardRoot, PortableRoot);
                return _cachedMode.Value;
            }
        }
    }

    public static string CurrentRoot => Mode == StorageLocationMode.Portable ? PortableRoot : StandardRoot;

    public static string ResolveCurrentRoot(string productRoot)
        => ResolveCurrentRoot(productRoot, StandardRoot, PointerPath);

    internal static string ResolveCurrentRoot(
        string productRoot,
        string standardRoot,
        string pointerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(standardRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointerPath);
        var portableRoot = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot)),
            "Data");
        return ResolveMode(pointerPath, standardRoot, portableRoot) == StorageLocationMode.Portable
            ? portableRoot
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(standardRoot));
    }

    public static async Task<StorageLocationSwitchResult> SetModeAsync(
        StorageLocationMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == Mode)
        {
            return new StorageLocationSwitchResult(true);
        }

        var source = CurrentRoot;
        var target = mode == StorageLocationMode.Portable ? PortableRoot : StandardRoot;
        if (mode == StorageLocationMode.Portable && !IsWritable(AppContext.BaseDirectory))
        {
            return new StorageLocationSwitchResult(false, "portable-root-not-writable");
        }

        try
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(source, target, cancellationToken);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PointerPath)!);
            var temporaryPath = PointerPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StorageLocationPointer(mode),
                    options: (JsonSerializerOptions?)null,
                    cancellationToken);
            }
            File.Move(temporaryPath, PointerPath, true);
            lock (Sync)
            {
                _cachedMode = mode;
            }
            return new StorageLocationSwitchResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StorageLocationSwitchResult(false, ex.Message);
        }
    }

    private static StorageLocationMode ResolveMode(
        string pointerPath,
        string standardRoot,
        string portableRoot)
    {
        var legacyPath = Path.Combine(standardRoot, PointerFileName);
        if (File.Exists(pointerPath))
        {
            return ReadMode(pointerPath);
        }
        if (File.Exists(legacyPath))
        {
            return ReadMode(legacyPath);
        }

        return File.Exists(Path.Combine(portableRoot, "settings.json"))
            ? StorageLocationMode.Portable
            : StorageLocationMode.Standard;
    }

    private static StorageLocationMode ReadMode(string path)
    {
        var pointer = JsonSerializer.Deserialize<StorageLocationPointer>(
            File.ReadAllText(path),
            PointerJsonOptions);
        if (pointer is null || !Enum.IsDefined(pointer.Mode))
        {
            throw new JsonException("The storage location pointer is invalid.");
        }

        return pointer.Mode;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".winpool-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(file), PointerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private sealed record StorageLocationPointer(StorageLocationMode Mode);

    private static JsonSerializerOptions CreatePointerJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
