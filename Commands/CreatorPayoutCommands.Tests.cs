using Discord;
using System.Reflection;
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
            Assert.That(commands["payout-request"].Parameters.Select(p => p.Name), Is.EquivalentTo(new[]
                { "user", "invoice_number", "amount", "name", "address", "bank_details", "vat_id", "period" }));
            Assert.That(commands["payout-request"].Parameters.Any(p => p.IsRequired), Is.False,
                "Creators run it for themselves without any option.");
            Assert.That(commands["payout-request"].Parameters.All(p => p.Description.Length <= 100), Is.True);
            Assert.That(commands["payout-request"].Parameters, Has.Count.LessThanOrEqualTo(25));
            Assert.That(commands["payout-request"].Parameters.Single(p => p.Name == "address").Description, Does.Contain(";"));
            Assert.That(commands["payout-request"].Parameters.Single(p => p.Name == "bank_details").Description, Does.Contain(";"));
            Assert.That(commands["payout-complete"].Parameters.Where(p => p.IsRequired).Select(p => p.Name),
                Is.EquivalentTo(new[] { "user", "invoice_number", "payment_reference" }));
            Assert.That(commands["payout-status"].Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "user" }));
            Assert.That(module.ModalCommands.Select(item => item.Name), Has.Some.Contain("creator-payout"));
        });
    }

    [Test]
    public async Task EveryPayoutCommandIsAvailableInTheBotDm()
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
        var group = typeof(CreatorReviewCommands);
        // Method level overrides would replace the group's contexts, so none may exist.
        foreach (var name in new[] { "PayoutRequest", "PayoutStatus", "PayoutComplete" })
        {
            var method = group.GetMethod(name)!;
            Assert.That(method.GetCustomAttributes(typeof(CommandContextTypeAttribute), false), Is.Empty, name);
            Assert.That(method.GetCustomAttributes(typeof(IntegrationTypeAttribute), false), Is.Empty, name);
            Assert.That(method.GetCustomAttributes(typeof(RequireContextAttribute), false), Is.Empty, name);
        }
        var contexts = group.GetCustomAttribute<CommandContextTypeAttribute>()!.ContextTypes;
        Assert.That(contexts, Does.Contain(InteractionContextType.BotDm));
        Assert.That(module.SlashCommands.Select(item => item.Name),
            Is.SupersetOf(new[] { "payout-request", "payout-status", "payout-complete", "balance", "apply" }));
    }

    [Test]
    public void PayoutRequestAuthorisationFollowsTheRule()
    {
        var reviewer = CreatorReviewCommands.ReviewerId;
        Assert.Multiple(() =>
        {
            Assert.That(CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(reviewer, 42, true), Is.True);
            Assert.That(CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(reviewer, 42, false), Is.True);
            Assert.That(CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(42, 42, false), Is.True);
            Assert.That(CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(42, 43, false), Is.False);
            Assert.That(CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(42, 42, true), Is.False);
        });
    }

    [Test]
    public void GeneratedInvoiceNumbersAreStableValidAndFitTheCustomId()
    {
        var day = new DateTime(2026, 10, 10, 23, 59, 0, DateTimeKind.Utc);
        var number = CreatorPayoutDocumentService.GenerateInvoiceNumber("acct-1", day);
        Assert.Multiple(() =>
        {
            Assert.That(number, Does.Match("^SB-20261010-[0-9A-F]{6}$"));
            Assert.That(CreatorPayoutDocumentService.ValidateInvoiceNumber(number), Is.Null);
            Assert.That(CreatorPayoutDocumentService.GenerateInvoiceNumber("acct-1", day.AddHours(-3)), Is.EqualTo(number));
            Assert.That(CreatorPayoutDocumentService.GenerateInvoiceNumber("acct-2", day), Is.Not.EqualTo(number));
            Assert.That(CreatorPayoutDocumentService.GenerateInvoiceNumber("acct-1", day.AddDays(1)), Is.Not.EqualTo(number));
            var parts = CreatorPayoutMessages.EncodeModalId(5, null, null).Split(':');
            Assert.That(parts[2], Is.EqualTo("-"));
            Assert.That(CreatorPayoutMessages.EncodeModalId(ulong.MaxValue, number, long.MaxValue).Length, Is.LessThan(100));
        });
    }

    [Test]
    public void CreatorConfirmationHidesInternalDetails()
    {
        var text = CreatorPayoutMessages.CreatorConfirmation(7000, "SB-20261010-ABCDEF");
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("70.00 EUR").And.Contain("SB-20261010-ABCDEF"));
            Assert.That(text, Does.Contain("By submitting this request you accept this self-billing invoice."));
            Assert.That(text, Does.Not.Contain("IBAN").And.Not.Contain(Sha).And.Not.Contain("discord:payout-request"));
            Assert.That(CreatorPayoutMessages.CreatorConfirmation(7000, "X-1-2", false), Does.Contain("contact support"));
        });
    }

    [Test]
    public void ReviewerDmNamesTheRequesterAndTheNextStep()
    {
        var text = CreatorPayoutMessages.PayoutRequestReply(
            7000, "Jane Doe", ["IBAN GB29"], "SB-1-2", "acct-1", "discord:payout-request:SB-1-2", Sha, 0,
            requestedBy: CreatorPayoutMessages.RequestedBy(5, 5));
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("Pay **70.00 EUR**"));
            Assert.That(text, Does.Contain("IBAN GB29").And.Contain("Requested by: <@5>"));
            Assert.That(text, Does.Contain("/creator payout-complete"));
            Assert.That(CreatorPayoutMessages.RequestedBy(9, 5), Does.Contain("on behalf of <@5>"));
        });
    }

    [Test]
    public void CreatorsGetFriendlyMessages()
    {
        Assert.That(CreatorPayoutDocumentService.ValidateAmount(5000, 5000, 7000, true), Does.Contain("20.00 more is needed"));
        Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
            Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.UsSettlement), DateTime.UtcNow.AddDays(-1), true),
            Does.StartWith("Your tax document route is UsSettlement"));
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
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.UsSettlement), now), Does.Contain("UsSettlement"));
        });
    }

    [Test]
    public void ModalCustomIdRoundTrips()
    {
        var id = CreatorPayoutMessages.EncodeModalId(267680402594988033, "SB-2026-0001", 7000);
        var parts = id.Split(':');
        Assert.That(id.Length, Is.LessThan(100));
        Assert.That(CreatorPayoutMessages.TryDecodeModalId(parts[1], parts[2], parts[3], out var user, out var cents, out var invoice), Is.True);
        Assert.That(invoice, Is.EqualTo("SB-2026-0001"));
        Assert.That((user, cents), Is.EqualTo((267680402594988033UL, (long?)7000)));
        var none = CreatorPayoutMessages.EncodeModalId(5, null, null).Split(':');
        Assert.That(CreatorPayoutMessages.TryDecodeModalId(none[1], none[2], none[3], out _, out cents, out invoice), Is.True);
        Assert.That(cents, Is.Null);
        Assert.That(invoice, Is.Null, "the generate marker decodes to null");
        Assert.That(CreatorPayoutMessages.TryDecodeModalId("x", "SB-2026-0001", "-", out _, out _, out _), Is.False);
        Assert.That(CreatorPayoutMessages.TryDecodeModalId("5", "SB-2026-0001", "-5", out _, out _, out _), Is.False);
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

    private static CreatorReview Review(CreatorOnboardingStatus status, CreatorTaxDocumentRoute route, CreatorSellerType seller) =>
        Review(status, route) with { SellerType = seller };

    private static PayoutInvoiceInput StatementInput() => Input() with { Route = PayoutRoute.Statement, InvoiceNumber = "PS-20261010-ABCDEF" };

    [Test]
    public void RouteTableAllowsOnlyTheSupportedRoutesAndNamesTheRest()
    {
        var now = DateTime.UtcNow;
        Assert.Multiple(() =>
        {
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.Statement, CreatorSellerType.Individual), now), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.UkSelfBilling, CreatorSellerType.Business), now), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.CreatorInvoice), now),
                Does.Contain("CreatorInvoice").And.Contain("their own invoice"));
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.NotApplicable), now), Does.Contain("NotApplicable"));
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.UsSettlement), now), Does.Contain("UsSettlement"));
            Assert.That(CreatorPayoutDocumentService.ValidateOnboarding(
                Review(CreatorOnboardingStatus.Approved, CreatorTaxDocumentRoute.Statement, CreatorSellerType.Business), now),
                Does.Contain("inconsistent"));
        });
    }

    [Test]
    public void StatementPdfIsGeneratedWithoutReverseChargeWording()
    {
        var pdf = CreatorPayoutDocumentService.RenderPdf(StatementInput() with { CreatorVatId = "GB1" });
        Assert.That(System.Text.Encoding.ASCII.GetString(pdf, 0, 4), Is.EqualTo("%PDF"));
        Assert.That(PayoutRoute.Statement.DocumentTitle, Is.EqualTo("Payout statement (Abrechnung)"));
        Assert.That(PayoutRoute.Statement.ReverseCharge, Is.False);
        Assert.That(PayoutRoute.Statement.VatNote, Does.Not.Contain("13b"));
        Assert.That(PayoutRoute.Statement.StatementText, Is.Null);
    }

    [Test]
    public void VatIdIsRefusedOnStatementOnly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CreatorPayoutDocumentService.ValidateCreatorVatId(PayoutRoute.Statement, "GB123"), Does.Contain("different tax route"));
            Assert.That(CreatorPayoutDocumentService.ValidateCreatorVatId(PayoutRoute.Statement, " "), Is.Null);
            Assert.That(CreatorPayoutDocumentService.ValidateCreatorVatId(PayoutRoute.UkSelfBilling, "GB123"), Is.Null);
        });
    }

    [Test]
    public void StatementEvidenceAndBookingUseTheStatementRoute()
    {
        var number = "PS-20261010-ABCDEF";
        var pdf = CreatorPayoutDocumentService.RenderPdf(StatementInput());
        var sha = CreatorPayoutDocumentService.Sha256Hex(pdf);
        using var evidence = JsonDocument.Parse(CreatorPayoutDocumentService.BuildEvidenceJson(
            7000, 0, 0, 0, CreatorPayoutDocumentService.EvidenceReference(number, PayoutRoute.Statement), sha));
        using var booking = JsonDocument.Parse(CreatorPayoutDocumentService.BuildBookingJson(
            number, new DateOnly(2026, 10, 10), 7000, "acct-1", "GB", sha, PayoutRoute.Statement));
        Assert.Multiple(() =>
        {
            Assert.That(evidence.RootElement.GetProperty("payoutEvidenceReference").GetString(), Is.EqualTo("statement:" + number));
            Assert.That(evidence.RootElement.GetProperty("netEurCents").GetInt64(), Is.EqualTo(7000));
            Assert.That(booking.RootElement.GetProperty("taxDocumentRoute").GetString(), Is.EqualTo("Statement"));
            Assert.That(booking.RootElement.GetProperty("reverseCharge").GetBoolean(), Is.False);
            Assert.That(booking.RootElement.GetProperty("evidence").GetProperty("pdfSha256").GetString(), Is.EqualTo(sha));
            Assert.That(booking.RootElement.GetProperty("ledgerReference").GetString(), Is.EqualTo("discord:payout-request:" + number));
        });
    }

    [Test]
    public void GeneratedPrefixFollowsTheRoute()
    {
        var day = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var statement = CreatorPayoutDocumentService.GenerateInvoiceNumber("acct-1", day, PayoutRoute.Statement);
        Assert.Multiple(() =>
        {
            Assert.That(statement, Does.Match("^PS-20261010-[0-9A-F]{6}$"));
            Assert.That(CreatorPayoutDocumentService.GenerateInvoiceNumber("acct-1", day, PayoutRoute.UkSelfBilling), Does.StartWith("SB-"));
            Assert.That(CreatorPayoutDocumentService.ValidateInvoiceNumber(statement), Is.Null);
        });
    }

    [Test]
    public void ConfirmationAndReviewerTextNameTheDocumentType()
    {
        var statement = CreatorPayoutMessages.CreatorConfirmation(7000, "PS-1-2", true, PayoutRoute.Statement);
        var selfBilling = CreatorPayoutMessages.CreatorConfirmation(7000, "SB-1-2");
        var reviewer = CreatorPayoutMessages.PayoutRequestReply(
            7000, "Jane", ["IBAN"], "PS-1-2", "a", "discord:payout-request:PS-1-2", Sha, 0, route: PayoutRoute.Statement);
        Assert.Multiple(() =>
        {
            Assert.That(statement, Does.Contain("Your payout statement is attached.")
                .And.Contain("By submitting this request you confirm this payout statement."));
            Assert.That(statement, Does.Not.Contain("self-billing"));
            Assert.That(selfBilling, Does.Contain("By submitting this request you accept this self-billing invoice."));
            Assert.That(reviewer, Does.Contain("payout statement (route Statement)"));
        });
    }
}
