# Purchase Command - Quick Summary

## What's New? 🎉

A beautiful and easy-to-use `/purchase` command for buying premium services directly from Discord!

## Key Features ✨

### 🎨 Beautiful Design
- Color-coded embeds (Gold for affordable, Red for insufficient balance)
- Rich emoji integration for better UX
- Clear information display with organized fields
- Interactive confirmation buttons

### 🛡️ Safe & Secure
- ✅ Balance verification before purchase
- ✅ Confirmation step prevents accidents  
- ✅ User identity verification
- ✅ Duplicate purchase prevention
- ✅ Comprehensive error handling

### 💎 Available Products

| Product | Slug | Duration |
|---------|------|----------|
| Premium (Monthly) | `premium` | 30 days |
| Premium+ (Weekly) | `premium_plus` | 7 days |
| **Premium+ (4 Weeks)** | `premium_plus-weeks` | 28 days ⭐ **33% cheaper!** |
| Premium+ (Hour) | `premium_plus-hour` | 1 hour |
| Starter Premium (Day) | `starter_premium-day` | 1 day |
| Starter Premium (Half Year) | `starter_premium` | 180 days |
| Pre API Access | `pre_api` | Varies |

## How to Use 📝

### Step 1: Run the command
```
/purchase product:premium_plus quantity:1
```

### Step 2: Review the beautiful embed showing:
- 📦 Quantity
- ⏰ Duration  
- 💰 Total Cost
- 💳 Your Balance
- 🎉 Special Pricing (if applicable)

### Step 3: Click a button
- ✅ **Confirm Purchase** → Complete transaction
- ❌ **Cancel** → No charges

### Step 4: Done! 
Get instant confirmation with all details

## Requirements 📋

1. **Linked Minecraft Account**
   - Use `/update-mc-user` first
   
2. **Sufficient CoflCoins**
   - Use `/topup` to buy more if needed

## Example Flow 🎬

```
User: /purchase product:premium_plus quantity:2
Bot: [Beautiful Embed showing 2x Premium+ for 14 days costing 2000 CoflCoins]
     [✅ Confirm Purchase] [❌ Cancel]
User: *clicks Confirm*
Bot: ✅ Purchase Successful! Premium+ activated for 14 days!
```

## Error Handling 🚨

| Error | Solution |
|-------|----------|
| ❌ Account not linked | Use `/update-mc-user` |
| ❌ Insufficient balance | Use `/topup` |
| ❌ Product not found | Check product name |
| ⏰ Duplicate prevention | Wait 1 minute |

## Technical Highlights ⚙️

- Based on reference implementation from Coflnet/SkyModCommands
- Modern Discord.Net interactions with buttons
- Comprehensive logging for support
- Graceful error handling with user-friendly messages
- Session-based security

## Why It's Great 🌟

1. **User-Friendly**: No complex syntax, just select and confirm
2. **Beautiful**: Professional embeds with colors and emojis
3. **Safe**: Multiple safeguards prevent mistakes
4. **Clear**: All information displayed before purchase
5. **Fast**: Complete purchase in 2 clicks
6. **Reliable**: Comprehensive error handling

## Files Added/Modified 📁

### New Files
- `Commands/PurchaseCommand.cs` - Main command implementation
- `Commands/PURCHASE_COMMAND_README.md` - Full documentation

### Modified Files  
- `Program.cs` - Added IProductsApi dependency injection
- `Services/DiscordHandler.cs` - Fixed missing closing brace

## Testing Checklist ✅

- [ ] Command shows up in Discord slash command list
- [ ] Product choices display correctly
- [ ] Embed shows all information properly
- [ ] Buttons work and are clickable
- [ ] Confirmation completes purchase
- [ ] Cancel button works
- [ ] Error for unlinked account
- [ ] Error for insufficient balance
- [ ] Only initiating user can confirm
- [ ] Logging works correctly

## Next Steps 🚀

1. Deploy the bot
2. Test with different products
3. Monitor logs for any issues
4. Gather user feedback
5. Consider adding features like:
   - Purchase history viewing
   - Gift purchases
   - Bundle deals
   - Auto-renewal options

---

**Made with ❤️ using Discord.Net and Coflnet APIs**
