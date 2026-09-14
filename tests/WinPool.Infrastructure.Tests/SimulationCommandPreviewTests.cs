using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class SimulationCommandPreviewTests
{
    [Fact]
    public async Task GeneratedTextParsesAndUsesInstalledStorageParametersWithoutExecutingIt()
    {
        const string name = "O'Brien ’ ‘ ‚ ‛ 中文\n$(throw 'never execute') ; `n";
        var empty = StorageSnapshot.Empty("test");
        var after = SimulationLayouts.StandardTiered();
        var texts = Enum.GetValues<SimulationEditKind>().SelectMany(kind => SimulationCommandPreview.Build(
            new(kind, after.StoragePools.First(x => !x.IsPrimordial).StableId, Name: name,
                DriveLetter: "T", FileSystem: "NTFS", SizeBytes: 4294967296, MemberDiskIds: ["not-a-windows-id"], CreateMsr: true),
            kind == SimulationEditKind.CreateVirtualDisk ? empty : after, after)).ToArray();
        // This fixed harness parses text as data. It never invokes a command from the generated text.
        const string harness = """
            $ErrorActionPreference = 'Stop'
            $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
            foreach ($text in $inputData.Texts) {
                $tokens = $null; $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) { throw ($errors.Message -join '; ') }
                foreach ($command in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)) {
                    $name = $command.GetCommandName()
                    if ($name -notmatch '^(New|Set|Clear|Initialize|Remove|Add|Resize)-(StoragePool|StorageTier|PhysicalDisk|VirtualDisk|Disk|Partition|PartitionAccessPath|Volume)$' -and $name -notin @('Format-Volume','Get-Disk')) { throw 'Unexpected executable syntax in generated text' }
                    $metadata = Get-Command -Name $name -ErrorAction Stop
                    foreach ($parameter in $command.CommandElements) {
                        if ($parameter -is [System.Management.Automation.Language.CommandParameterAst] -and -not $metadata.Parameters.ContainsKey($parameter.ParameterName)) { throw ($name + ' has no parameter ' + $parameter.ParameterName) }
                    }
                }
            }
            $tokens = $null; $errors = $null
            $quoted = [System.Management.Automation.Language.Parser]::ParseInput($inputData.Quoted, [ref]$tokens, [ref]$errors)
            $literal = $quoted.Find({ param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)
            if ($errors.Count -gt 0 -or $literal.Value -cne $inputData.Expected) { throw 'String literal round trip failed' }
            [Console]::Out.Write('verified')
            """;
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::InputEncoding = [Text.Encoding]::UTF8; [Console]::OutputEncoding = [Text.Encoding]::UTF8;\n" + harness)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Texts = texts, Quoted = SimulationCommandPreview.Quote(name), Expected = name }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal("verified", (await output).Trim());
        Assert.All(texts, text => Assert.DoesNotContain("not-a-windows-id", text));
    }
}
