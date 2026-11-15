namespace SteamFileDownloader.Tests;

public class LoginArgumentsTests
{
    private static LoginArguments Parse(string commandLine) =>
        LoginCommand.ParseArguments(commandLine.Length == 0 ? [] : commandLine.Split(' '));

    private static string Describe(string commandLine)
    {
        var arguments = Parse(commandLine);

        return $"{arguments.Command}|{arguments.Login.Username}|{string.Join(",", arguments.Usernames)}";
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
        await Assert.That(() => Parse(commandLine)).Throws<UsageException>().WithMessageContaining(error);
    }

    [Test]
    public async Task UsernameLikeASubcommandGetsBothHints()
    {
        var exception = await Assert.That(() => Parse("lisa")).Throws<UsageException>();

        await Assert.That(exception?.Message.Replace(Help.ExeName, "SFD", StringComparison.Ordinal))
            .Contains("Unknown login subcommand \"lisa\". Did you mean list? Expected list, remove. To log in as lisa: SFD login --username lisa. ");
    }
}
