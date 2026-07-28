using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coflnet.Discord;

/// <summary>
/// Matches user messages against predefined FAQ entries using keyword + blacklist logic,
/// ported from the Node.js bot's answer.json system.
/// </summary>
public class FaqService
{
    private const int MaxLoggedMessageLength = 100;
    private static readonly Regex DiscordUserMention = new(@"<@!?\d+>", RegexOptions.Compiled);
    private readonly ILogger<FaqService> logger;
    private List<FaqEntry> entries = new();

    public FaqService(ILogger<FaqService> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Load FAQ entries from a JSON file path.
    /// </summary>
    public void Load(string jsonPath)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            entries = JsonSerializer.Deserialize<List<FaqEntry>>(json) ?? new();
            logger.LogInformation("Loaded {count} FAQ entries from {path}", entries.Count, jsonPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load FAQ from {path}", jsonPath);
        }
    }

    /// <summary>
    /// Returns the first matching FAQ answer, or null if no match.
    /// Matches the original Node.js logic: message must be ≤ 60 chars,
    /// all question keywords must be present, and no blacklist word must be present.
    /// </summary>
    public string? GetResponse(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        // Original logic skips messages longer than 60 chars
        if (message.Length > 60)
            return null;

        var lower = message.ToLowerInvariant();

        foreach (var entry in entries)
        {
            // Check blacklist first: if any blacklisted word is present, skip this entry
            if (entry.Blacklist != null && entry.Blacklist.Any(b => lower.Contains(b)))
                continue;

            // All question keywords must be present
            if (entry.Question.All(q => lower.Contains(q)))
            {
                logger.LogInformation("FAQ matched keywords {keywords} for message '{message}'",
                    string.Join(", ", entry.Question), CreateLogSample(message));
                return entry.Answer;
            }
        }

        return null;
    }

    internal static string CreateLogSample(string message)
    {
        var sanitized = DiscordUserMention.Replace(message, "@user").ReplaceLineEndings(" ");
        return sanitized[..Math.Min(sanitized.Length, MaxLoggedMessageLength)];
    }
}

public class FaqEntry
{
    public List<string> Question { get; set; } = new();
    public List<string>? Blacklist { get; set; }
    public string Answer { get; set; } = string.Empty;
}
