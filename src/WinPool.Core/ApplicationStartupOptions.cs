namespace WinPool.Core;

public enum ApplicationStartupTarget
{
    None,
    Manage,
    Edit,
    Test,
    Monitor,
    Development,
    Settings,
    Welcome
}

public sealed record ApplicationStartupOptions(
    bool EnterRealModeAfterElevation,
    ApplicationStartupTarget Target = ApplicationStartupTarget.None)
{
    public const string ElevatedRealArgument = "--winpool-elevated-real";
    public const string StorageLocationHandoffArgument = "--winpool-storage-location-handoff";
    public const string WaitForProcessArgument = "--winpool-wait-for-process";
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
}
