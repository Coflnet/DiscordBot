# Purchase Command Documentation

## Overview
The `/purchase` command allows users to purchase premium services (premium, premium_plus, and pre_api) directly from Discord using their CoflCoins balance.

## Features

### ✨ User-Friendly Interface
- Beautiful embeds with color-coded information
- Interactive confirmation buttons
- Clear cost breakdown and duration display
- Balance checking before purchase
- Emoji-enhanced UI for better readability

### 🛡️ Safety Features
- Requires linked Minecraft account via `/update-mc-user`
- Balance verification before purchase
- Confirmation step to prevent accidental purchases
- User verification (only the user who initiated can confirm)
- Duplicate prevention with unique references

### 💎 Available Products

#### Premium Plans
- **Premium (Monthly)** - `premium`
  - Monthly premium subscription
  - Access to premium features

- **Premium+ (Weekly)** - `premium_plus`
  - Weekly premium plus subscription
  - Enhanced features

- **Premium+ (4 Weeks)** - `premium_plus-weeks`
  - 4-week premium plus bundle
  - **33% cheaper** than buying weekly!

- **Premium+ (Hour)** - `premium_plus-hour`
  - Hourly premium plus access
  - Perfect for testing or short-term needs

#### Starter Options
- **Starter Premium (Day)** - `starter_premium-day`
  - Single day of starter premium
  - Great for trying out features

- **Starter Premium (180 Days)** - `starter_premium`
  - 180 days of starter premium
  - Long-term value option

#### API Access
- **Pre API Access** - `pre_api`
  - API access for developers
  - Integrate with Coflnet services

## Usage

### Basic Command
```
/purchase product:<product_name> quantity:<1-12>
```

### Examples
```
/purchase product:premium quantity:1
/purchase product:premium_plus-weeks quantity:2
/purchase product:pre_api quantity:1
```

## How It Works

### Step 1: Initiate Purchase
Run the `/purchase` command with your desired product and quantity.

### Step 2: Review Information
The bot displays an embed showing:
- **Product Details**: Name and description
- **Quantity**: Number of units
- **Duration**: How long the service lasts
- **Total Cost**: Cost in CoflCoins
- **Your Balance**: Current CoflCoins balance
- **Special Pricing**: Any discounts applied (if applicable)

### Step 3: Confirm or Cancel
Click one of the buttons:
- ✅ **Confirm Purchase** - Completes the transaction
- ❌ **Cancel** - Cancels the purchase

### Step 4: Success!
Upon confirmation, you'll receive a success message with:
- Confirmation of purchase
- Duration activated
- Cost deducted from balance

## Prerequisites

### Required
1. **Linked Minecraft Account**
   - Use `/update-mc-user` to link your account
   - Must have Discord username set on Hypixel profile

2. **Sufficient CoflCoins**
   - Check your balance in the purchase preview
   - Use `/topup` to purchase more CoflCoins if needed

## Error Handling

### Common Errors

#### ❌ Account Not Linked
```
Error: You need to link your Minecraft account first!
Solution: Use /update-mc-user to link your account
```

#### ❌ Insufficient Balance
```
Error: You need X more CoflCoins
Solution: Use /topup to purchase more CoflCoins
```

#### ❌ Product Not Found
```
Error: The product could not be found
Solution: Check the product name and try again
```

#### ⏰ Duplicate Purchase Prevention
```
Error: Please wait a minute before purchasing again
Reason: Prevents accidental duplicate purchases
```

## Design Highlights

### Visual Feedback
- **Gold Color** - When purchase is affordable
- **Red Color** - When balance is insufficient
- **Green Color** - On successful purchase
- **Red Embed** - On cancellation

### Emojis Used
- 💎 - Purchase/Premium
- 📦 - Quantity
- ⏰ - Duration
- 💰 - Cost
- 💳 - Balance
- ⚠️ - Warning/Error
- ✅ - Success/Confirm
- ❌ - Cancel/Error
- 🎉 - Special offers/discounts
- 💚 - Thank you message

## Technical Details

### Architecture
- **Command Handler**: `PurchaseCommand.cs`
- **Interaction Type**: Slash Command + Component Buttons
- **APIs Used**:
  - `IUserApi` - User balance and purchase processing
  - `IProductsApi` - Product information (injected but available for future use)
  - `Persistence` - Discord/Minecraft account linking

### Security
- User ID verification on button interactions
- Discord ID encoded in button custom IDs
- Unique transaction references
- Session-based confirmation tokens

### Reference Implementation
Based on `PurchaseCommand` from [Coflnet/SkyModCommands](https://github.com/Coflnet/SkyModCommands/blob/main/Commands/Minecraft/PurchaseCommand.cs)

### Key Differences from Minecraft Implementation
1. **Discord Native**: Uses Discord embeds and buttons instead of Minecraft chat
2. **Visual Design**: Enhanced with colors, emojis, and structured embeds
3. **Simplified Flow**: Two-step process (review → confirm) instead of complex argument parsing
4. **Modern UX**: Interactive buttons instead of clickable chat messages

## Future Enhancements

### Potential Features
- [ ] Purchase history viewing
- [ ] Gift purchases to other users
- [ ] Subscription auto-renewal
- [ ] Bundle deals with multiple products
- [ ] Refund/cancellation within time window
- [ ] Payment method selection (CoflCoins vs external)

## Support

If you encounter issues:
1. Check that your account is linked with `/update-mc-user`
2. Verify you have sufficient CoflCoins
3. Contact support in the Discord server
4. Check bot logs for detailed error messages

## Developer Notes

### Adding New Products
Products are automatically loaded from the Payments API. To add new product choices to the slash command:

```csharp
[Choice("Product Name Display", "product_slug")]
```

### Logging
All purchases are logged with:
- Discord User ID
- User ID (internal)
- Product purchased
- Quantity
- Total cost

### Error Handling
Comprehensive try-catch blocks with:
- User-friendly error messages
- Detailed logging for debugging
- Fallback handling for API errors
