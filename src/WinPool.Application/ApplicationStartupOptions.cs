using WinPool.Domain;

namespace WinPool.Application;

public enum ApplicationStartupTarget
{
    None,
    Manage,
    Edit,
    DiskAndPartition,
    Test,
    Monitor,
    Development,
    Settings,
    Welcome,
    Hardware
}

public sealed record ApplicationStartupOptions(
    bool EnterRealModeAfterElevation,
    ApplicationStartupTarget Target = ApplicationStartupTarget.None)
{
    public const string ElevatedRealArgument = "--winpool-elevated-real";
    public const string StorageLocationHandoffArgument = "--winpool-storage-location-handoff";
    public const string WaitForProcessArgument = "--winpool-wait-for-process";
    public const string WaitForProcessStartedAtArgument = "--winpool-wait-for-process-started-at";
    public const string WaitForAgentProcessArgument = "--winpool-wait-for-agent-process";
    public const string WaitForAgentProcessStartedAtArgument = "--winpool-wait-for-agent-process-started-at";
    public const string ElevationReadyEventArgument = "--winpool-elevation-ready-event";
    public const string ElevationContinueEventArgument = "--winpool-elevation-continue-event";
    public const string PageArgument = "--page";

    public static bool RequestsProcessHandoff(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            argument.Equals(ElevatedRealArgument, StringComparison.OrdinalIgnoreCase)
            || argument.Equals(
                StorageLocationHandoffArgument,
                StringComparison.OrdinalIgnoreCase));

    public static ApplicationStartupOptions Parse(
        IEnumerable<string> arguments,
        PrivilegeState privilegeState)
    {
        var argumentList = arguments.ToArray();
        var requested = argumentList.Any(argument =>
            argument.Equals(ElevatedRealArgument, StringComparison.OrdinalIgnoreCase));
        return new ApplicationStartupOptions(
            requested && privilegeState == PrivilegeState.Administrator,
            ParseTarget(argumentList));
    }

    public static ApplicationStartupTarget ParseTarget(
        IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!arguments[index].Equals(PageArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Enum.TryParse<ApplicationStartupTarget>(
                arguments[index + 1],
                ignoreCase: true,
                out var target)
                && target != ApplicationStartupTarget.None
                ? target
                : ApplicationStartupTarget.None;
        }

        return ApplicationStartupTarget.None;
    }

    public static int? GetHandoffProcessId(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!arguments[index].Equals(WaitForProcessArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(arguments[index + 1], out var processId) && processId > 0
                ? processId
                : null;
        }

        return null;
    }

    public static bool TryGetElevationHandoff(
        IReadOnlyList<string> arguments,
        out ElevationProcessHandoff handoff)
    {
        handoff = default!;
        if (!arguments.Any(argument =>
                argument.Equals(ElevatedRealArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var appProcessId = GetHandoffProcessId(arguments);
        var appStartedAt = GetUnixMilliseconds(arguments, WaitForProcessStartedAtArgument);
        var agentProcessId = GetPositiveInt(arguments, WaitForAgentProcessArgument);
        var agentStartedAt = GetUnixMilliseconds(arguments, WaitForAgentProcessStartedAtArgument);
        var readyEventName = GetValue(arguments, ElevationReadyEventArgument);
        var continueEventName = GetValue(arguments, ElevationContinueEventArgument);
        if (appProcessId is null
            || appStartedAt is null
            || agentProcessId is null
            || agentStartedAt is null
            || string.IsNullOrWhiteSpace(readyEventName)
            || string.IsNullOrWhiteSpace(continueEventName))
        {
            return false;
        }

        handoff = new(
            new(appProcessId.Value, appStartedAt.Value),
            new(agentProcessId.Value, agentStartedAt.Value),
            readyEventName,
            continueEventName);
        return true;
    }

    private static int? GetPositiveInt(IReadOnlyList<string> arguments, string argumentName)
    {
        var value = GetValue(arguments, argumentName);
        return int.TryParse(value, out var processId) && processId > 0
            ? processId
            : null;
    }

    private static DateTimeOffset? GetUnixMilliseconds(
        IReadOnlyList<string> arguments,
        string argumentName)
    {
        var value = GetValue(arguments, argumentName);
        return long.TryParse(value, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
    }

    private static string? GetValue(IReadOnlyList<string> arguments, string argumentName)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(argumentName, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}

public sealed record ElevationProcessHandoff(
    ProcessHandoffWitness AppProcess,
    ProcessHandoffWitness AgentProcess,
    string ReadyEventName,
    string ContinueEventName);
