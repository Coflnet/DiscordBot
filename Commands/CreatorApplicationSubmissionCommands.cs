using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Coflnet.Discord;

public sealed class CreatorApplicationSubmissionCommands(
    ILogger<CreatorApplicationSubmissionCommands> logger) : InteractionModuleBase
{
    [ComponentInteraction("creator-submit:*")]
    public async Task Submit(string applicantIdText)
    {
        if (!CreatorReviewCommands.TryUserId(applicantIdText, out var applicantId)
            || applicantId != Context.User.Id
            || Context.Channel is not IDMChannel
            || Context.Interaction is not SocketMessageComponent component
            || component.Message.Author.Id != Context.Client.CurrentUser.Id)
        {
            await RespondAsync("Only the applicant can submit this application in their bot DM.", ephemeral: true);
            return;
        }

        var application = component.Message.Embeds.SingleOrDefault()?.Description;
        if (string.IsNullOrWhiteSpace(application) || application.Length > 3500)
        {
            await RespondAsync("This application preview is invalid. Run `/creator apply` again.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        try
        {
            var reviewer = await Context.Client.GetUserAsync(CreatorReviewCommands.ReviewerId)
                ?? throw new InvalidOperationException("Creator reviewer is unavailable.");
            await reviewer.SendMessageAsync(
                $"Creator application from {Context.User.Mention} (Discord ID `{applicantId}`).\n"
                + "Review it here with `/creator review`: use the applicant ID and the application text below "
                + "as `application`, then fill in the reviewed residence and capacity.\n"
                + $"Privacy notice shown: https://coflnet.com/privacy ({CreatorReviewCommands.PrivacyNoticeVersion}).",
                embed: new EmbedBuilder().WithTitle("Expert Config creator application")
                    .WithDescription(application)
                    .WithFooter($"Submission {component.Message.Id}")
                    .Build(),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not deliver creator application from {applicantId}", applicantId);
            await FollowupAsync("Your application could not be delivered. Please try Submit again later.", ephemeral: true);
            return;
        }

        // Delivery succeeded: failure to update the preview must not be reported as delivery failure.
        try
        {
            await component.Message.ModifyAsync(message =>
            {
                message.Content = "Application submitted. You will receive the review outcome in this DM.";
                message.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Application delivered, but preview {messageId} could not be updated", component.Message.Id);
        }
        await FollowupAsync("Your application was sent for review. You will receive the outcome here.", ephemeral: true);
    }
}
