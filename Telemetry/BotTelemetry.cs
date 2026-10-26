using System.Diagnostics;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using OpenTelemetry.Trace;

namespace Coflnet.DiscordBot.Telemetry;

/// <summary>Plain description of an incoming interaction, decoupled from Discord.Net socket types.</summary>
public sealed record InteractionInfo(
    ulong InteractionId,
    string Kind,
    string Name,
    ulong UserId,
    ulong? GuildId,
    ulong? ChannelId,
    IReadOnlyList<KeyValuePair<string, string?>> Options);

/// <summary>Outcome of an interaction as shown on the span and in the log.</summary>
public sealed record InteractionOutcome(bool Success, string Status, string? Reason);

/// <summary>
/// ActivitySource and helpers for tracing Discord interactions. The source has to be registered
/// with the tracer provider (see Program.cs) or no span created here is ever exported.
/// </summary>
public static class BotTelemetry
{
    public const string SourceName = "Coflnet.DiscordBot";
    public const string SpanPrefix = "discord.";
    public const int MaxValueLength = 200;

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly string[] SensitiveFragments =
        ["token", "secret", "password", "passwd", "apikey", "api_key", "api-key", "authorization", "credential", "iban", "private"];

    public static bool IsSensitive(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower == "key" || lower.EndsWith("key") || SensitiveFragments.Any(lower.Contains);
    }

    public static string FormatValue(string? value)
    {
        if (value == null)
            return "";
        return value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "...";
    }

    public static string SpanName(InteractionInfo info) => $"{SpanPrefix}{info.Kind} {info.Name}";

    public static List<KeyValuePair<string, object?>> BuildTags(InteractionInfo info)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("discord.interaction.id", info.InteractionId.ToString()),
            new("discord.interaction.kind", info.Kind),
            new("discord.command.name", info.Name),
            new("discord.user.id", info.UserId.ToString()),
        };
        if (info.GuildId != null)
            tags.Add(new("discord.guild.id", info.GuildId.Value.ToString()));
        if (info.ChannelId != null)
            tags.Add(new("discord.channel.id", info.ChannelId.Value.ToString()));
        foreach (var (name, value) in info.Options)
            tags.Add(new($"discord.option.{name}", IsSensitive(name) ? "[redacted]" : FormatValue(value)));
        return tags;
    }

    public static InteractionOutcome Describe(global::Discord.Interactions.IResult result)
    {
        if (result.IsSuccess)
            return new InteractionOutcome(true, "ok", null);
        var status = result.Error?.ToString() ?? "Error";
        var reason = result.ErrorReason;
        if (result is ExecuteResult { Exception: { } ex })
            reason = string.IsNullOrEmpty(reason) ? ex.Message : reason;
        return new InteractionOutcome(false, status, reason);
    }

    public static void RecordException(this Activity? activity, Exception ex)
    {
        if (activity == null)
            return;
        activity.AddException(ex);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
    }

    public static Activity? StartInteraction(InteractionInfo info)
    {
        var activity = Source.StartActivity(SpanName(info), ActivityKind.Server);
        if (activity == null)
            return null;
        foreach (var tag in BuildTags(info))
            activity.SetTag(tag.Key, tag.Value);
        return activity;
    }

    public static void Complete(Activity? activity, InteractionOutcome outcome, Exception? exception = null)
    {
        if (activity == null)
            return;
        activity.SetTag("discord.result.success", outcome.Success);
        activity.SetTag("discord.result.status", outcome.Status);
        if (outcome.Reason != null)
            activity.SetTag("discord.result.reason", FormatValue(outcome.Reason));
        if (exception != null)
            activity.AddException(exception);
        if (!outcome.Success)
            activity.SetStatus(ActivityStatusCode.Error, outcome.Reason ?? outcome.Status);
        activity.Dispose();
    }

    public static InteractionInfo Describe(SocketInteraction interaction)
    {
        var options = new List<KeyValuePair<string, string?>>();
        string kind, name;
        switch (interaction)
        {
            case SocketSlashCommand slash:
                kind = "slash";
                name = slash.CommandName;
                Flatten(slash.Data.Options, "", options);
                break;
            case SocketMessageCommand message:
                kind = "message-command";
                name = message.CommandName;
                options.Add(new("message_id", message.Data.Message?.Id.ToString()));
                options.Add(new("message_author_id", message.Data.Message?.Author?.Id.ToString()));
                break;
            case SocketUserCommand user:
                kind = "user-command";
                name = user.CommandName;
                options.Add(new("target_user_id", user.Data.Member?.Id.ToString()));
                break;
            case SocketMessageComponent component:
                kind = "component";
                name = component.Data.CustomId;
                options.Add(new("values", string.Join(",", component.Data.Values ?? [])));
                break;
            case SocketModal modal:
                kind = "modal";
                name = modal.Data.CustomId;
                // only the field names and sizes, the content of free text is not needed to debug
                foreach (var field in modal.Data.Components)
                    options.Add(new(field.CustomId, $"[{field.Value?.Length ?? 0} chars]"));
                break;
            case SocketAutocompleteInteraction auto:
                kind = "autocomplete";
                name = auto.Data.CommandName;
                break;
            default:
                kind = interaction.Type.ToString().ToLowerInvariant();
                name = kind;
                break;
        }
        return new InteractionInfo(interaction.Id, kind, name, interaction.User.Id, interaction.GuildId, interaction.ChannelId, options);
    }

    private static void Flatten(IEnumerable<SocketSlashCommandDataOption> source, string prefix, List<KeyValuePair<string, string?>> target)
    {
        foreach (var option in source)
        {
            var key = prefix + option.Name;
            if (option.Options is { Count: > 0 })
                Flatten(option.Options, key + ".", target);
            else
                target.Add(new(key, option.Value?.ToString()));
        }
    }
}
