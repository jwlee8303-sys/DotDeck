using DotDeck.Core.Paths;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>Start page: shows where DotDeck reads and writes, and whether sandbox mode is active.</summary>
public sealed class WelcomeDocumentViewModel : DocumentViewModel
{
    public const string Id = "doc:welcome";

    public WelcomeDocumentViewModel(AppPaths paths, string version, Action<DocumentViewModel> close)
        : base(Id, "시작", close)
    {
        Paths = paths;
        Version = version;
    }

    public AppPaths Paths { get; }

    public string Version { get; }

    public string HomeModeText => Paths.HomeSource switch
    {
        HomeSource.CommandLine => "샌드박스 (--home)",
        HomeSource.EnvironmentVariable => "샌드박스 (DOTDECK_HOME)",
        _ => "실제 사용자 홈",
    };

    public override bool IsRestorable => true;
}
