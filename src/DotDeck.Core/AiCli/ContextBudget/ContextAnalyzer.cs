using System.Globalization;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>
/// Turns what <see cref="ContextCollector"/> found into the tab's report: MCP rows take their stored measurement (same
/// configuration only) or a status, items are ordered largest first, and suggestions follow the thresholds. Never
/// starts a server and never changes a file.
/// </summary>
public static class ContextAnalyzer
{
    public const int MaxDescriptionSuggestions = 3;

    /// <param name="failures">Failed measurements of this run by server key and configuration hash (not stored).</param>
    public static ContextReport Build(
        CliKind cli,
        string? workingDirectory,
        ContextCollection collection,
        McpMeasurementStore? measurements,
        IReadOnlyDictionary<string, (string ConfigHash, McpProbeResult Result)>? failures,
        ContextBudgetSettings settings,
        DateTimeOffset now,
        SessionLogMeasurement? log = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();

        var items = new List<ContextItem>(collection.Items);
        foreach (var server in collection.McpServers.DistinctBy(s => s.Key))
        {
            items.Add(McpItem(server, settings.McpDirectMeasure ? measurements : null, failures, settings.McpDirectMeasure));
        }

        var ordered = items
            .OrderByDescending(i => i.Counts)
            .ThenByDescending(i => i.Tokens)
            .ThenBy(i => i.Category)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var report = new ContextReport(cli, workingDirectory, ordered, [], collection.Problems, now, log);
        return report with { Suggestions = Suggest(report, settings) };
    }

    public static ContextItem McpItem(
        McpServerSpec server,
        McpMeasurementStore? measurements,
        IReadOnlyDictionary<string, (string ConfigHash, McpProbeResult Result)>? failures,
        bool directMeasure = true)
    {
        ArgumentNullException.ThrowIfNull(server);
        var label = $"{server.Name} MCP";
        if (server.Disabled)
        {
            return new ContextItem(ContextCategory.Mcp, label, server.SourcePath, 0, ContextItemState.Disabled, server.Source + " · 꺼짐", server.Key);
        }

        if (server.UsesOAuth)
        {
            return new ContextItem(ContextCategory.Mcp, label, server.SourcePath, 0, ContextItemState.AuthRequired, server.Source + " · 인증 필요", server.Key);
        }

        if (!directMeasure)
        {
            return new ContextItem(ContextCategory.Mcp, label, server.SourcePath, 0, ContextItemState.NotMeasured,
                server.Source + " · 크기는 기타에 포함", server.Key);
        }

        var hash = server.ConfigHash;
        if (failures is not null && failures.TryGetValue(server.Key, out var failure) && failure.ConfigHash == hash)
        {
            var state = failure.Result.State == ContextItemState.AuthRequired ? ContextItemState.AuthRequired : ContextItemState.Failed;
            var text = state == ContextItemState.AuthRequired ? failure.Result.Reason ?? "인증 필요" : "측정 실패 (" + failure.Result.Reason + ")";
            return new ContextItem(ContextCategory.Mcp, label, server.SourcePath, 0, state, server.Source + " · " + text, server.Key);
        }

        if (measurements?.Find(server) is { } measured)
        {
            var when = measured.MeasuredAt.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
            return new ContextItem(ContextCategory.Mcp, label, server.SourcePath, measured.Tokens, ContextItemState.Measured,
                string.Create(CultureInfo.InvariantCulture, $"{server.Source} · 도구 {measured.ToolCount}개 · {when} 측정"), server.Key);
        }

        var status = server.Unsupported is { } unsupported ? unsupported
            : measurements?.IsStale(server) == true ? "설정 변경됨 · 다시 측정 필요"
            : "미측정";
        return new ContextItem(ContextCategory.Mcp, label, server.SourcePath, 0, ContextItemState.NotMeasured, server.Source + " · " + status, server.Key);
    }

    public static IReadOnlyList<ContextSuggestion> Suggest(ContextReport report, ContextBudgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(settings);
        var result = new List<ContextSuggestion>();
        var total = report.Total;
        var baseline = report.Baseline;

        if (baseline > settings.TotalWarningTokens)
        {
            result.Add(new ContextSuggestion(
                $"기본 소모 약 {TokenEstimator.Format(baseline)} 토큰 → 권장 {TokenEstimator.Format(settings.TotalWarningTokens)} 이하. 아래 큰 항목부터 줄이세요"));
        }

        if (report.Other is { } other && baseline > 0 && other * 100.0 / baseline >= settings.McpSharePercent && report.Items.Any(i => i.Category == ContextCategory.Mcp && i.State != ContextItemState.Disabled))
        {
            result.Add(new ContextSuggestion(
                $"시스템·MCP 등 기타가 {TokenEstimator.Format(other)} 토큰({other * 100.0 / baseline:0}%) → 이 프로젝트에서 안 쓰는 MCP 서버를 끄면 줄어듭니다"));
        }

        foreach (var mcp in report.Items.Where(i => i.Category == ContextCategory.Mcp && i.Counts && total > 0))
        {
            var share = mcp.Tokens * 100.0 / total;
            if (share >= settings.McpSharePercent)
            {
                result.Add(new ContextSuggestion(
                    $"{mcp.Name}가 {TokenEstimator.Format(mcp.Tokens)} 토큰({share:0}%) → 이 프로젝트에서 안 쓰면 끄세요", mcp.Path));
            }
        }

        foreach (var file in report.Items.Where(i => i.Category == ContextCategory.Instructions && i.Counts && !i.BuiltIn && i.Tokens > settings.InstructionFileTokens))
        {
            result.Add(new ContextSuggestion(
                $"{file.Name} {file.Tokens:N0} 토큰 → {settings.InstructionFileTokens:N0} 이하 권장, 세부 내용은 별도 문서로 분리", file.Path));
        }

        var longDescriptions = report.Items
            .Where(i => i.Category is ContextCategory.Skills or ContextCategory.Agents && i.Counts && !i.BuiltIn && i.Tokens > settings.DescriptionTokens)
            .OrderByDescending(i => i.Tokens)
            .ToList();
        if (longDescriptions.Count > 0)
        {
            result.Add(new ContextSuggestion(
                $"스킬·에이전트 설명 중 {settings.DescriptionTokens:N0} 토큰을 넘는 긴 항목 {longDescriptions.Count}개 → 한두 문장으로 줄이세요"));
            foreach (var item in longDescriptions.Take(MaxDescriptionSuggestions))
            {
                result.Add(new ContextSuggestion($"  · {item.Name} {item.Tokens:N0} 토큰", item.Path));
            }
        }

        var unmeasured = report.Items.Count(i => i.Category == ContextCategory.Mcp && i.State == ContextItemState.NotMeasured);
        if (unmeasured > 0 && settings.McpDirectMeasure)
        {
            result.Add(new ContextSuggestion($"MCP 서버 {unmeasured}개는 미측정이라 합계에 빠져 있습니다 → [전체 측정]으로 확인"));
        }

        return result;
    }
}
