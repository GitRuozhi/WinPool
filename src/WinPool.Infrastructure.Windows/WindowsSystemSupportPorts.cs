using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using WinPool.Application;
using WinPool.Domain;
using WinPool.ToolManagement;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsTemporaryFileCleanupPort : ITemporaryFileCleanupPort
{
    private readonly IReadOnlyDictionary<TemporaryFileScope, string> _roots;
    private readonly IWindowsResourceProtectionDetector _protectionDetector;

    public WindowsTemporaryFileCleanupPort(
        TemporaryCleanupRoots roots,
        IWindowsResourceProtectionDetector? protectionDetector = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _protectionDetector = protectionDetector ?? new WindowsResourceProtectionDetector();
        _roots = new Dictionary<TemporaryFileScope, string>
        {
            [TemporaryFileScope.WinPoolTemporaryFiles] =
                NormalizeDirectory(roots.WinPoolTemporaryDirectory),
            [TemporaryFileScope.CurrentUserTemporaryFiles] =
                NormalizeDirectory(roots.CurrentUserTemporaryDirectory),
            [TemporaryFileScope.WindowsOrdinaryTemporaryFiles] =
                NormalizeDirectory(roots.WindowsTemporaryDirectory)
        };
    }

    public Task<IReadOnlyList<TemporaryCleanupCandidate>> ScanAsync(
        IReadOnlyList<TemporaryFileScope> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var candidates = new List<TemporaryCleanupCandidate>();
        foreach (var scope in scopes.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_roots.TryGetValue(scope, out var root) || !Directory.Exists(root))
            {
                continue;
            }

            ScanRoot(root, scope, candidates, cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<TemporaryCleanupCandidate>>(
            candidates
                .OrderBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public Task<TemporaryCleanupPortResult> CleanAsync(
        IReadOnlyList<TemporaryCleanupCandidate> approvedCandidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedCandidates);
        var results = new List<TemporaryCleanupItemResult>(approvedCandidates.Count);
        foreach (var candidate in approvedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(CleanOne(candidate));
        }

        return Task.FromResult(new TemporaryCleanupPortResult(results));
    }

    private void ScanRoot(
        string root,
        TemporaryFileScope scope,
        ICollection<TemporaryCleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (HasReparsePointInPath(root, root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!isReparsePoint)
                    {
                        pending.Push(path);
                    }

                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    var fullPath = info.FullName;
                    candidates.Add(new TemporaryCleanupCandidate(
                        CreateCandidateId(scope, fullPath, info.Length, info.LastWriteTimeUtc),
                        fullPath,
                        scope,
                        info.Length,
                        isReparsePoint,
                        _protectionDetector.IsProtected(fullPath)));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // A changing temp tree is expected. The review/execute rescan detects drift.
                }
            }
        }
    }

    private TemporaryCleanupItemResult CleanOne(TemporaryCleanupCandidate candidate)
    {
        if (!_roots.TryGetValue(candidate.Scope, out var root))
        {
            return Result(candidate, TemporaryCleanupItemStatus.Skipped, "temporary-cleanup.scope.invalid");
        }

        string path;
        try
        {
            path = Path.GetFullPath(candidate.FullPath);
            if (!IsDescendant(path, root))
            {
                return Result(
                    candidate,
                    TemporaryCleanupItemStatus.Skipped,
                    "temporary-cleanup.path.outside-scope");
            }

            if (HasReparsePointInPath(root, path))
            {
                return Result(
                    candidate,
                    TemporaryCleanupItemStatus.Skipped,
                    "temporary-cleanup.reparse-point");
            }

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Result(
                    candidate,
                    TemporaryCleanupItemStatus.Skipped,
                    "temporary-cleanup.identity.changed");
            }

            if (_protectionDetector.IsProtected(path))
            {
                return Result(
                    candidate,
                    TemporaryCleanupItemStatus.Skipped,
                    "temporary-cleanup.windows-resource-protected");
            }

            var info = new FileInfo(path);
            var currentId = CreateCandidateId(
                candidate.Scope,
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc);
            if (currentId != candidate.Id || info.Length != candidate.Length)
            {
                return Result(
                    candidate,
                    TemporaryCleanupItemStatus.Skipped,
                    "temporary-cleanup.identity.changed");
            }

            File.Delete(path);
            return Result(candidate, TemporaryCleanupItemStatus.Removed, "temporary-cleanup.removed");
        }
        catch (FileNotFoundException)
        {
            return Result(candidate, TemporaryCleanupItemStatus.Skipped, "temporary-cleanup.missing");
        }
        catch (DirectoryNotFoundException)
        {
            return Result(candidate, TemporaryCleanupItemStatus.Skipped, "temporary-cleanup.missing");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result(candidate, TemporaryCleanupItemStatus.Failed, "temporary-cleanup.failed");
        }
    }

    private static IEnumerable<string> EnumerateEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static TemporaryCleanupCandidateId CreateCandidateId(
        TemporaryFileScope scope,
        string path,
        long length,
        DateTime lastWriteTimeUtc)
    {
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{scope}\n{Path.GetFullPath(path).ToUpperInvariant()}\n{length}\n{lastWriteTimeUtc.Ticks}");
        return new TemporaryCleanupCandidateId(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                .ToLowerInvariant());
    }

    private static TemporaryCleanupItemResult Result(
        TemporaryCleanupCandidate candidate,
        TemporaryCleanupItemStatus status,
        string code) =>
        new(candidate.Id, status, code);

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsDescendant(string path, string directory) =>
        path.Length > directory.Length &&
        path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
        (path[directory.Length] == Path.DirectorySeparatorChar ||
         path[directory.Length] == Path.AltDirectorySeparatorChar);

    private static bool HasReparsePointInPath(string root, string path)
    {
        try
        {
            return HasReparsePointInPathCore(root, path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            // A disappearing or inaccessible path is never safe to clean.
            return true;
        }
    }

    private static bool HasReparsePointInPathCore(string root, string path)
    {
        var normalizedRoot = NormalizeDirectory(root);
        var normalizedPath = Path.GetFullPath(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(normalizedRoot, normalizedPath)
            && !IsDescendant(normalizedPath, normalizedRoot))
        {
            return true;
        }

        if (File.GetAttributes(normalizedRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }
}

public interface IWindowsResourceProtectionDetector
{
    bool IsProtected(string fullPath);
}

public sealed class WindowsResourceProtectionDetector : IWindowsResourceProtectionDetector
{
    public bool IsProtected(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        try
        {
            return SfcIsFileProtected(nint.Zero, Path.GetFullPath(fullPath));
        }
        catch (DllNotFoundException)
        {
            // Path policy still excludes component, update and system roots.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("sfc.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SfcIsFileProtected(nint rpcHandle, string protectedFileName);
}

public sealed class WindowsTestProcessSchedulingPort : ITestProcessSchedulingPort
{
    private readonly Func<int, bool> _isRegisteredTestProcess;

    public WindowsTestProcessSchedulingPort(Func<int, bool> isRegisteredTestProcess)
    {
        _isRegisteredTestProcess =
            isRegisteredTestProcess ?? throw new ArgumentNullException(nameof(isRegisteredTestProcess));
    }

    public Task<TestProcessSchedulingSnapshot?> CaptureAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (processId <= 0 || !_isRegisteredTestProcess(processId))
        {
            return Task.FromResult<TestProcessSchedulingSnapshot?>(null);
        }

        using var process = Process.GetProcessById(processId);
        var snapshot = new TestProcessSchedulingSnapshot(
            processId,
            true,
            FromWindowsPriority(process.PriorityClass),
            DecodeAffinity(process.ProcessorAffinity));
        return Task.FromResult<TestProcessSchedulingSnapshot?>(snapshot);
    }

    public Task ApplyAsync(
        int processId,
        TestProcessPriority priority,
        IReadOnlyList<int> logicalProcessorIndices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureRegistered(processId);
        var affinity = EncodeAffinity(logicalProcessorIndices);
        using var process = Process.GetProcessById(processId);
        process.PriorityClass = ToWindowsPriority(priority);
        process.ProcessorAffinity = affinity;
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        TestProcessSchedulingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureRegistered(snapshot.ProcessId);
        using var process = Process.GetProcessById(snapshot.ProcessId);
        process.PriorityClass = ToWindowsPriority(snapshot.Priority);
        process.ProcessorAffinity = EncodeAffinity(snapshot.LogicalProcessorIndices);
        return Task.CompletedTask;
    }

    private void EnsureRegistered(int processId)
    {
        if (processId <= 0 || !_isRegisteredTestProcess(processId))
        {
            throw new InvalidOperationException("Only a registered WinPool test process may be adjusted.");
        }
    }

    private static ProcessPriorityClass ToWindowsPriority(TestProcessPriority priority) =>
        priority switch
        {
            TestProcessPriority.Idle => ProcessPriorityClass.Idle,
            TestProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
            TestProcessPriority.Normal => ProcessPriorityClass.Normal,
            TestProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
            TestProcessPriority.High => ProcessPriorityClass.High,
            _ => throw new ArgumentOutOfRangeException(nameof(priority))
        };

    private static TestProcessPriority FromWindowsPriority(ProcessPriorityClass priority) =>
        priority switch
        {
            ProcessPriorityClass.Idle => TestProcessPriority.Idle,
            ProcessPriorityClass.BelowNormal => TestProcessPriority.BelowNormal,
            ProcessPriorityClass.Normal => TestProcessPriority.Normal,
            ProcessPriorityClass.AboveNormal => TestProcessPriority.AboveNormal,
            ProcessPriorityClass.High => TestProcessPriority.High,
            _ => throw new InvalidOperationException(
                $"Priority class {priority} is outside the WinPool test whitelist.")
        };

    private static nint EncodeAffinity(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var width = IntPtr.Size * 8;
        if (indices.Count == 0 ||
            indices.Distinct().Count() != indices.Count ||
            indices.Any(index => index < 0 || index >= width))
        {
            throw new ArgumentOutOfRangeException(
                nameof(indices),
                $"Affinity indices must be unique values from 0 through {width - 1}.");
        }

        ulong mask = 0;
        foreach (var index in indices)
        {
            mask |= 1UL << index;
        }

        return unchecked((nint)mask);
    }

    private static IReadOnlyList<int> DecodeAffinity(nint affinity)
    {
        var mask = unchecked((ulong)affinity);
        var indices = new List<int>();
        for (var index = 0; index < IntPtr.Size * 8; index++)
        {
            if ((mask & (1UL << index)) != 0)
            {
                indices.Add(index);
            }
        }

        return indices;
    }
}

public sealed record WindowsCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IWindowsCommandRunner
{
    Task<WindowsCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class ProcessWindowsCommandRunner : IWindowsCommandRunner
{
    public async Task<WindowsCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fixed Windows command did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new WindowsCommandResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }
}

public sealed record WindowsPowerPlanDescriptor(
    Guid PowerPlanId,
    string DisplayName,
    bool IsActive);

public sealed partial class WindowsPowerPlanCatalog
{
    private readonly IWindowsCommandRunner _runner;
    private readonly string _powerCfgPath;

    public WindowsPowerPlanCatalog(
        IWindowsCommandRunner runner,
        string? windowsDirectory = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        var root = windowsDirectory
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _powerCfgPath = Path.Combine(
            Path.GetFullPath(root),
            "System32",
            "powercfg.exe");
    }

    public async Task<IReadOnlyList<WindowsPowerPlanDescriptor>> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            _powerCfgPath,
            ["/list"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "powercfg /list failed while reading installed power plans.");
        }

        return Parse(result.StandardOutput);
    }

    internal static IReadOnlyList<WindowsPowerPlanDescriptor> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return PowerPlanLineRegex()
            .Matches(output)
            .Select(match => new WindowsPowerPlanDescriptor(
                Guid.Parse(match.Groups["id"].Value),
                match.Groups["name"].Value.Trim(),
                match.Groups["active"].Success))
            .GroupBy(item => item.PowerPlanId)
            .Select(group => group.First())
            .ToArray();
    }

    [GeneratedRegex(
        @"(?im)^\s*[^:\r\n]*:\s*(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\((?<name>[^\r\n]*?)\)\s*(?<active>\*)?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PowerPlanLineRegex();
}

public sealed partial class WindowsTemporaryPowerPlanPort : ITemporaryPowerPlanPort
{
    private readonly IWindowsCommandRunner _runner;
    private readonly string _powerCfgPath;

    public WindowsTemporaryPowerPlanPort(
        IWindowsCommandRunner runner,
        string? windowsDirectory = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        var root = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _powerCfgPath = Path.Combine(Path.GetFullPath(root), "System32", "powercfg.exe");
    }

    public async Task<PowerPlanSnapshot> CaptureActiveAsync(CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(_powerCfgPath, ["/getactivescheme"], cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(result, "capture active power plan");
        var match = PowerPlanGuidRegex().Match(result.StandardOutput);
        if (!match.Success ||
            !Guid.TryParse(match.Groups["id"].Value, out var powerPlanId) ||
            powerPlanId == Guid.Empty)
        {
            throw new InvalidDataException("powercfg did not return an active power-plan GUID.");
        }

        return new PowerPlanSnapshot(powerPlanId);
    }

    public Task ActivateAsync(Guid powerPlanId, CancellationToken cancellationToken) =>
        SetActiveAsync(powerPlanId, cancellationToken);

    public Task RestoreAsync(
        PowerPlanSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SetActiveAsync(snapshot.PowerPlanId, cancellationToken);
    }

    private async Task SetActiveAsync(Guid powerPlanId, CancellationToken cancellationToken)
    {
        if (powerPlanId == Guid.Empty)
        {
            throw new ArgumentException("A concrete power-plan GUID is required.", nameof(powerPlanId));
        }

        var result = await _runner
            .RunAsync(
                _powerCfgPath,
                ["/setactive", powerPlanId.ToString("D", CultureInfo.InvariantCulture)],
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(result, "activate power plan");
    }

    private static void EnsureSuccess(WindowsCommandResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not {operation}. ExitCode={result.ExitCode}; {result.StandardError}");
        }
    }

    [GeneratedRegex(
        @"(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PowerPlanGuidRegex();
}

public sealed record WindowsVolumeTargetBinding(
    string PlanHash,
    StorageObjectId VolumeId,
    string MountPath,
    string PlannedVolumeGuidPath);

public static class WindowsVolumeIdentityProbe
{
    public static VolumeTargetSnapshot? Resolve(
        StorageObjectId volumeId,
        string mountPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        var normalizedMount = Path.GetFullPath(mountPath);
        if (!Path.EndsInDirectorySeparator(normalizedMount))
        {
            normalizedMount += Path.DirectorySeparatorChar;
        }

        var buffer = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(
                normalizedMount,
                buffer,
                buffer.Capacity))
        {
            return null;
        }

        var stable = buffer.ToString().Trim().TrimEnd('\\').ToUpperInvariant();
        if (!stable.StartsWith(@"\\?\VOLUME{", StringComparison.Ordinal) ||
            !stable.EndsWith('}'))
        {
            return null;
        }

        return new(volumeId, stable, normalizedMount);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);
}

public interface IWindowsVolumeTargetBindingResolver
{
    Task<VolumeTargetSnapshot?> ResolvePlannedAsync(
        StorageObjectId volumeId,
        string planHash,
        CancellationToken cancellationToken);

    Task<VolumeTargetSnapshot?> ResolveCurrentAsync(
        StorageObjectId volumeId,
        CancellationToken cancellationToken);
}

public sealed class BoundWindowsVolumeTargetResolver : IWindowsVolumeTargetBindingResolver
{
    private readonly IReadOnlyDictionary<StorageObjectId, WindowsVolumeTargetBinding> _bindings;

    public BoundWindowsVolumeTargetResolver(IEnumerable<WindowsVolumeTargetBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings.ToDictionary(binding => binding.VolumeId);
    }

    public Task<VolumeTargetSnapshot?> ResolvePlannedAsync(
        StorageObjectId volumeId,
        string planHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bindings.TryGetValue(volumeId, out var binding) ||
            !StringComparer.Ordinal.Equals(binding.PlanHash, planHash))
        {
            return Task.FromResult<VolumeTargetSnapshot?>(null);
        }

        return Task.FromResult<VolumeTargetSnapshot?>(
            NewSnapshot(binding, NormalizeVolumeGuid(binding.PlannedVolumeGuidPath)));
    }

    public Task<VolumeTargetSnapshot?> ResolveCurrentAsync(
        StorageObjectId volumeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bindings.TryGetValue(volumeId, out var binding))
        {
            return Task.FromResult<VolumeTargetSnapshot?>(null);
        }

        var mountPath = NormalizeMountPath(binding.MountPath);
        var buffer = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(mountPath, buffer, buffer.Capacity))
        {
            return Task.FromResult<VolumeTargetSnapshot?>(null);
        }

        return Task.FromResult<VolumeTargetSnapshot?>(
            NewSnapshot(binding, NormalizeVolumeGuid(buffer.ToString())));
    }

    private static VolumeTargetSnapshot NewSnapshot(
        WindowsVolumeTargetBinding binding,
        string volumeGuidPath) =>
        new(binding.VolumeId, volumeGuidPath, NormalizeMountPath(binding.MountPath));

    private static string NormalizeMountPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string NormalizeVolumeGuid(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim();
        if (!value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !value.TrimEnd('\\').EndsWith('}'))
        {
            throw new ArgumentException("A Windows volume GUID path is required.", nameof(path));
        }

        return value.TrimEnd('\\').ToUpperInvariant();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);
}

public interface IWindowsVolumeFlushApi
{
    void Flush(string volumeGuidPath);
}

public sealed class WindowsVolumeFlushApi : IWindowsVolumeFlushApi
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public void Flush(string volumeGuidPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuidPath);
        using var handle = CreateFile(
            volumeGuidPath.TrimEnd('\\'),
            GenericWrite,
            ShareRead | ShareWrite,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Could not open the approved volume for Flush. Win32={Marshal.GetLastWin32Error()}.");
        }

        if (!FlushFileBuffers(handle))
        {
            throw new IOException(
                $"FlushFileBuffers failed for the approved volume. Win32={Marshal.GetLastWin32Error()}.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);
}

public sealed class WindowsVolumeMaintenancePort : IVolumeMaintenancePort
{
    private readonly IWindowsVolumeTargetBindingResolver _resolver;
    private readonly IWindowsVolumeFlushApi _flushApi;
    private readonly IWindowsCommandRunner _runner;
    private readonly string _defragPath;

    public WindowsVolumeMaintenancePort(
        IWindowsVolumeTargetBindingResolver resolver,
        IWindowsVolumeFlushApi flushApi,
        IWindowsCommandRunner runner,
        string? windowsDirectory = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _flushApi = flushApi ?? throw new ArgumentNullException(nameof(flushApi));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        var root = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _defragPath = Path.Combine(Path.GetFullPath(root), "System32", "defrag.exe");
    }

    public Task<VolumeTargetSnapshot?> ResolvePlannedTargetAsync(
        StorageObjectId volumeId,
        string planHash,
        CancellationToken cancellationToken) =>
        _resolver.ResolvePlannedAsync(volumeId, planHash, cancellationToken);

    public Task<VolumeTargetSnapshot?> ResolveCurrentTargetAsync(
        StorageObjectId volumeId,
        CancellationToken cancellationToken) =>
        _resolver.ResolveCurrentAsync(volumeId, cancellationToken);

    public Task<VolumeMaintenanceEvidence> FlushAsync(
        VolumeTargetSnapshot target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        _flushApi.Flush(target.StableIdentity);
        return Task.FromResult(
            new VolumeMaintenanceEvidence(
                "Win32.FlushFileBuffers",
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}"));
    }

    public async Task<VolumeMaintenanceEvidence> TrimOrOptimizeAsync(
        VolumeTargetSnapshot target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var result = await _runner
            .RunAsync(
                _defragPath,
                [target.DisplayIdentity, "/L", "/U", "/V"],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows Optimize/Trim failed. ExitCode={result.ExitCode}; {result.StandardError}");
        }

        return new VolumeMaintenanceEvidence(
            "Windows.Defrag.Trim",
            result.StandardOutput);
    }
}

public interface IRamMapExecutableIdentityProbe
{
    Task<RamMapToolIdentity?> ProbeAsync(
        string executablePath,
        CancellationToken cancellationToken);
}

public sealed class WindowsRamMapExecutableIdentityProbe : IRamMapExecutableIdentityProbe
{
    public async Task<RamMapToolIdentity?> ProbeAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var path = Path.GetFullPath(executablePath);
        if (!File.Exists(path) || !IsRamMapExecutableName(Path.GetFileName(path)))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        var version = First(versionInfo.ProductVersion, versionInfo.FileVersion) ?? string.Empty;
        var signatureTrusted = WindowsAuthenticodeVerifier.IsTrusted(path);
        var publisher = signatureTrusted
            ? TryReadSignerName(path) ?? string.Empty
            : string.Empty;
        var pathBindingHash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(path.Trim().ToUpperInvariant())))
            .ToLowerInvariant();
        return new RamMapToolIdentity(
            pathBindingHash,
            version,
            publisher,
            sha256,
            signatureTrusted,
            RequiresElevation: true);
    }

    private static bool IsRamMapExecutableName(string name) =>
        name.Equals("RAMMap.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RAMMap64.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RAMMap64a.exe", StringComparison.OrdinalIgnoreCase);

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? TryReadSignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

public sealed class DirectElevatedRamMapCacheClearPort : IRamMapCacheClearPort
{
    private static readonly IReadOnlyList<string> FixedArguments = ["-Es", "-Et"];

    private readonly string _executablePath;
    private readonly IRamMapExecutableIdentityProbe _identityProbe;
    private readonly IWindowsCommandRunner _runner;
    private readonly bool _isInsideElevatedBroker;

    public DirectElevatedRamMapCacheClearPort(
        string executablePath,
        IRamMapExecutableIdentityProbe identityProbe,
        IWindowsCommandRunner runner,
        bool isInsideElevatedBroker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _isInsideElevatedBroker = isInsideElevatedBroker;
    }

    public bool SupportsElevatedBroker => _isInsideElevatedBroker;

    public Task<RamMapToolIdentity?> DetectIdentityAsync(CancellationToken cancellationToken) =>
        _identityProbe.ProbeAsync(_executablePath, cancellationToken);

    public async Task<RamMapCacheClearEvidence> ClearAsync(
        RamMapCacheClearRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_isInsideElevatedBroker)
        {
            throw new InvalidOperationException(
                "RAMMap cache clearing is only available inside the one-shot elevated Broker.");
        }

        if (request.Mode != RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The RAMMap mode is not whitelisted.");
        }

        var result = await _runner
            .RunAsync(_executablePath, FixedArguments, cancellationToken)
            .ConfigureAwait(false);
        return new RamMapCacheClearEvidence(
            FixedArguments,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            null,
            null,
            UsedElevatedBroker: true);
    }
}

public sealed class WindowsToolExecutableTrustVerifier : IToolExecutableTrustVerifier
{
    public Task<bool> IsTrustedAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsAuthenticodeVerifier.IsTrusted(executablePath));
    }
}

public static class WindowsAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string filePath)
    {
        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData(fileInfoPointer);
            var action = GenericVerifyV2;
            return WinVerifyTrust(nint.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public WinTrustFileInfo(string filePath)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = nint.Zero;
            KnownSubject = nint.Zero;
        }

        public uint StructureSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public WinTrustData(nint fileInfo)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = nint.Zero;
            SipClientData = nint.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = nint.Zero;
            UrlReference = nint.Zero;
            ProviderFlags = 0x00000100;
            UiContext = 0;
        }

        public uint StructureSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);
}
