namespace WinPool.Application;

/// <summary>The exact host reserved before a durable CallIssued record.</summary>
public sealed record PowerShellHostCall(Guid Incarnation, int ProcessId, DateTimeOffset StartedAtUtc, Guid RequestId);
