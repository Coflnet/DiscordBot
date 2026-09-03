using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Coflnet.Discord;

public class CreatorReviewCommandsTests
{
    [Test]
    public async Task DiscordAcceptsTheCreatorCommandSchema()
    {
        using var interactions = new InteractionService(new DiscordRestClient());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton((Persistence)RuntimeHelpers.GetUninitializedObject(
                typeof(Persistence)))
            .AddSingleton((DiscordHandler)RuntimeHelpers.GetUninitializedObject(
                typeof(DiscordHandler)))
            .AddSingleton((CreatorOnboardingClient)RuntimeHelpers.GetUninitializedObject(
                typeof(CreatorOnboardingClient)))
            .BuildServiceProvider();
        var module = await interactions.AddModuleAsync<CreatorReviewCommands>(
            services);

        Assert.That(module.SlashCommands.Select(item => item.Name),
            Is.EquivalentTo(new[]
                { "review", "request-guardian", "set-status", "show" }));
        var review = module.SlashCommands.Single(item => item.Name == "review");
        Assert.Multiple(() =>
        {
            Assert.That(review.Parameters.Where(item => item.IsRequired)
                    .Select(item => item.Name),
                Is.EquivalentTo(new[]
                {
                    "applicant", "application", "decision", "residence",
                    "capacity", "privacy-notice", "rule-version", "reason"
                }));
            Assert.That(review.Parameters.Single(item => item.Name == "verification")
                .IsRequired, Is.False);
            Assert.That(review.Parameters.Single(item => item.Name == "tax-document")
                .IsRequired, Is.False);
            Assert.That(review.Parameters.Any(item => item.Name == "adult-from"),
                Is.False);
        });
    }

    [Test]
    public void AcceptsOnlyExactDiscordMessageLinksAndUtcDates()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CreatorReviewCommands.TryMessageLink(
                "https://discord.com/channels/12345678901234567/22345678901234567/32345678901234567",
                out var guild, out var channel, out var message), Is.True);
            Assert.That(guild, Is.EqualTo(12345678901234567UL));
            Assert.That(channel, Is.EqualTo(22345678901234567UL));
            Assert.That(message, Is.EqualTo(32345678901234567UL));
            Assert.That(CreatorReviewCommands.TryMessageLink(
                "https://example.com/channels/12345678901234567/22345678901234567/32345678901234567",
                out _, out _, out _), Is.False);
            Assert.That(CreatorReviewCommands.TryUtcDate(
                "2026-08-31", out var date), Is.True);
            Assert.That(date.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(CreatorReviewCommands.TryUtcDate(
                "31.08.2026", out _), Is.False);
        });
    }

    [Test]
    public void CreatorReviewsAreLimitedToTheReviewer()
    {
        var attributes = typeof(CreatorReviewCommands).GetCustomAttributes(true)
            .Select(item => item.GetType().Name);
        Assert.Multiple(() =>
        {
            Assert.That(CreatorReviewCommands.IsAuthorizedReviewer(
                CreatorReviewCommands.ReviewerId), Is.True);
            Assert.That(CreatorReviewCommands.IsAuthorizedReviewer(1), Is.False);
            Assert.That(attributes, Does.Not.Contain(
                "DefaultMemberPermissionsAttribute"));
            Assert.That(attributes, Does.Not.Contain(
                "RequireUserPermissionAttribute"));
        });
    }

    [Test]
    public void GuardianIdentityAndComponentIdAreBounded()
    {
        var customId = CreatorReviewCommands.GuardianCustomId(
            Guid.NewGuid(), new string('b', 64), "en");

        Assert.Multiple(() =>
        {
            Assert.That(customId.Length, Is.LessThanOrEqualTo(100));
            Assert.That(CreatorReviewCommands.TryDiscordAccount(
                "discord:267680402594988033", out var id), Is.True);
            Assert.That(id, Is.EqualTo(267680402594988033));
            Assert.That(CreatorReviewCommands.TryDiscordAccount(
                "coflnet:267680402594988033", out _), Is.False);
        });
    }

    [Test]
    public async Task ReviewClientUsesReviewerCredentialAndAuthenticatedActor()
    {
        var response = Review();
        var handler = new Handler(JsonSerializer.Serialize(response));
        var client = new CreatorOnboardingClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["CREATOR_ONBOARDING:REVIEW_TOKEN"] = new string('a', 32)
                }).Build());

        var stored = await client.Append(response.Next(
            CreatorOnboardingStatus.Suspended, "manual suspension"), 42);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Id, Is.EqualTo(response.Id));
            Assert.That(handler.Path, Is.EqualTo("/api/creator-onboarding/reviews"));
            Assert.That(handler.Token, Is.EqualTo(new string('a', 32)));
            Assert.That(handler.Reviewer, Is.EqualTo("discord:42"));
            Assert.That(handler.Body, Does.Contain("manual suspension"));
        });
    }

    private static CreatorReview Review() => new(
        Guid.NewGuid(), "creator", "e7246661de77474f94627fabf9880f60",
        CreatorOnboardingStatus.Approved, "DE", "DE",
        CreatorSellerType.Individual, "DE",
        CreatorCapacityStatus.AdultDeclared,
        CreatorTaxDocumentRoute.Statement,
        "privacy-2026-08-31", "verification:1",
        null, null, null,
        "discord:1:2:3", new string('b', 64), "discord:42",
        DateTime.UtcNow, "rules-v1", DateTime.UtcNow.AddYears(1),
        "checklist complete");

    private sealed class Handler(string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Token { get; private set; }
        public string? Reviewer { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Token = request.Headers.Authorization?.Parameter;
            Reviewer = request.Headers.GetValues("X-Reviewer-Id").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
