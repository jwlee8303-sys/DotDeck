using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DotDeck.Core.Security;

namespace DotDeck.Core.AiCli.Approvals;

/// <summary>
/// Session record of answered requests: one JSON line per answer in <c>approvals.jsonl</c> of the DotDeck session folder
/// (who, when, what). Requests of CLIs run by hand have no session folder and go to <paramref name="fallback"/> (the app
/// log). Commands are masked again before they are written (secret handling rules of the app log).
/// </summary>
public sealed class ApprovalRecordLog(Func<string, string?> sessionDirectory, Action<string>? fallback = null) : IApprovalLog
{
    public const string FileName = "approvals.jsonl";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly object _gate = new();

    public void Record(ApprovalRequest request, ApprovalResponder responder)
    {
        ArgumentNullException.ThrowIfNull(request);
        var line = new JsonObject
        {
            ["time"] = (request.ResolvedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            ["id"] = request.Id,
            ["cli"] = request.Cli.ToString(),
            ["source"] = request.Source.ToString(),
            ["responder"] = responder.ToString(),
            ["state"] = request.State.ToString(),
            ["result"] = request.ResultText,
            ["tool"] = SecretMasker.MaskText(request.ToolName),
            ["command"] = SecretMasker.MaskText(request.Command),
            ["cwd"] = request.WorkingDirectory,
            ["requestedAt"] = request.RequestedAt.ToString("O", CultureInfo.InvariantCulture),
        }.ToJsonString(JsonOptions);

        var directory = sessionDirectory(request.SessionKey);
        if (directory is not null && Directory.Exists(directory))
        {
            lock (_gate)
            {
                File.AppendAllText(Path.Combine(directory, FileName), line + "\n", new UTF8Encoding(false));
            }
        }
        else
        {
            fallback?.Invoke("AI 승인 요청 " + line);
        }
    }
}
