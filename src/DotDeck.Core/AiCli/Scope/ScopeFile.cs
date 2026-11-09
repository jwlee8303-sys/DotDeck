using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DotDeck.Core.AiCli.Guard;

namespace DotDeck.Core.AiCli.Scope;

/// <summary>One root entry of scope.json.</summary>
public sealed record ScopeFileRoot
{
    public string? ProjectId { get; init; }

    public string? Name { get; init; }

    public required string Path { get; init; }

}

/// <summary>
/// Contents of a session's scope.json (DESIGN "범위 검사"). DotDeck rewrites it atomically whenever the scope changes
/// and the guard reads it on every tool call, so narrowing the scope takes effect immediately.
/// </summary>
public sealed record ScopeDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string SessionId { get; init; }

    public long Generation { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<ScopeFileRoot> Roots { get; init; } = [];

    public IReadOnlyList<string> ReadOnlyRoots { get; init; } = [];

    public bool AllowOutsideReads { get; init; }

    public string? Home { get; init; }

    public string? ExclusionsFile { get; init; }

    public GuardPolicy ToPolicy() => new()
    {
        Roots = [.. Roots.Select(r => r.Path)],
        ReadOnlyRoots = ReadOnlyRoots,
        AllowOutsideReads = AllowOutsideReads,
        Home = Home,
    };
}

public sealed class ScopeFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads and writes scope.json with sharing-violation retries. Any problem reading is an exception (fail closed).</summary>
public static class ScopeFile
{
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ScopeDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>Parses and validates scope.json contents.</summary>
    public static ScopeDocument Parse(string json, string? expectedSessionId = null)
    {
        ScopeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ScopeDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ScopeFileException("scope.json을 해석할 수 없습니다", ex);
        }

        if (document is null || string.IsNullOrWhiteSpace(document.SessionId))
        {
            throw new ScopeFileException("scope.json에 세션 정보가 없습니다");
        }

        if (document.SchemaVersion != ScopeDocument.CurrentSchemaVersion)
        {
            throw new ScopeFileException($"지원하지 않는 scope.json 형식입니다 (schemaVersion {document.SchemaVersion})");
        }

        if (expectedSessionId is not null && !string.Equals(document.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            throw new ScopeFileException("scope.json의 세션이 일치하지 않습니다");
        }

        if (document.Roots is null || document.Roots.Any(r => r is null || string.IsNullOrWhiteSpace(r.Path) || !Path.IsPathFullyQualified(r.Path))
            || document.ReadOnlyRoots is null || document.ReadOnlyRoots.Any(r => string.IsNullOrWhiteSpace(r) || !Path.IsPathFullyQualified(r)))
        {
            throw new ScopeFileException("scope.json의 루트 목록이 올바르지 않습니다");
        }

        return document;
    }

    /// <summary>Reads scope.json, retrying while DotDeck replaces it.</summary>
    public static ScopeDocument Read(string path, string? expectedSessionId = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                return Parse(reader.ReadToEnd(), expectedSessionId);
            }
            catch (FileNotFoundException ex)
            {
                throw new ScopeFileException("scope.json이 없습니다", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new ScopeFileException("세션 폴더가 없습니다", ex);
            }
            catch (IOException ex) when (attempt < Attempts)
            {
                _ = ex;
                Thread.Sleep(RetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < Attempts)
            {
                // MoveFileEx replacement can briefly deny access to the old file.
                Thread.Sleep(RetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ScopeFileException("scope.json을 읽을 수 없습니다", ex);
            }
        }
    }

    /// <summary>Writes scope.json atomically (temp file in the same folder, then replace), retrying on sharing violations.</summary>
    public static void Write(string path, ScopeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("scope.json 경로가 올바르지 않습니다", nameof(path));
        Directory.CreateDirectory(directory);
        var bytes = new UTF8Encoding(false).GetBytes(Serialize(document));
        var temp = Path.Combine(directory, $".scope-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts * 2)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
