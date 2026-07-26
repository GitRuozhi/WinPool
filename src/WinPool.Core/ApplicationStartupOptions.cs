namespace WinPool.Core;

public sealed record ApplicationStartupOptions(bool EnterRealModeAfterElevation)
{
    public const string ElevatedRealArgument = "--winpool-elevated-real";
    public const string WaitForProcessArgument = "--winpool-wait-for-process";

    public static ApplicationStartupOptions Parse(
        IEnumerable<string> arguments,
        PrivilegeState privilegeState)
    {
        var requested = arguments.Any(argument =>
            argument.Equals(ElevatedRealArgument, StringComparison.OrdinalIgnoreCase));
        return new ApplicationStartupOptions(
            requested && privilegeState == PrivilegeState.Administrator);
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
