using Discord;
using Discord.Interactions;
using Coflnet.Payments.Client.Api;
using Coflnet.Payments.Client.Model;
using Discord.WebSocket;

namespace Coflnet.Discord;

public class PurchaseCommand : InteractionModuleBase
{
    private readonly IUserApi userApi;
    private readonly IProductsApi productsApi;
    private readonly ILogger<PurchaseCommand> logger;
    private readonly Persistence persistence;

    public PurchaseCommand(
        Coflnet.Payments.Client.Api.IUserApi userApi,
        Coflnet.Payments.Client.Api.IProductsApi productsApi,
        ILogger<PurchaseCommand> logger,
        Persistence persistence)
    {
        this.userApi = userApi;
        this.productsApi = productsApi;
        this.logger = logger;
        this.persistence = persistence;
    }

    [SlashCommand("purchase", "Purchase premium services (premium, premium_plus, or pre_api)")]
    public async Task Purchase(
        [Summary("product", "The product to purchase (premium, premium_plus, or pre_api)")]
        [Choice("Premium (Monthly)", "premium")]
        [Choice("Premium+ (Weekly)", "premium_plus")]
        [Choice("Premium+ (4 Weeks - 18% cheaper!)", "premium_plus-weeks")]
        [Choice("Pre API Access", "pre_api")]
        [Choice("Starter Premium (180 Days)", "starter_premium")]
        string productSlug,
        [Summary("quantity", "Number of units to purchase (1-12)")] int count = 1)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Validate count
            if (count < 1 || count > 12)
            {
                await FollowupAsync("❌ The quantity must be between 1 and 12", ephemeral: true);
                return;
            }

            // Normalize product slug
            if (productSlug == "prem+" || productSlug == "premium+")
                productSlug = "premium_plus";

            // Prevent too many users from purchasing pre_api (max 2 users)
            if (productSlug == "pre_api")
            {
                try
                {
                    var currentCount = await productsApi.ProductsServiceServiceSlugCountGetAsync("pre_api");
                    if (currentCount >= 2)
                    {
                        await FollowupAsync($"❌ Pre API access is limited to at most 2 people. Its already used by 2 users, please check again in 20 minutes.", ephemeral: true);
                        return;
                    }
                }
                catch (Payments.Client.Client.ApiException e)
                {
                    logger.LogWarning(e, "Could not retrieve pre_api usage count");
                    await FollowupAsync("❌ Could not check Pre API availability. Please try again later.", ephemeral: true);
                    return;
                }
            }

            // Get Discord account info to find user ID
            var discordInfo = await persistence.GetDiscordAccountInfo(Context.User.Id);
            if (discordInfo?.MinecraftName == null)
            {
                await FollowupAsync(
                    "❌ **You need to link your Minecraft account first!**\n\n" +
                    "Use `/update-mc-user` to link your account.",
                    ephemeral: true);
                return;
            }

