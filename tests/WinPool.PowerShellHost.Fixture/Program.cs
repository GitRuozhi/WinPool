using System.Text.Json;
using WinPool.Infrastructure.Windows;

// This process exists solely to exercise parent death against the production
// Job/process/frame implementation. Neither template loads Storage or calls a provider.
var templates = new PowerShellHostTemplates("param($p); '{}'", """
    param($p)
    Start-Sleep -Milliseconds 3000
    '{"Completed":true}'
    """, "param($kind,$p); '{}'", false);
await using var host = new WindowsPowerShellHost(templates);
await using var lease = await host.ReserveWriteAsync(default);
var pending = lease.ExecuteAsync(new { }, default);
await lease.WaitUntilStartedAsync(default);
Console.WriteLine("HOSTCALL:" + JsonSerializer.Serialize(lease.HostCall));
await Console.Out.FlushAsync();
await pending;
