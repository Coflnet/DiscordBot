using Discord;
using Discord.Interactions;
using Coflnet.Payments.Client.Api;

namespace Coflnet.Discord;

public class PurchaseCountCommand : InteractionModuleBase
{
    private readonly IProductsApi productsApi;
    private readonly ILogger<PurchaseCountCommand> logger;

    // Only this Discord user id may execute the command
    private  ulong[] AllowedUserIds = [914907821676568647UL, 267680402594988033L];

    public PurchaseCountCommand(IProductsApi productsApi, ILogger<PurchaseCountCommand> logger)
    {
        this.productsApi = productsApi;
        this.logger = logger;
    }

    [SlashCommand("purchase-count", "Report the number of purchases/usages for a given product/service (admin only)")]
    [IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.PrivateChannel, InteractionContextType.BotDm)]
    public async Task PurchaseCount()
    {
        await DeferAsync(ephemeral: true);

        try
        {
            if (!AllowedUserIds.Contains(Context.User.Id))
            {
                await FollowupAsync("❌ You are not allowed to run this command.", ephemeral: true);
                return;
            }

            var productSlug = "rust-addon";

            long count;
            try
            {
                count = await productsApi.ProductsServiceServiceSlugCountGetAsync(productSlug);
            }
            catch (Payments.Client.Client.ApiException e)
            {
                logger.LogWarning(e, "Could not retrieve count for product {productSlug}", productSlug);
                await FollowupAsync($"❌ Could not retrieve count for `{productSlug}`: {e.Message}", ephemeral: true);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle($"📊 Purchase Count for {productSlug}")
                .WithDescription($"Current count: **{count}**")
                .WithColor(Color.Blue)
                .WithCurrentTimestamp()
                .Build();

            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in purchase-count command for user {user}", Context.User.Id);
            await FollowupAsync("❌ An unexpected error occurred. Check logs for details.", ephemeral: true);
        }
    }
}
