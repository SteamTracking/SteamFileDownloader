using System.Buffers.Text;
using System.Text;

namespace SteamFileDownloader.Tests;

public class AccountStoreTests
{
    private static string Describe(string path) =>
        string.Join(",", AccountStore.GetLogins(path).Select(static l => $"{l.Username}:{(l.IsDefault ? "default" : "-")}:{l.RefreshToken}"));

    private static string SaveAll(TempDirectory temp, params string[] usernames)
    {
        var path = Path.Combine(temp.Path, "account.json");

        foreach (var username in usernames)
        {
            AccountStore.Save(new SavedAccount(username, $"token-{username}"), path);
        }

        return path;
    }

    [Test]
    public async Task LoginsListTheDefaultFirst()
    {
        using var temp = new TempDirectory();
        var path = SaveAll(temp, "b", "c", "a");

        await Assert.That(Describe(path)).IsEqualTo("a:default:token-a,b:-:token-b,c:-:token-c");
    }

    [Test]
    public async Task RemovingTheDefaultOfTwoMakesTheOtherTheDefault()
    {
        using var temp = new TempDirectory();
        var path = SaveAll(temp, "first", "Second");

        await Assert.That(AccountStore.Remove(["second"], path)).IsEqualTo("first");
        await Assert.That(Describe(path)).IsEqualTo("first:default:token-first");
    }

    [Test]
    public async Task RemovingTheDefaultOfSeveralLeavesNoDefault()
    {
        using var temp = new TempDirectory();
        var path = SaveAll(temp, "a", "b", "c");

        await Assert.That(AccountStore.Remove(["c"], path)).IsNull();
        await Assert.That(Describe(path)).IsEqualTo("a:-:token-a,b:-:token-b");
    }

    [Test]
    public async Task RemovingDownToOneAccountMakesItTheDefault()
    {
        using var temp = new TempDirectory();
        var path = SaveAll(temp, "a", "b", "c");

        AccountStore.Remove(["c"], path);

        await Assert.That(AccountStore.Remove(["a"], path)).IsEqualTo("b");
        await Assert.That(Describe(path)).IsEqualTo("b:default:token-b");
    }

    [Test]
    public async Task RemovingAnotherAccountKeepsTheDefault()
    {
        using var temp = new TempDirectory();
        var path = SaveAll(temp, "a", "b", "c");

        await Assert.That(AccountStore.Remove(["a"], path)).IsEqualTo("c");
        await Assert.That(Describe(path)).IsEqualTo("c:default:token-c,b:-:token-b");
    }

    [Test]
    public async Task RemovingSeveralAccountsAtOnceMakesTheRemainingOneTheDefault()
    {
        using var temp = new TempDirectory();
        var path = SaveAll(temp, "a", "b", "c");

        await Assert.That(AccountStore.Remove(["c", "A"], path)).IsEqualTo("b");
        await Assert.That(Describe(path)).IsEqualTo("b:default:token-b");
    }

    [Test]
    [Arguments("anonymous")]
    [Arguments("Anonymous")]
    public async Task TheAnonymousUsernameNeedsNoSavedLogin(string username)
    {
        await Assert.That(AccountStore.LoadCredentials(username)?.IsAnonymous).IsTrue();
    }

    private static string Jwt(string payload) =>
        $"eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.c2lnbmF0dXJl";

    [Test]
    public async Task ExpiryIsReadFromTheToken()
    {
        var expiry = AccountStore.GetExpiry(Jwt("""{"iss":"steam","sub":"76561197960287930","exp":1800000000}"""));

        await Assert.That(expiry).IsEqualTo(new DateTimeOffset(2027, 1, 15, 8, 0, 0, TimeSpan.Zero));
    }

    [Test]
    [Arguments("not-a-jwt")]
    [Arguments("a.!!!.c")]
    [Arguments("a.bm90IGpzb24.c")]
    public async Task ExpiryOfAnUnreadableTokenIsNull(string token)
    {
        await Assert.That(AccountStore.GetExpiry(token)).IsNull();
    }

    [Test]
    [Arguments("""{"sub":"76561197960287930"}""")]
    [Arguments("""{"exp":"soon"}""")]
    [Arguments("""[1]""")]
    public async Task ExpiryOfATokenWithoutAnExpiryIsNull(string payload)
    {
        await Assert.That(AccountStore.GetExpiry(Jwt(payload))).IsNull();
    }
}
