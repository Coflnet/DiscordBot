using Coflnet.DiscordBot.Phone;
using Discord;
using Discord.Interactions;

public sealed class PhoneCommands(
    DiscordCallHandoff handoff,
    TwilioCallGate callGate,
    ILogger<PhoneCommands> logger) : InteractionModuleBase
{
    [SlashCommand("test-phone-call", "Test the phone waiting-room handoff without placing a call")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task TestPhoneCall(
        [Summary("german", "Use the German disclosure recording")] bool german = false)
    {
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
