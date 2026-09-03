using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Coflnet.Discord;

public class RewardCommandsTests
{
    [Test]
    public async Task CommandsExposeExpectedShapeAndOnlyApprovedSources()
    {
        using var interactions = new InteractionService(new DiscordRestClient());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton((RewardLedgerClient)RuntimeHelpers.GetUninitializedObject(
                typeof(RewardLedgerClient)))
            .BuildServiceProvider();
        var module = await interactions.AddModuleAsync<RewardCommands>(services);
        var commands = module.SlashCommands.ToDictionary(command => command.Name);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Keys, Is.EquivalentTo(
                new[] { "record", "pending", "query", "cancel" }));
            Assert.That(Required(commands["record"]), Is.EquivalentTo(new[]
            {
                "recipient", "source", "amount-cents", "offer-version",
                "reference", "reason"
            }));
            Assert.That(commands["record"].Parameters.Select(item => item.Name),
                Has.Member("pending"));
            Assert.That(Required(commands["pending"]), Is.EquivalentTo(new[]
            {
                "email", "amount-cents", "offer-version", "reference", "reason"
            }));
            Assert.That(Required(commands["query"]), Is.EquivalentTo(new[] { "email" }));
            Assert.That(Required(commands["cancel"]), Is.EquivalentTo(new[]
            {
                "email", "entry", "reason"
            }));
            Assert.That(Enum.GetValues<RewardGrantSource>(),
                Is.EquivalentTo(new[]
                    { RewardGrantSource.Report, RewardGrantSource.Referral }));
            Assert.That(RewardCommands.Reference(
                    RewardGrantSource.Report, " issue-123 "),
                Is.EqualTo("discord:report:issue-123"));
            Assert.That(() => RewardCommands.Reference(
                    RewardGrantSource.Referral, " "),
                Throws.ArgumentException);
            Assert.That(() => RewardCommands.Reference(
                    RewardGrantSource.Report, "reporter@example.com"),
                Throws.ArgumentException);
            var emailId = RewardCommands.RecipientId(" Reporter@Example.com ");
            Assert.That(emailId, Is.EqualTo(
                RewardCommands.RecipientId("reporter@example.com")));
            Assert.That(emailId, Does.StartWith("email-sha256-v1:"));
            Assert.That(emailId, Does.Not.Contain("reporter"));
            Assert.That(emailId, Is.Not.EqualTo(
                RewardCommands.RecipientId("other@example.com")));
            Assert.That(() => RewardCommands.RecipientId("not-an-email@"),
                Throws.ArgumentException);
            Assert.That(RewardCommands.EvidenceReference(" discord:123 "),
                Is.EqualTo("discord:123"));
            Assert.That(() => RewardCommands.EvidenceReference("raw-value"),
                Throws.ArgumentException);
            Assert.That(() => RewardCommands.EvidenceReference(
                    "email:reporter@example.com"),
                Throws.ArgumentException);
            Assert.That(() => RewardCommands.LedgerText(
                    "Contact reporter@example.com", "reason", 300),
                Throws.ArgumentException);
            Assert.That(RewardCommands.OptionalEntryId(""), Is.Null);
            Assert.That(RewardCommands.OptionalEntryId("   "), Is.Null);
            var entryId = Guid.NewGuid();
            Assert.That(RewardCommands.OptionalEntryId(entryId.ToString()),
                Is.EqualTo(entryId));
            Assert.That(RewardCommands.EntryId($" {entryId} ", "entry"),
                Is.EqualTo(entryId));
            Assert.That(() => RewardCommands.EntryId("not-a-guid", "entry"),
                Throws.ArgumentException);
            Assert.That(() => RewardCommands.EntryId("", "entry"),
                Throws.ArgumentException);
        });

        static IEnumerable<string> Required(SlashCommandInfo command) =>
            command.Parameters.Where(item => item.IsRequired).Select(item => item.Name);
    }

    [Test]
    public async Task ClientWritesIdempotentAwardWithExistingCredential()
    {
        const string email = "Reporter@Example.com";
        var entry = new RewardGrantEntry(Guid.NewGuid());
        var handler = new Handler(JsonSerializer.Serialize(
            new RewardGrantResult(entry, true)));
        var client = new RewardLedgerClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["REWARDS:WRITE_TOKEN"] = new string('a', 32)
                }).Build());

        var result = await client.Record(new(
            "discord:report:issue-123", RewardCommands.RecipientId(email), 2,
            RewardGrantSource.Report, 500,
            "reports-v1", "valid report", null));

        Assert.Multiple(() =>
        {
            Assert.That(result.Entry.Id, Is.EqualTo(entry.Id));
            Assert.That(handler.Path, Is.EqualTo("/api/rewards/entries"));
            Assert.That(handler.Token, Is.EqualTo(new string('a', 32)));
            Assert.That(handler.Body, Does.Contain("discord:report:issue-123"));
            Assert.That(handler.Body, Does.Contain("\"kind\":2"));
            Assert.That(handler.Body, Does.Contain("\"source\":1"));
            Assert.That(handler.Body, Does.Not.Contain(email).IgnoreCase);
        });
    }

    [Test]
    public async Task ClientWritesPendingReportWithoutAnEmailInThePayload()
    {
        const string email = "Reporter@Example.com";
        var entry = new RewardGrantEntry(Guid.NewGuid());
        var handler = new Handler(JsonSerializer.Serialize(
            new RewardGrantResult(entry, true)));
        var client = new RewardLedgerClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["REWARDS:WRITE_TOKEN"] = new string('a', 32)
                }).Build());

        await client.Record(new(
            "discord:report:issue-124", RewardCommands.RecipientId(email), 1,
            RewardGrantSource.Report, 500,
            "reports-v1", "unreviewed report", null));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Body, Does.Contain("\"kind\":1"));
            Assert.That(handler.Body, Does.Contain("\"source\":1"));
            Assert.That(handler.Body, Does.Contain("\"relatedEntryId\":null"));
            Assert.That(handler.Body, Does.Not.Contain(email).IgnoreCase);
        });
    }

    [Test]
    public async Task ClientWritesAwardRelatedToItsPendingEntry()
    {
        const string email = "Reporter@Example.com";
        var pendingId = Guid.NewGuid();
        var entry = new RewardGrantEntry(Guid.NewGuid());
        var handler = new Handler(JsonSerializer.Serialize(
            new RewardGrantResult(entry, true)));
        var client = new RewardLedgerClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["REWARDS:WRITE_TOKEN"] = new string('a', 32)
                }).Build());

        await client.Record(new(
            "discord:report:issue-124-award", RewardCommands.RecipientId(email), 2,
            RewardGrantSource.Report, 500,
            "reports-v1", "approved report", null, pendingId));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Body, Does.Contain("\"kind\":2"));
            Assert.That(handler.Body, Does.Contain(
                $"\"relatedEntryId\":\"{pendingId}\""));
            Assert.That(handler.Body, Does.Not.Contain(email).IgnoreCase);
        });
    }

    [Test]
    public async Task ClientWritesCancellationRelatedToItsPendingEntryWithoutSource()
    {
        const string email = "Reporter@Example.com";
        var pendingId = Guid.NewGuid();
        var entry = new RewardGrantEntry(Guid.NewGuid());
        var handler = new Handler(JsonSerializer.Serialize(
            new RewardGrantResult(entry, true)));
        var client = new RewardLedgerClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["REWARDS:WRITE_TOKEN"] = new string('a', 32)
                }).Build());

        await client.Record(new(
            $"discord:report:cancel:{pendingId:N}", RewardCommands.RecipientId(email),
            6, null, 0, null, "report was invalid", null, pendingId));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Body, Does.Contain("\"kind\":6"));
            Assert.That(handler.Body, Does.Contain("\"source\":null"));
            Assert.That(handler.Body, Does.Contain("\"remunerationEurCents\":0"));
            Assert.That(handler.Body, Does.Contain(
                $"\"relatedEntryId\":\"{pendingId}\""));
            Assert.That(handler.Body, Does.Not.Contain(email).IgnoreCase);
        });
    }

    [Test]
    public async Task ClientReadsBalanceAndLedgerWithoutAnEmailInTheUrl()
    {
        const string email = "Researcher@Example.com";
        var accountId = RewardCommands.RecipientId(email);
        var entryId = Guid.NewGuid();
        var handler = new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/balance", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new RewardBalanceInfo(
                    accountId, "EUR", 0, 500, 0, 500, 7000))
                : JsonSerializer.Serialize(new[]
                {
                    new RewardLedgerEntryInfo(entryId, "discord:report:issue-123", 2,
                        500, "valid report", DateTime.UtcNow)
                }));
        var client = new RewardLedgerClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["REWARDS:WRITE_TOKEN"] = new string('a', 32)
                }).Build());

        var balance = await client.GetBalance(accountId);
        var balancePath = handler.Path;
        var balanceToken = handler.Token;
        var ledger = await client.GetLedger(accountId);
        var ledgerPath = handler.Path;

        Assert.Multiple(() =>
        {
            Assert.That(balance.AvailableEurCents, Is.EqualTo(500));
            Assert.That(balancePath, Does.Contain("/balance"));
            Assert.That(balancePath, Does.Contain(Uri.EscapeDataString(accountId)));
            Assert.That(balancePath, Does.Not.Contain("researcher").IgnoreCase);
            Assert.That(balanceToken, Is.EqualTo(new string('a', 32)));
            Assert.That(ledger, Has.Count.EqualTo(1));
            Assert.That(ledger[0].Id, Is.EqualTo(entryId));
            Assert.That(ledgerPath, Does.Contain("/ledger"));
            Assert.That(ledgerPath, Does.Not.Contain("researcher").IgnoreCase);
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, string> respond)
        : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Token { get; private set; }
        public string? Body { get; private set; }

        public Handler(string body) : this(_ => body)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Token = request.Headers.Authorization?.Parameter;
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
