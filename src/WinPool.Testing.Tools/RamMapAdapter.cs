using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed class RamMapAdapter : IExternalSystemSupportToolAdapter
{
    private static readonly IReadOnlyList<string> EmptySystemAndStandbyArguments =
        Array.AsReadOnly(["-Es", "-Et"]);

    private readonly string _executablePath;
    private readonly string _workingDirectory;

    public RamMapAdapter(string executablePath)
    {
        _executablePath = ToolAdapterSupport.ValidateExecutable(
            executablePath,
            "rammap.exe",
            "rammap64.exe",
            "rammap64a.exe");
        _workingDirectory = Path.GetDirectoryName(_executablePath)
            ?? throw new ArgumentException(
                "RAMMap has no executable directory.",
                nameof(executablePath));
    }

    public ToolId ToolId => ToolIds.RamMap;

    public ToolCapabilities Capabilities => ToolCapabilities.SystemCacheCleanup;

    public ApplicationResult<ToolInvocation> BuildInvocation(
        AuthorizedSystemSupportAction action,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                "rammap.authorization.expired",
                "The RAMMap action authorization has expired.");
        }

        if (action.Action is not ClearSystemFileCacheAction clearAction
            || clearAction.Kind is not SystemSupportActionKind.ClearSystemFileCache)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                "rammap.action.unsupported",
                "RAMMap only accepts an authorized system-cache clearing action.");
        }

        if (clearAction.Mode
            is not RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                "rammap.mode.not_whitelisted",
                "The requested RAMMap cache clearing mode is not whitelisted.");
        }

        return ApplicationResult<ToolInvocation>.Succeeded(
            new ToolInvocation(
                ToolId,
                _executablePath,
                EmptySystemAndStandbyArguments,
                _workingDirectory,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ToolOutputEncoding.Utf8,
                TimeSpan.FromMinutes(2)),
            correlationId);
    }
}
