using System.Text.Json;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

[Collection("PowerShellHost")]
public sealed class WindowsStorageJobReaderTests
{
    [Fact]
    public async Task FixedQueryReadsRawCimStateInsteadOfAdaptedCompletedName()
    {
        await using var host = new WindowsPowerShellHost(Templates("state"));
        var completed = await new WindowsStorageJobReader(host).ReadAsync(default);
        Assert.Equal((ushort)7, Assert.Single(completed.Jobs).JobState);
        Assert.NotEqual(default, completed.ObservedAtUtc);

        // Active, unknown, and reserved states retain their numeric values;
        // the backend's existing terminal-state whitelist remains responsible.
        foreach (ushort state in new ushort[] { 0, 2, 4, 8, 9, 10, 11, 12, ushort.MaxValue })
        {
            var json = await host.QueryAsync(WindowsPowerShellQuery.StorageJobs, new { State = state }, default);
            var observation = JsonSerializer.Deserialize<WindowsStorageJobAbsenceEvidence>(json, WindowsPowerShellHost.JsonOptions)!;
            var job = Assert.Single(observation.Jobs);
            Assert.Equal(state, job.JobState);
            Assert.Equal("job-unique", job.UniqueId);
            Assert.Equal("job-object", job.ObjectId);
        }
    }

    [Theory]
    [InlineData("provider-error")]
    [InlineData("missing-raw")]
    [InlineData("null-raw")]
    [InlineData("empty-raw")]
    [InlineData("named-raw")]
    [InlineData("missing-identity")]
    public async Task FailedEnumerationOrUnavailableRawStateCannotBecomeEmptyJobs(string mode)
    {
        await using var host = new WindowsPowerShellHost(Templates(mode));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsStorageJobReader(host).ReadAsync(default));
    }

    [Fact]
    public async Task SuccessfulEmptyEnumerationRemainsAnExplicitEmptyObservation()
    {
        await using var host = new WindowsPowerShellHost(Templates("empty"));
        var observation = await new WindowsStorageJobReader(host).ReadAsync(default);
        Assert.Empty(observation.Jobs);
        Assert.NotEqual(default, observation.ObservedAtUtc);
    }

    [Theory]
    [InlineData("@{ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Jobs=$null}")]
    [InlineData("@{Jobs=@()}")]
    [InlineData("@{ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Jobs=@($null)}")]
    public async Task IncompleteObservationCannotBeAcceptedAsNoJobs(string result)
    {
        var templates = Templates("empty") with { Query = "param($kind,$p); " + result + " | ConvertTo-Json -Compress" };
        await using var host = new WindowsPowerShellHost(templates);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsStorageJobReader(host).ReadAsync(default));
    }

    private static PowerShellHostTemplates Templates(string mode) => new(
        "param($p); throw 'fixture-inventory-not-used'",
        "param($p); throw 'fixture-write-not-used'",
        "param($kind,$p)\n$fixtureMode='" + mode + "'\n" + """
        function Get-CimInstance {
            param($Namespace,$ClassName,$ErrorAction)
            if($ClassName -ne 'MSFT_StorageJob') { throw 'unexpected-fixture-class' }
            if($fixtureMode -eq 'provider-error') { throw 'fixture-provider-enumeration-failed' }
            if($fixtureMode -eq 'empty') { return }
            $raw=[uint16]7
            if($null -ne $p.State) { $raw=[uint16]$p.State }
            $properties=@{JobState=@{Value=$raw}}
            switch($fixtureMode) {
                'missing-raw' { $properties=@{} }
                'null-raw' { $properties.JobState.Value=$null }
                'empty-raw' { $properties.JobState.Value='' }
                'named-raw' { $properties.JobState.Value='Completed' }
            }
            $unique='job-unique'; if($fixtureMode -eq 'missing-identity') { $unique='' }
            [pscustomobject]@{UniqueId=$unique;ObjectId='job-object';JobState='Completed';CimInstanceProperties=$properties}
        }
        & {
        """ + "\n" + PowerShellStorageQueryScript.Source + "\n} $kind $p", false);
}
