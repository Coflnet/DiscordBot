using Coflnet.DiscordBot.Phone;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

public sealed class PhoneCommands(
    DiscordCallHandoff handoff,
    TwilioCallGate callGate,
    TwilioVoicemailService voicemail,
    IOptions<TwilioVoiceOptions> options,
    ILogger<PhoneCommands> logger) : InteractionModuleBase
{
    private readonly TwilioVoiceOptions options = options.Value;

    [SlashCommand("missed-phone-calls", "Show recent calls that could not be connected")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task MissedPhoneCalls(
        [Summary("clear", "Clear the stored missed-call history")] bool clear = false)
    {
        if (Context.User.Id != options.TargetUserId)
        {
            await RespondAsync("You are not allowed to use this command.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        try
        {
            if (clear)
            {
                await callGate.ClearMissedCallsAsync();
                await FollowupAsync("Missed phone-call history cleared.", ephemeral: true);
                return;
            }

            var calls = await callGate.GetMissedCallsAsync();
            if (calls.Count == 0)
            {
                await FollowupAsync("No missed phone calls were recorded in the last 30 days.", ephemeral: true);
                return;
            }

            var lines = calls.Select(call =>
                $"<t:{call.Timestamp.ToUnixTimeSeconds()}:R> — caller `{call.CallerReference}` — "
                + (call.Reason == MissedCallReason.TargetUnavailable
                    ? "target unavailable"
                    : "another call was in progress"));
            await FollowupAsync(
                "Recent missed phone calls (newest first):\n" + string.Join('\n', lines),
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not retrieve missed phone calls");
            await FollowupAsync("Could not retrieve missed phone calls. Check the bot logs.", ephemeral: true);
        }
    }

    [SlashCommand("phone-voicemails", "Listen to or delete recent phone voicemails")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task PhoneVoicemails()
    {
        if (!await RequireOwnerAsync())
            return;

        await DeferAsync(ephemeral: true);
        try
        {
            var recordings = await voicemail.GetAsync();
            if (recordings.Count == 0)
            {
                await FollowupAsync("No phone voicemails are available.", ephemeral: true);
                return;
            }

            await FollowupAsync(
                "Select a voicemail, then use **Play** or **Delete**.",
                components: BuildVoicemailMenu(recordings),
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not retrieve phone voicemails");
            await FollowupAsync("Could not retrieve phone voicemails. Check the bot logs.", ephemeral: true);
        }
    }

    [ComponentInteraction("phone-voicemail-select")]
    public async Task SelectPhoneVoicemail()
    {
        if (!await RequireOwnerAsync())
            return;

        var component = (SocketMessageComponent)Context.Interaction;
        var recordingSid = component.Data.Values.FirstOrDefault();
        var selected = recordingSid is null ? null : await voicemail.GetAsync(recordingSid);
        var recordings = await voicemail.GetAsync();
        if (selected is null)
        {
            await component.UpdateAsync(message =>
            {
                message.Content = "That voicemail is no longer available.";
                message.Components = recordings.Count == 0
                    ? new ComponentBuilder().Build()
                    : BuildVoicemailMenu(recordings);
            });
            return;
        }

        await component.UpdateAsync(message =>
        {
            message.Content = VoicemailDescription(selected);
            message.Components = BuildVoicemailMenu(recordings, selected.RecordingSid);
        });
    }

    [ComponentInteraction("phone-voicemail-play:*")]
    public async Task PlayPhoneVoicemail(string recordingSid)
    {
        if (!await RequireOwnerAsync())
            return;

        var component = (SocketMessageComponent)Context.Interaction;
        await component.DeferAsync(ephemeral: true);
        try
        {
            var selected = await voicemail.GetAsync(recordingSid);
            var audio = await voicemail.DownloadAsync(recordingSid, CancellationToken.None);
            if (selected is null || audio is null)
            {
                await FollowupAsync("That voicemail is no longer available.", ephemeral: true);
                return;
            }

            using var stream = new MemoryStream(audio);
            var delete = new ComponentBuilder()
                .WithButton("Delete recording", $"phone-voicemail-delete:{recordingSid}", ButtonStyle.Danger)
                .Build();
            await FollowupWithFileAsync(
                stream,
                $"voicemail-{selected.Timestamp:yyyyMMdd-HHmmss}.wav",
                VoicemailDescription(selected),
                ephemeral: true,
                components: delete);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not download a Twilio voicemail");
            await FollowupAsync("Could not download that voicemail. Check the bot logs.", ephemeral: true);
        }
    }

    [ComponentInteraction("phone-voicemail-delete:*")]
    public async Task DeletePhoneVoicemail(string recordingSid)
    {
        if (!await RequireOwnerAsync())
            return;

        var component = (SocketMessageComponent)Context.Interaction;
        await component.DeferAsync(ephemeral: true);
        try
        {
            var deleted = await voicemail.DeleteAsync(recordingSid, CancellationToken.None);
            await component.ModifyOriginalResponseAsync(message =>
            {
                message.Content = deleted
                    ? "Voicemail deleted from Twilio."
                    : "That voicemail was already deleted.";
                message.Attachments = Array.Empty<FileAttachment>();
                message.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not delete a Twilio voicemail");
            await FollowupAsync("Could not delete that voicemail. Check the bot logs.", ephemeral: true);
        }
    }

    [SlashCommand("test-phone-call", "Test the phone waiting-room handoff without placing a call")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task TestPhoneCall(
        [Summary("german", "Use the German disclosure recording")] bool german = false)
    {
        if (Context.User.Id != options.TargetUserId)
        {
            await RespondAsync("You are not allowed to use this command.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var reservation = $"test:{Context.Interaction.Id}";
        if (!await callGate.TryReserveAsync(reservation))
        {
            await FollowupAsync("A real or test phone call is already in progress.", ephemeral: true);
            return;
        }

        DiscordVoiceSession? session = null;
        try
        {
            var language = german ? PhoneLanguage.German : PhoneLanguage.English;
            session = await handoff.PrepareAsync(language, CancellationToken.None);
            if (session is null)
            {
                await FollowupAsync(
                    "The target left, the private channel became busy, or two seconds of microphone audio were not received within 15 seconds.",
                    ephemeral: true);
                return;
            }

            await FollowupAsync(
                "Disclosure acknowledged and the private-channel move succeeded. The test will reset in five seconds.",
                ephemeral: true);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Phone handoff test failed");
            await FollowupAsync($"Phone handoff test failed: {exception.Message}", ephemeral: true);
        }
        finally
        {
            try
            {
                if (session is not null)
                    await handoff.EndAsync(session);
            }
            finally
            {
                await callGate.ReleaseAsync(reservation);
            }
        }
    }

    private async Task<bool> RequireOwnerAsync()
    {
        if (Context.User.Id == options.TargetUserId)
            return true;
        await RespondAsync("You are not allowed to use this command.", ephemeral: true);
        return false;
    }

    private static MessageComponent BuildVoicemailMenu(
        IReadOnlyList<PhoneVoicemail> recordings,
        string? selectedRecordingSid = null)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("phone-voicemail-select")
            .WithPlaceholder("Choose a voicemail")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var recording in recordings)
        {
            menu.AddOption(
                $"{recording.Timestamp:yyyy-MM-dd HH:mm} UTC · {recording.DurationSeconds}s",
                recording.RecordingSid,
                $"Caller {recording.CallerReference} · {ReasonDescription(recording.Reason)}",
                isDefault: recording.RecordingSid == selectedRecordingSid);
        }

        var components = new ComponentBuilder().WithSelectMenu(menu, row: 0);
        if (selectedRecordingSid is not null)
        {
            components
                .WithButton("Play", $"phone-voicemail-play:{selectedRecordingSid}", ButtonStyle.Primary, row: 1)
                .WithButton("Delete", $"phone-voicemail-delete:{selectedRecordingSid}", ButtonStyle.Danger, row: 1);
        }
        return components.Build();
    }

    private static string VoicemailDescription(PhoneVoicemail voicemail)
        => $"Voicemail from caller `{voicemail.CallerReference}` · "
            + $"<t:{voicemail.Timestamp.ToUnixTimeSeconds()}:R> · {voicemail.DurationSeconds}s · "
            + ReasonDescription(voicemail.Reason);

    private static string ReasonDescription(MissedCallReason reason)
        => reason == MissedCallReason.TargetUnavailable
            ? "target unavailable"
            : "another call was in progress";
}
