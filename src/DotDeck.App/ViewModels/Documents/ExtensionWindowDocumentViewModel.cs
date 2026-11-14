using System.Globalization;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>
/// Guide tab that opens a new extension window (a floating docking window) so the window is not empty.
/// Files and terminals dragged or moved there sit beside it; closing it leaves the window to its other tabs.
/// </summary>
public sealed class ExtensionWindowDocumentViewModel : DocumentViewModel
{
    public const string ContentIdPrefix = "doc:extension:";

    public ExtensionWindowDocumentViewModel(int number, Action<DocumentViewModel> close)
        : base(ContentIdPrefix + number.ToString(CultureInfo.InvariantCulture), "확장 창", close)
    {
        Number = number;
    }

    public int Number { get; }

    public override bool IsRestorable => true;

    /// <summary>Number of a guide tab's ContentId, or null when the id is not one.</summary>
    public static int? NumberOf(string contentId) =>
        contentId.StartsWith(ContentIdPrefix, StringComparison.Ordinal)
        && int.TryParse(contentId.AsSpan(ContentIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
            ? number
            : null;
}