            var userId = discordInfo.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                await FollowupAsync(
                    "❌ **Could not find your user ID**\n\n" +
                    "Please contact support on Discord.",
                    ephemeral: true);
                return;
            }

            // Get product details with pricing
            RuleResult adjustedProduct;
            try
            {
                adjustedProduct = await userApi.UserUserIdPriceForProductSlugGetAsync(userId, productSlug);
            }
            catch (Payments.Client.Client.ApiException e)
            {
                var errorMessage = e.Message.Contains("{\"Message\":\"") 
                    ? e.Message.Substring("Error calling UserUserIdPriceForProductSlugGet: {\"Message\":\"".Length).TrimEnd('"', '}')
                    : "Product not found";
                await FollowupAsync($"❌ **Error:** {errorMessage}", ephemeral: true);
                return;
            }

            var product = adjustedProduct.ModifiedProduct;
            if (product == null)
            {
                await FollowupAsync($"❌ The product `{productSlug}` could not be found", ephemeral: true);
                return;
            }

            // Calculate totals
            var totalCost = (long)(product.Cost * count);
            var timeSpan = TimeSpan.FromSeconds(product.OwnershipSeconds * count);
            var duration = timeSpan < TimeSpan.FromDays(1) 
                ? $"{(int)timeSpan.TotalHours} hour{(timeSpan.TotalHours == 1 ? "" : "s")}"
                : $"{(int)timeSpan.TotalDays} day{(timeSpan.TotalDays == 1 ? "" : "s")}";

            // Get user balance
            var userInfo = await userApi.UserUserIdGetAsync(userId);
            var balance = userInfo.Balance;

            // Create confirmation embed
            var embedBuilder = new EmbedBuilder()
                .WithTitle($"💎 Purchase {product.Title}")
                .WithDescription($"**{product.Description}**")
                .WithColor(totalCost <= balance ? Color.Gold : Color.Red)
                .AddField("📦 Quantity", $"{count}x", inline: true)
                .AddField("⏰ Duration", duration, inline: true)
                .AddField("💰 Total Cost", $"{totalCost:N0} CoflCoins", inline: true)
                .AddField("💳 Your Balance", $"{balance:N0} CoflCoins", inline: true)
                .WithFooter($"Product: {productSlug}")
                .WithCurrentTimestamp();

            // Check if user has enough balance
            if (totalCost > balance)
            {
                embedBuilder.AddField("⚠️ Insufficient Balance", 
                    $"You need **{(totalCost - balance):N0}** more CoflCoins.\n" +
                    "Use `/topup` to purchase more CoflCoins.");
                await FollowupAsync(embed: embedBuilder.Build(), ephemeral: true);
                return;
            }

            // Add discount info if applicable (if rules exist)
            if (adjustedProduct.Rules != null && adjustedProduct.Rules.Count > 0)
            {
                embedBuilder.AddField("🎉 Special Pricing", 
                    "A discount or special pricing has been applied to your purchase!");
            }

            // Create confirmation component
            var components = new ComponentBuilder()
                .WithButton("✅ Confirm Purchase", $"purchase_confirm:{productSlug}:{count}:{Context.User.Id}", ButtonStyle.Success)
                .WithButton("❌ Cancel", $"purchase_cancel:{Context.User.Id}", ButtonStyle.Danger)
                .Build();

            await FollowupAsync(
                "**Do you want to proceed with this purchase?**", 
                embed: embedBuilder.Build(), 
                components: components, 
                ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in purchase command for user {userId}", Context.User.Id);
            await FollowupAsync(
                "❌ **An error occurred while processing your purchase.**\n" +
                "Please try again or contact support if the issue persists.", 
                ephemeral: true);
        }
    }

    [ComponentInteraction("purchase_confirm:*:*:*")]
    public async Task ConfirmPurchase(string productSlug, string countStr, string userIdStr)
    {
        var component = (SocketMessageComponent)Context.Interaction;

        // Verify the user clicking is the same as who initiated
        if (Context.User.Id.ToString() != userIdStr)
        {
            await component.RespondAsync("❌ You cannot confirm someone else's purchase!", ephemeral: true);
            return;
        }

        // Acknowledge quickly and remove buttons from the original message to prevent double clicks
        await component.DeferAsync(ephemeral: true);
        try
        {
            try
            {
                await component.Message.ModifyAsync(msg =>
                {
                    msg.Content = "Processing your purchase...";
                    msg.Components = new ComponentBuilder().Build();
                });
            }
            catch { /* non-fatal if message modify fails */ }

            var count = int.Parse(countStr);
            var discordInfo = await persistence.GetDiscordAccountInfo(Context.User.Id);
            var userId = discordInfo?.UserId;

            if (string.IsNullOrEmpty(userId))
            {
                await FollowupAsync("❌ User ID not found. Please try the command again.", ephemeral: true);
                return;
            }

            // Execute the purchase (may take time)
            // Double-check pre_api availability to avoid race conditions (max 2 users)
            if (productSlug == "pre_api")
            {
                try
                {
                    var currentCount = await productsApi.ProductsServiceServiceSlugCountGetAsync("pre_api");
                    if (currentCount >= 2)
                    {
                        var msg = $"❌ Pre API access is limited to at most 2 people. Its already full. Please try again in half an hour. Purchase cancelled.";
                        await FollowupAsync(msg, ephemeral: true);
                        try
                        {
                            await component.Message.ModifyAsync(m =>
                            {
                                m.Content = msg;
                                m.Embed = null;
                                m.Components = new ComponentBuilder().Build();
                            });
                        }
                        catch { }
                        return;
                    }
                }
                catch (Payments.Client.Client.ApiException e)
                {
                    logger.LogWarning(e, "Could not retrieve pre_api usage count at confirm step");
                    await FollowupAsync("❌ Could not check Pre API availability. Please try again later.", ephemeral: true);
                    return;
                }
            }

            var reference = $"discord_{Context.User.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            await userApi.UserUserIdServicePurchaseProductSlugPostAsync(userId, productSlug, reference, count);

            // Get updated product info for success message
            var adjustedProduct = await userApi.UserUserIdPriceForProductSlugGetAsync(userId, productSlug);
            var product = adjustedProduct.ModifiedProduct;
            var totalCost = (long)(product.Cost * count);
            var timeSpan = TimeSpan.FromSeconds(product.OwnershipSeconds * count);
            var duration = timeSpan < TimeSpan.FromDays(1)
                ? $"{(int)timeSpan.TotalHours} hour{(timeSpan.TotalHours == 1 ? "" : "s")}" 
                : $"{(int)timeSpan.TotalDays} day{(timeSpan.TotalDays == 1 ? "" : "s")}";

            var successEmbed = new EmbedBuilder()
                .WithTitle("✅ Purchase Successful!")
                .WithDescription($"**{product.Title}** has been activated!")
                .WithColor(Color.Green)
                .AddField("📦 Purchased", $"{count}x {product.Title}", inline: true)
                .AddField("⏰ Duration", duration, inline: true)
                .AddField("💰 Cost", $"{totalCost:N0} CoflCoins", inline: true)
                .WithFooter("Thank you for supporting us! 💚")
                .WithCurrentTimestamp()
                .Build();

            // Send result as a followup message (ephemeral)
            await FollowupAsync(embed: successEmbed, ephemeral: true);

            // Try to update original message to show completion (best-effort)
            try
            {
                await component.Message.ModifyAsync(msg =>
                {
                    msg.Content = "✅ **Purchase completed!**";
                    msg.Embed = successEmbed;
                    msg.Components = new ComponentBuilder().Build();
                });
            }
            catch { }

            logger.LogInformation("User {discordId} ({userId}) purchased {count}x {product} for {cost} CoflCoins", 
                Context.User.Id, userId, count, productSlug, totalCost);
        }
        catch (Payments.Client.Client.ApiException e)
        {
            var errorMessage = "An error occurred";
            if (e.Message.Contains("insuficcient balance"))
            {
                errorMessage = "❌ **Insufficient balance!**\n\nUse `/topup` to purchase more CoflCoins.";
            }
            else if (e.Message.Contains("same reference found"))
            {
                errorMessage = "⏰ **Please wait a minute before purchasing again.**\n\nThis prevents accidental duplicate purchases.";
            }
            else if (e.Message.Length > 68)
            {
                var msg = e.Message.Substring(68).Trim('}', '"');
                errorMessage = $"❌ **Error:** {msg}";
            }

            // Inform the user via followup
            await FollowupAsync(errorMessage, ephemeral: true);

            // Try to update original message to show error (best-effort)
            try
            {
                await component.Message.ModifyAsync(msg =>
                {
                    msg.Content = errorMessage;
                    msg.Embed = null;
                    msg.Components = new ComponentBuilder().Build();
                });
            }
            catch { }

            logger.LogError(e, "Purchase error for user {userId}: {error}", Context.User.Id, e.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error confirming purchase for user {userId}", Context.User.Id);
            await FollowupAsync("❌ **An unexpected error occurred.**\nPlease contact support if this issue persists.", ephemeral: true);
            try
            {
                await component.Message.ModifyAsync(msg =>
                {
                    msg.Content = "❌ **An unexpected error occurred.**\nPlease contact support if this issue persists.";
                    msg.Embed = null;
                    msg.Components = new ComponentBuilder().Build();
                });
            }
            catch { }
        }
    }

    [ComponentInteraction("purchase_cancel:*")]
    public async Task CancelPurchase(string userIdStr)
    {
        if (Context.User.Id.ToString() != userIdStr)
        {
            await RespondAsync("❌ You cannot cancel someone else's purchase!", ephemeral: true);
            return;
        }

        var cancelEmbed = new EmbedBuilder()
            .WithTitle("❌ Purchase Cancelled")
            .WithDescription("Your purchase has been cancelled.")
            .WithColor(Color.Red)
            .WithCurrentTimestamp()
            .Build();

        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(msg =>
        {
            msg.Content = "";
            msg.Embed = cancelEmbed;
            msg.Components = new ComponentBuilder().Build();
        });
    }
}
