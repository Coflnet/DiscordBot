using System.Net;
using System.Runtime.CompilerServices;
using Coflnet.Sky.McConnect.Api;
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
            .AddSingleton<IConnectApi>((ConnectApi)RuntimeHelpers
                .GetUninitializedObject(typeof(ConnectApi)))
            .AddSingleton((CreatorOnboardingClient)RuntimeHelpers.GetUninitializedObject(
                typeof(CreatorOnboardingClient)))
            .AddSingleton((RewardLedgerClient)RuntimeHelpers.GetUninitializedObject(
                typeof(RewardLedgerClient)))
            .BuildServiceProvider();
        var module = await interactions.AddModuleAsync<CreatorReviewCommands>(
            services);

        Assert.That(module.SlashCommands.Select(item => item.Name),
            Is.EquivalentTo(new[]
                { "balance", "review", "request-guardian", "set-status", "show" }));
        Assert.That(module.SlashCommands.Single(item => item.Name == "balance").Parameters,
            Is.Empty, "Creators must only query their own linked account.");
        var review = module.SlashCommands.Single(item => item.Name == "review");
        Assert.Multiple(() =>
        {
            Assert.That(review.Parameters.Where(item => item.IsRequired)
                    .Select(item => item.Name),
                Is.EquivalentTo(new[]
                {
                    "applicant", "application", "residence", "capacity"
                }));
            Assert.That(review.Parameters.Single(item => item.Name == "decision")
                .IsRequired, Is.False);
            Assert.That(review.Parameters.Single(item => item.Name == "verification")
                .IsRequired, Is.False);
            Assert.That(review.Parameters.Single(item => item.Name == "tax-document")
                .IsRequired, Is.False);
            Assert.That(review.Parameters.Any(item => item.Name == "adult-from"),
                Is.False);
            Assert.That(review.Parameters.Single(item => item.Name == "minecraft-uuid")
                .IsRequired, Is.False);
        });
    }

    [Test]
    public void AReviewedMinecraftUuidDoesNotNeedALinkedDiscordAccount()
    {
        var primary = Guid.Parse("e7246661de77474f94627fabf9880f60");
        var secondary = Guid.Parse("f7246661de77474f94627fabf9880f61");
        var linked = new DiscordAccountInfo
        {
            UserId = "12",
            MinecraftUuid = primary,
            MinecraftUuids = [primary, secondary]
        };

        Assert.Multiple(() =>
        {
            Assert.That(CreatorReviewCommands.SelectIdentity(
                    null, primary.ToString("N"), out var reviewed),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.ReviewedUuid));
            Assert.That(reviewed, Is.EqualTo(primary));
            Assert.That(CreatorReviewCommands.SelectIdentity(
                    new DiscordAccountInfo(), primary.ToString(), out reviewed),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.ReviewedUuid));
            Assert.That(reviewed, Is.EqualTo(primary));
            Assert.That(CreatorReviewCommands.SelectIdentity(null, "", out _),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.Unlinked));
            Assert.That(CreatorReviewCommands.SelectIdentity(
                    new DiscordAccountInfo { UserId = "12" }, "", out _),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.Unlinked));
            Assert.That(CreatorReviewCommands.SelectIdentity(linked, "", out var stored),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.LinkedAccount));
            Assert.That(stored, Is.EqualTo(primary));
            Assert.That(CreatorReviewCommands.SelectIdentity(
                    linked, secondary.ToString("N"), out stored),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.LinkedAccount));
            Assert.That(stored, Is.EqualTo(secondary));
            Assert.That(CreatorReviewCommands.SelectIdentity(linked, "not-a-uuid", out _),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.Invalid));
            Assert.That(CreatorReviewCommands.SelectIdentity(
                    linked, Guid.Empty.ToString("N"), out _),
                Is.EqualTo(CreatorReviewCommands.CreatorIdentitySource.Invalid));
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
                "https://discord.com/channels/@me/22345678901234567/32345678901234567",
                out guild, out channel, out message), Is.True);
            Assert.That(guild, Is.Zero);
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

    [Test]
    public void ReviewClientReturnsSafeValidationMessage()
    {
        var handler = new Handler(
            "{\"slug\":\"reward\",\"message\":\"The tax document route does not match the tax residence\"}",
            HttpStatusCode.BadRequest);
        var client = new CreatorOnboardingClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["REFERRAL_BASE_URL"] = "https://referral.invalid",
                    ["CREATOR_ONBOARDING:REVIEW_TOKEN"] = new string('a', 32)
                }).Build());

        var exception = Assert.ThrowsAsync<HttpRequestException>(() =>
            client.Append(Review().Next(
                CreatorOnboardingStatus.Approved, "review"), 42));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Message,
                Is.EqualTo("The tax document route does not match the tax residence"));
        });
    }

    [Test]
    public void PaidSellingBlockersNameTheFieldToCorrect()
    {
        var uk = Review() with
        {
            ResidenceCountry = "GB",
            TaxResidenceCountry = "GB",
            CapacityJurisdiction = "GB-ENG",
            TaxDocumentRoute = CreatorTaxDocumentRoute.UkSelfBilling
        };

        Assert.Multiple(() =>
        {
            Assert.That(CreatorPublishing.PaidSellingBlockers(uk).Single(),
                Does.Contain("tax-document").And.Contain("UkSelfBilling")
                    .And.Contain("Individual").And.Contain("use Statement"));
            Assert.That(CreatorPublishing.PaidSellingBlockers(uk with
                {
                    SellerType = CreatorSellerType.Business
                }), Is.Empty);
            Assert.That(CreatorPublishing.PaidSellingBlockers(uk with
                {
                    SellerType = CreatorSellerType.Business,
                    VerificationReference = null
                }).Single(), Does.Contain("verification"));
            Assert.That(CreatorPublishing.PaidSellingBlockers(uk with
                {
                    TaxDocumentRoute = CreatorTaxDocumentRoute.Statement,
                    CapacityJurisdiction = "GB"
                }).Single(),
                Does.Contain("capacity-law").And.Contain("GB-ENG"));
            Assert.That(CreatorPublishing.PaidSellingBlockers(Review() with
                {
                    ResidenceCountry = "UK",
                    TaxResidenceCountry = "UK",
                    CapacityJurisdiction = "UK"
                }).Select(item => item.Split(' ')[0]),
                Is.EquivalentTo(new[] { "residence", "tax", "`capacity-law`", "`tax-document`" }));
            Assert.That(CreatorPublishing.PaidSellingBlockers(Review() with
                {
                    TaxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable
                }).Single(), Does.Contain("tax-document").And.Contain("unset"));
            Assert.That(CreatorPublishing.PaidSellingBlockers(Review()), Is.Empty);
        });
    }

    [Test]
    public void TheReviewSummaryStatesWhetherSellingWorks()
    {
        var now = DateTime.UtcNow;

        Assert.Multiple(() =>
        {
            Assert.That(CreatorPublishing.Summary(Review(), now),
                Does.Contain("Paid selling is enabled")
                    .And.Contain("e7246661de77474f94627fabf9880f60"));
            Assert.That(CreatorPublishing.Summary(Review() with
                {
                    TaxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable
                }, now),
                Does.StartWith("Free Configs").And.Contain("paid selling is blocked"));
            Assert.That(CreatorPublishing.Summary(Review() with
                {
                    Status = CreatorOnboardingStatus.Pending
                }, now),
                Does.StartWith("Publishing Configs is **blocked**")
                    .And.Contain("only Approved may publish"));
            Assert.That(CreatorPublishing.PublishingBlockers(Review() with
                {
                    ValidUntilUtc = now.AddDays(-1)
                }, now).Single(), Does.Contain("expired"));
            Assert.That(CreatorPublishing.PublishingBlockers(Review() with
                {
                    CapacityStatus = CreatorCapacityStatus.Minor16PlusWithGuardian,
                    RepresentativeAccountId = "discord:9"
                }, now).Single(), Does.Contain("request-guardian"));
            Assert.That(CreatorPublishing.PublishingBlockers(Review(), now), Is.Empty);
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

    private sealed class Handler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
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
            return new(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
