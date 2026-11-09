using System.Runtime.InteropServices;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.App.Tests;

/// <summary>Splits built command lines with the real Windows parser to prove they round-trip exactly.</summary>
public sealed partial class CommandLineRoundTripTests
{
    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    public static TheoryData<string[]> Cases => new()
    {
        new[] { "plain", "two words", "" },
        new[] { @"D:\web game\", @"C:\trailing\\", @"\\server\share\x y\" },
        new[] { "say \"hi\"", "\\\"", "a\\\\\"b", "\"" },
        new[] { "한글 경로\\폴더", "탭\t문자", "줄\n바꿈\r\n끝" },
        new[] { "100% & ^ | < > ( ) !", "it's 'single'", "-c", "approval_policy=\"never\"" },
        new[] { "developer_instructions=" + TomlString.Basic("C:\\a \"b\"\n둘째 줄") },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Built_command_line_splits_back_into_the_same_arguments(string[] arguments)
    {
        const string exe = @"C:\Program Files\Some Tool\tool.exe";

        var parsed = Split(WindowsCommandLine.Join(exe, arguments));

        Assert.Equal([exe, .. arguments], parsed);
    }
}
