using System.Runtime.CompilerServices;
using System.Text.Json;
using Coflnet.Sky.McConnect.Api;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Coflnet.Discord;

public class CreatorPayoutCommandsTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task PayoutCommandsAreRegisteredInTheCreatorGroup()
    {
        using var interactions = new InteractionService(new DiscordRestClient());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton((Persistence)RuntimeHelpers.GetUninitializedObject(typeof(Persistence)))
            .AddSingleton((DiscordHandler)RuntimeHelpers.GetUninitializedObject(typeof(DiscordHandler)))
            .AddSingleton<IConnectApi>((ConnectApi)RuntimeHelpers.GetUninitializedObject(typeof(ConnectApi)))
            .AddSingleton((CreatorOnboardingClient)RuntimeHelpers.GetUninitializedObject(typeof(CreatorOnboardingClient)))
            .AddSingleton((RewardLedgerClient)RuntimeHelpers.GetUninitializedObject(typeof(RewardLedgerClient)))
            .BuildServiceProvider();
        var module = await interactions.AddModuleAsync<CreatorReviewCommands>(services);
        var commands = module.SlashCommands.ToDictionary(item => item.Name);

        Assert.Multiple(() =>
        {
            Assert.That(commands["payout-request"].Parameters.Where(p => p.IsRequired).Select(p => p.Name),
                Is.EquivalentTo(new[] { "user", "invoice_number" }));
            Assert.That(commands["payout-request"].Parameters.Single(p => p.Name == "amount").IsRequired, Is.False);
            Assert.That(commands["payout-complete"].Parameters.Where(p => p.IsRequired).Select(p => p.Name),
                Is.EquivalentTo(new[] { "user", "invoice_number", "payment_reference" }));
            Assert.That(commands["payout-status"].Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "user" }));
            Assert.That(module.ModalCommands.Select(item => item.Name), Has.Some.Contain("creator-payout"));
        });
    }

    [Test]
    public void OnlyTheNamedReviewerPassesTheGateOfThePayoutCommands()
    {
        Assert.That(CreatorReviewCommands.IsAuthorizedReviewer(1), Is.False);
        Assert.That(CreatorReviewCommands.IsAuthorizedReviewer(CreatorReviewCommands.ReviewerId), Is.True);
    }

    [Test]
    public void EvidenceJsonHasExactlyTheServerKeysAndNetArithmetic()
    {
        var json = CreatorPayoutDocumentService.BuildEvidenceJson(7000, 0, 0, 0, "selfbilling:SB-2026-0001", Sha);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
            {
                "grossEurCents", "creatorVatEurCents", "withholdingTaxEurCents",
                "solidaritySurchargeEurCents", "netEurCents",
                "payoutEvidenceReference", "payoutEvidenceSha256"
            }));
            Assert.That(root.GetProperty("netEurCents").GetInt64(), Is.EqualTo(7000));
            Assert.That(json.Length, Is.LessThan(4000));
        });
        using var withTax = JsonDocument.Parse(CreatorPayoutDocumentService.BuildEvidenceJson(7000, 100, 50, 20, "a:b", Sha));
        Assert.That(withTax.RootElement.GetProperty("netEurCents").GetInt64(), Is.EqualTo(7030));
    }

    [Test]
    public void PayoutDetailsCarryTheNamespacedPaymentReference()
    {
        using var doc = JsonDocument.Parse(CreatorPayoutDocumentService.BuildPayoutDetailsJson(" paypal:TX1 "));
        Assert.That(doc.RootElement.GetProperty("paymentReference").GetString(), Is.EqualTo("paypal:TX1"));
    }

    [Test]
    public void BookingJsonMatchesTheContractAndExcludesIdentity()
    {
        var pdf = CreatorPayoutDocumentService.RenderPdf(Input());
        var sha = CreatorPayoutDocumentService.Sha256Hex(pdf);
        var json = CreatorPayoutDocumentService.BuildBookingJson(
            "SB-2026-0001", new DateOnly(2026, 10, 10), 7000, "acct-1", "GB", sha);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
            {
                "schemaVersion", "provider", "payoutId", "voucherNumber", "voucherDate", "currency",
                "grossCents", "creatorVatCents", "withholdingTaxCents", "solidaritySurchargeCents",
                "netCents", "rewardAccountId", "creatorCountry", "taxDocumentRoute", "reverseCharge",
                "ledgerReference", "evidence"
            }));
            Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("voucherDate").GetString(), Is.EqualTo("2026-10-10"));
            Assert.That(root.GetProperty("taxDocumentRoute").GetString(), Is.EqualTo("UkSelfBilling"));
            Assert.That(root.GetProperty("reverseCharge").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("ledgerReference").GetString(), Is.EqualTo("discord:payout-request:SB-2026-0001"));
            Assert.That(root.GetProperty("evidence").GetProperty("pdf").GetString(), Is.EqualTo("SB-2026-0001.pdf"));
            Assert.That(root.GetProperty("evidence").GetProperty("pdfSha256").GetString(), Is.EqualTo(sha));
            Assert.That(json, Does.Not.Contain("Jane").And.Not.Contain("GB29"));
        });
    }

    [Test]
    public void PdfWithBankDetailsIsGenerated()
    {
        var pdf = CreatorPayoutDocumentService.RenderPdf(Input());
        Assert.That(pdf, Is.Not.Empty);
        Assert.That(System.Text.Encoding.ASCII.GetString(pdf, 0, 4), Is.EqualTo("%PDF"));
    }

    [Test]
    public void ValidationHelpersRejectBadInput()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CreatorPayoutDocumentService.ValidateAmount(6999, 10000, 7000), Does.Contain("below"));
            Assert.That(CreatorPayoutDocumentService.ValidateAmount(7001, 7000, 7000), Does.Contain("exceeds"));
            Assert.That(CreatorPayoutDocumentService.ValidateAmount(7000, 7000, 7000), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ValidatePaymentReference("TX1"), Is.Not.Null);
            Assert.That(CreatorPayoutDocumentService.ValidatePaymentReference("paypal:"), Is.Not.Null);
            Assert.That(CreatorPayoutDocumentService.ValidatePaymentReference("paypal:TX1"), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ToCents(70.005m), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ToCents(70m), Is.EqualTo(7000));
            Assert.That(CreatorPayoutDocumentService.ValidateInvoiceNumber("a/b"), Is.Not.Null);
            Assert.That(CreatorPayoutDocumentService.ParseAddress("A;B\\nC"), Is.EqualTo(new[] { "A", "B", "C" }));
        });
    }

    [Test]
    public void OnboardingMustBeApprovedUkSelfBilling()
    {
        var approved = Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.UkSelfBilling);
        var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.Multiple(() =>
        {
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(approved, now), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(null, now), Is.Not.Null);
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Pending, CreatorTaxDocumentRoute.UkSelfBilling), now), Does.Contain("Pending"));
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.Statement), now), Does.Contain("Statement"));
        });
    }

    [Test]
    public void ModalCustomIdRoundTrips()
    {
        var id = CreatorPayoutMessages.EncodeModalId(267680402594988033, "SB-2026-0001", 7000);
        var parts = id.Split(':');
        Assert.That(id.Length, Is.LessThan(100));
        Assert.That(CreatorPayoutMessages.TryDecodeModalId(parts[1], parts[2], parts[3], out var user, out var cents), Is.True);
        Assert.That((user, cents), Is.EqualTo((267680402594988033UL, (long?)7000)));
        var none = CreatorPayoutMessages.EncodeModalId(5, "SB-2026-0001", null).Split(':');
        Assert.That(CreatorPayoutMessages.TryDecodeModalId(none[1], none[2], none[3], out _, out cents), Is.True);
        Assert.That(cents, Is.Null);
        Assert.That(CreatorPayoutMessages.TryDecodeModalId("x", "SB-2026-0001", "-", out _, out _), Is.False);
        Assert.That(CreatorPayoutMessages.TryDecodeModalId("5", "SB-2026-0001", "-5", out _, out _), Is.False);
    }

    [Test]
    public void PayoutRequestReplyLeadsWithAmountAndShowsBankDetails()
    {
        var text = CreatorPayoutMessages.PayoutRequestReply(
            7000, "Jane Doe", ["Jane Doe", "IBAN GB29 NWBK 6016 1331 9268 19"],
            "SB-2026-0001", "acct-1", "discord:payout-request:SB-2026-0001", Sha, 1250);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("Pay **70.00 EUR** to Jane Doe"));
            Assert.That(text, Does.Contain("IBAN GB29 NWBK 6016 1331 9268 19"));
            Assert.That(text, Does.Contain("SB-2026-0001").And.Contain("acct-1").And.Contain(Sha));
            Assert.That(text, Does.Contain("Remaining available balance: **12.50 EUR**"));
        });
    }

    [Test]
    public void PayoutCompleteReplyStatesAmountAndBalance()
    {
        var text = CreatorPayoutMessages.PayoutCompleteReply(
            7000, "SB-2026-0001", "paypal:TX1", new("a", "EUR", 0, 500, 0, 500, 7000));
        Assert.That(text, Does.StartWith("Booked as paid: **70.00 EUR**").And.Contain("available 5.00 EUR"));
    }

    [Test]
    public void StatusBelowThresholdShowsMissingAmount()
    {
        var text = CreatorPayoutMessages.StatusReply(
            new("a", "EUR", 100, 5000, 0, 5000, 7000),
            Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.UkSelfBilling), []);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("not reached"));
            Assert.That(text, Does.Contain("0.00 EUR payable, 20.00 EUR missing"));
            Assert.That(text, Does.Contain("UkSelfBilling, country GB"));
            Assert.That(text, Does.Contain("Open payout requests: none"));
        });
    }

    [Test]
    public void StatusAtThresholdShowsPayableAndOpenRequest()
    {
        var request = new RewardLedgerEntryInfo(Guid.NewGuid(), "discord:payout-request:SB-1", 4, 7000, "r", DateTime.UtcNow);
        var closed = new RewardLedgerEntryInfo(Guid.NewGuid(), "discord:payout-request:SB-0", 4, 7000, "r", DateTime.UtcNow);
        var paid = new RewardLedgerEntryInfo(Guid.NewGuid(), "discord:payout:SB-0", 5, -7000, "r", DateTime.UtcNow, closed.Id);
        var text = CreatorPayoutMessages.StatusReply(
            new("a", "EUR", 0, 14000, 7000, 7000, 7000), null, [closed, paid, request]);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("(reached)"));
            Assert.That(text, Does.Contain("**70.00 EUR** payable now"));
            Assert.That(text, Does.Contain("discord:payout-request:SB-1").And.Not.Contain("SB-0"));
            Assert.That(CreatorPayoutMessages.OpenRequests([closed, paid, request]), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void FindOpenRequestSkipsPaidRequests()
    {
        var request = new RewardLedgerEntryInfo(Guid.NewGuid(), "discord:payout-request:SB-1", 4, 7000, "r", DateTime.UtcNow);
        var paid = new RewardLedgerEntryInfo(Guid.NewGuid(), "discord:payout:SB-1", 5, -7000, "r", DateTime.UtcNow, request.Id);
        Assert.Multiple(() =>
        {
            Assert.That(CreatorPayoutDocumentService.FindOpenRequest([request], "SB-1").Request, Is.EqualTo(request));
            Assert.That(CreatorPayoutDocumentService.FindOpenRequest([request, paid], "SB-1").Error, Does.Contain("already"));
            Assert.That(CreatorPayoutDocumentService.FindOpenRequest([], "SB-1").Error, Does.Contain("No payout request"));
        });
    }

    [Test]
    public void DefaultPeriodStartsAfterTheLastPayout()
    {
        var ledger = new[]
        {
            new RewardLedgerEntryInfo(Guid.NewGuid(), "a", 2, 100, "r", new DateTime(2026, 1, 5)),
            new RewardLedgerEntryInfo(Guid.NewGuid(), "p", 5, -100, "r", new DateTime(2026, 2, 1)),
            new RewardLedgerEntryInfo(Guid.NewGuid(), "b", 2, 100, "r", new DateTime(2026, 3, 7)),
            new RewardLedgerEntryInfo(Guid.NewGuid(), "c", 3, 100, "r", new DateTime(2026, 4, 9)),
        };
        Assert.That(CreatorPayoutDocumentService.DefaultPeriod(ledger, new DateOnly(2026, 10, 10)),
            Is.EqualTo("2026-03-07 – 2026-10-10"));
    }

    private static PayoutInvoiceInput Input() => new(
        "SB-2026-0001", new DateOnly(2026, 10, 10), "2026-09-01 – 2026-10-10",
        "Jane Doe", ["1 High Street", "London"], "GB", null,
        ["Jane Doe", "IBAN GB29 NWBK 6016 1331 9268 19"],
        new("Example GmbH", "Street 1;12345 City", "DE123456789"),
        7000, "acct-1", "discord:payout-request:SB-2026-0001");

    private static CreatorReview Review(CreatorOnboardingStatus status, CreatorTaxDocumentRoute route) => new(
        Guid.NewGuid(), "acct-1", "uuid", status, "GB", "GB", CreatorSellerType.Business,
        "GB-ENG", CreatorCapacityStatus.AdultDeclared, route, "v", null, null, null, null,
        "e:1", Sha, "rev", DateTime.UtcNow, "rule", null, "r");
}
