using System.Text;

namespace DotDeck.Core.AiCli.Mcp;

/// <summary>Bounded newline-delimited UTF-8 messages; no headers or diagnostics on stdout.</summary>
public static class JsonRpcFraming
{
    public const int MaxMessageLength = 256 * 1024;

    public static async Task<string?> ReadAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= MaxMessageLength) throw new InvalidDataException("MCP 메시지 크기 제한을 초과했습니다.");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    public static async Task WriteAsync(TextWriter writer, string message, CancellationToken cancellationToken = default)
    {
        await writer.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
