namespace WinPool.Ipc;

public static class AppExitSignal
{
    public static string CreateName(string userSidHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSidHash);
        if (userSidHash.Length != 64
            || userSidHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The SID hash must be a SHA-256 hexadecimal value.",
                nameof(userSidHash));
        }

        return $"Local\\WinPool.App.Exit.{userSidHash[..24]}";
    }
}
