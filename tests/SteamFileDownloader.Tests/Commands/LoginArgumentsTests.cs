namespace SteamFileDownloader.Tests;

public class LoginArgumentsTests
{
    private static string? Describe(string commandLine)
    {
        var arguments = LoginCommand.ParseArguments(commandLine.Length == 0 ? [] : commandLine.Split(' '));

        return arguments == null ? null : $"{arguments.Command}|{arguments.Login.Username}|{string.Join(",", arguments.Usernames)}";
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
    [Arguments("lsit", "Unknown login subcommand \"lsit\". Did you mean list? Expected list, remove.")]
    [Arguments("remvoe someone", "Unknown login subcommand \"remvoe\". Did you mean remove?")]
    public async Task InvalidCommandLinesAreRejected(string commandLine, string error)
    {
        await Assert.That(Describe(commandLine)).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(error);
    }

    [Test]
    public async Task UsernameLikeASubcommandGetsBothHints()
    {
        await Assert.That(Describe("lisa")).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput().Replace(Help.ExeName, "SFD", StringComparison.Ordinal))
            .Contains("Unknown login subcommand \"lisa\". Did you mean list? Expected list, remove. To log in as lisa: SFD login --username lisa. ");
    }
}
