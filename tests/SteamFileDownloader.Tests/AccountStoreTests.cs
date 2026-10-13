namespace SteamFileDownloader.Tests;

public class AccountStoreTests
{
    [Test]
    [Arguments("anonymous")]
    [Arguments("Anonymous")]
    public async Task TheAnonymousUsernameNeedsNoSavedLogin(string username)
    {
        await Assert.That(AccountStore.LoadCredentials(username)?.IsAnonymous).IsTrue();
    }
}
