using System.IO.Compression;
using System.Text;

namespace WinPool.Infrastructure.Tests;

// Test fixtures alone can execute their local fake-command harnesses. Product
// requests use the persistent host's closed typed protocol.
internal static class PowerShellFixtureScriptCodec
{
    internal static string CompressFixedScript(string script)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, true)) gzip.Write(Encoding.UTF8.GetBytes(script));
        return Convert.ToBase64String(buffer.ToArray());
    }
    internal static string EncodeFixedScriptLoader()
    {
        const string bootstrap = "$i=[Console]::OpenStandardInput();$l=[IO.MemoryStream]::new();"
            + "while(($n=$i.ReadByte()) -ge 0 -and $n -ne 10){$l.WriteByte([byte]$n)};"
            + "$b=[Convert]::FromBase64String([Text.Encoding]::ASCII.GetString($l.ToArray()));$m=[IO.MemoryStream]::new($b);"
            + "$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);"
            + "$r=[IO.StreamReader]::new($g,[Text.UTF8Encoding]::new($false));$s=$r.ReadToEnd();& ([ScriptBlock]::Create($s))";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
    }
    internal static string EncodeFixedCompressedScript(string script)
    {
        var bootstrap = "$b=[Convert]::FromBase64String('" + CompressFixedScript(script)
            + "');$m=[IO.MemoryStream]::new($b);$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);"
            + "$r=[IO.StreamReader]::new($g,[Text.UTF8Encoding]::new($false));$s=$r.ReadToEnd();& ([ScriptBlock]::Create($s))";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
    }
}
