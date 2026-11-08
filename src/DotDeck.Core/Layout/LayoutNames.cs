namespace DotDeck.Core.Layout;

/// <summary>Names of saved docking layouts (<c>layout\named\&lt;name&gt;.xml</c>).</summary>
public static class LayoutNames
{
    public const int MaxLength = 64;

    public const string Extension = ".xml";

    /// <summary>Error message for an unusable name, or null when it can be used as a file name.</summary>
    public static string? Validate(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "이름을 입력하세요.";
        }

        if (trimmed.Length > MaxLength)
        {
            return $"이름은 {MaxLength}자 이하로 입력하세요.";
        }

        return FileOperations.FileNameRules.Validate(trimmed);
    }

    public static string FileName(string name) => name.Trim() + Extension;

    /// <summary>Layout names from the files of the named-layout folder, sorted; other files are ignored.</summary>
    public static IReadOnlyList<string> FromFileNames(IEnumerable<string> fileNames) =>
        fileNames
            .Where(f => f.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(f => f[..^Extension.Length])
            .Where(n => Validate(n) is null)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
