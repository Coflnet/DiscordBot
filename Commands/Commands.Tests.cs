using Coflnet.Payments.Client.Model;
using NUnit.Framework;

namespace Coflnet.Discord;

public class CommandsTests
{
    [Test]
    public void TransactionsDescriptionStartsWithBalance()
    {
        var transactions = new[]
        {
            new ExternalTransaction("12", "premium", "ref-1", -1800, new DateTime(2026, 9, 1)),
            new ExternalTransaction("13", "topup", "ref-2", 5400, new DateTime(2026, 9, 2))
        };

        var description = global::Commands.TransactionsDescription(
            new User(balance: 3600, availableBalance: 3600), transactions);

        var lines = description.Split('\n');
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("Balance: **3,600** CoflCoins"));
            Assert.That(lines, Has.Length.EqualTo(4));
            Assert.That(lines[2], Does.StartWith("12 ").And.EndWith("-1800 premium - ref-1"));
            Assert.That(lines[3], Does.StartWith("13 ").And.EndWith("5400 topup - ref-2"));
        });
    }

    [Test]
    public void TransactionsDescriptionShowsAvailableBalanceWhenReserved()
    {
        var description = global::Commands.TransactionsDescription(
            new User(balance: 3600, availableBalance: 1800), Array.Empty<ExternalTransaction>());

        Assert.That(description.Split('\n')[0],
            Is.EqualTo("Balance: **3,600** CoflCoins (available: **1,800**)"));
    }
}
