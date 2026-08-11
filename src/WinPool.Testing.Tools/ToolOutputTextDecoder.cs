using System.Text;
using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed record ResolvedToolOutputEncoding(
    ToolOutputEncoding Family,
    int CodePage);

public interface IToolOutputCodePageResolver
{
    ResolvedToolOutputEncoding Resolve(ToolOutputEncoding family);
}

public sealed class SystemToolOutputCodePageResolver : IToolOutputCodePageResolver
{
    static SystemToolOutputCodePageResolver()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ResolvedToolOutputEncoding Resolve(ToolOutputEncoding family) =>
        new(
            family,
            family switch
            {
                ToolOutputEncoding.Utf8 => Encoding.UTF8.CodePage,
                ToolOutputEncoding.Utf16LittleEndian => Encoding.Unicode.CodePage,
                ToolOutputEncoding.SystemAnsi =>
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
                ToolOutputEncoding.Oem =>
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            });
}

public sealed class ToolOutputTextDecoder
{
    private readonly Decoder decoder;
    private readonly char[] characters = new char[4_096];

    public ToolOutputTextDecoder(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        decoder = Encoding.GetEncoding(
                codePage,
                new EncoderReplacementFallback("\uFFFD"),
                new DecoderReplacementFallback("\uFFFD"))
            .GetDecoder();
    }

    public string Decode(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        var result = new StringBuilder();
        do
        {
            decoder.Convert(
                bytes,
                characters,
                flush,
                out var bytesUsed,
                out var charactersUsed,
                out var completed);
            if (charactersUsed > 0)
            {
                result.Append(characters, 0, charactersUsed);
            }

            bytes = bytes[bytesUsed..];
            if (completed)
            {
                break;
            }
        }
        while (!bytes.IsEmpty || flush);

        return result.ToString();
    }

    public string Complete() => Decode(ReadOnlySpan<byte>.Empty, flush: true);
}
