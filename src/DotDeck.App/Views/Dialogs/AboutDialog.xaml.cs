using System.IO;
using System.Windows;
using DotDeck.App.Infrastructure.Windows;

namespace DotDeck.App.Views.Dialogs;

/// <summary>Help → DotDeck 정보: version, data folders and the shipped third-party notices (read-only).</summary>
public partial class AboutDialog : Window
{
    /// <summary>Notices and license files shipped next to DotDeck.exe (see DotDeck.App.csproj).</summary>
    public const string NoticesFile = "THIRD-PARTY-NOTICES.txt";

    public const string LicensesFolder = "licenses";

    private readonly Action<string> _reveal;

    public AboutDialog(string version, string home, string dataDir, Action<string> reveal)
    {
        _reveal = reveal;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        VersionText.Text = $"DotDeck {version}";
        PathsText.Text = $"홈: {home}\n데이터: {dataDir}";
        NoticesBox.Text = ReadNotices(AppContext.BaseDirectory);
        OpenFolderButton.IsEnabled = Directory.Exists(Path.Combine(AppContext.BaseDirectory, LicensesFolder));
    }

    /// <summary>The notices text, or a short message when the file is missing (a damaged install).</summary>
    internal static string ReadNotices(string appDirectory)
    {
        var path = Path.Combine(appDirectory, NoticesFile);
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{NoticesFile}을 읽지 못했습니다({ex.Message}). 설치 폴더를 확인하세요.";
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) =>
        _reveal(Path.Combine(AppContext.BaseDirectory, LicensesFolder));
}
