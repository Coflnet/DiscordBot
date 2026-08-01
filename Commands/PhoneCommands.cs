using Coflnet.DiscordBot.Phone;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;

public sealed class PhoneCommands(
    DiscordCallHandoff handoff,
    TwilioCallGate callGate,
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
                await FollowupAsync("The configured target user is not in the phone waiting room.", ephemeral: true);
                return;
            }

            await FollowupAsync(
                "Disclosure played and the private-channel move succeeded. The test will reset in five seconds.",
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
}
