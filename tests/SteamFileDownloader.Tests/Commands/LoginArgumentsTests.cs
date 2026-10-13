namespace SteamFileDownloader.Tests;

public class LoginArgumentsTests
{
    private static string? Describe(string commandLine)
    {
        var arguments = LoginCommand.ParseArguments(commandLine.Length == 0 ? [] : commandLine.Split(' '));

        return arguments == null ? null : $"{arguments.Command}|{arguments.Username}|{string.Join(",", arguments.Usernames)}";
    }

    [Test]
    [Arguments("", "login||")]
    [Arguments("--username someone", "login|someone|")]
    [Arguments("list", "login list||")]
    [Arguments("remove a B", "login remove||a,B")]
    public async Task CommandLinesAreParsed(string commandLine, string expected)
    {
        await Assert.That(Describe(commandLine)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("someone", "login takes the name as --username, like: ")]
    [Arguments("list extra", "Unexpected argument \"extra\"")]
    [Arguments("list --username x", "login list takes no options.")]
    [Arguments("remove -- x", "login remove takes usernames, like: ")]
    [Arguments("a b", "Unexpected argument \"a\"")]
    public async Task InvalidCommandLinesAreRejected(string commandLine, string error)
    {
        await Assert.That(Describe(commandLine)).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(error);
    }
}
