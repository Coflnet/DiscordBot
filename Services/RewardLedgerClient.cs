using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Coflnet.Discord;

public enum RewardGrantSource
{
    Report = 1,
    Referral = 2
}

public sealed class RewardLedgerClient(
    HttpClient http,
    IConfiguration configuration)
{
    public async Task<RewardGrantResult> Record(
        RewardGrantRequest reward,
        CancellationToken cancellationToken = default)
    {
        var (baseUri, token) = Credentials("WRITE_TOKEN");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, "/api/rewards/entries"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", token);
        request.Content = JsonContent.Create(reward);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RewardGrantResult>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Reward ledger returned an empty response.");
    }

    public async Task<RewardBalanceInfo> GetBalance(
        string rewardAccountId,
        CancellationToken cancellationToken = default)
    {
        var (baseUri, token) = Credentials("WRITE_TOKEN");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUri,
                $"/api/rewards/accounts/{Uri.EscapeDataString(rewardAccountId)}/balance"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RewardBalanceInfo>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Reward ledger returned an empty response.");
    }

    public async Task<IReadOnlyList<RewardLedgerEntryInfo>> GetLedger(
        string rewardAccountId,
        CancellationToken cancellationToken = default)
    {
        var (baseUri, token) = Credentials("WRITE_TOKEN");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUri,
                $"/api/rewards/accounts/{Uri.EscapeDataString(rewardAccountId)}/ledger"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RewardLedgerEntryInfo>>(
            cancellationToken: cancellationToken)
            ?? [];
    }

    private (Uri BaseUri, string Token) Credentials(string tokenKey)
    {
        var token = configuration[$"REWARDS:{tokenKey}"];
        if (!Uri.TryCreate(configuration["REFERRAL_BASE_URL"], UriKind.Absolute,
                out var baseUri)
            || token?.Length < 32)
            throw new InvalidOperationException(
                "Reward ledger write access is not configured.");
        return (baseUri, token!);
    }
}

public record RewardGrantRequest(
    string Reference,
    string RewardAccountId,
    int Kind,
    RewardGrantSource? Source,
    long RemunerationEurCents,
    string? OfferVersion,
    string Reason,
    string? DetailsJson,
    Guid? RelatedEntryId = null);

public record RewardGrantResult(RewardGrantEntry Entry, bool Created);

public record RewardGrantEntry(Guid Id);

public record RewardBalanceInfo(
    string RewardAccountId,
    string Currency,
    long PendingEurCents,
    long OutstandingEurCents,
    long ReservedEurCents,
    long AvailableEurCents,
    long PayoutThresholdEurCents);

public record RewardLedgerEntryInfo(
    Guid Id,
    string Reference,
    int Kind,
    long RemunerationEurCents,
    string Reason,
    DateTime CreatedAt);
