# Purchase Command - Visual Preview

## Command Execution Flow

### 1️⃣ User Executes Command
```
/purchase product:premium_plus quantity:2
```

---

### 2️⃣ Bot Displays Beautiful Confirmation Embed

```
╔════════════════════════════════════════════════════════╗
║                💎 Purchase Premium+                     ║
╠════════════════════════════════════════════════════════╣
║                                                         ║
║  Premium+ subscription with enhanced features          ║
║                                                         ║
║  📦 Quantity          ⏰ Duration        💰 Total Cost  ║
║  2x                   14 days            2,000 CoflCoins║
║                                                         ║
║  💳 Your Balance                                        ║
║  5,000 CoflCoins                                        ║
║                                                         ║
║  Product: premium_plus                                  ║
║  Today at 12:34 PM                                      ║
║                                                         ║
╚════════════════════════════════════════════════════════╝

Do you want to proceed with this purchase?

[   ✅ Confirm Purchase   ]  [   ❌ Cancel   ]
```

**Color:** 🟡 Gold (when affordable) | 🔴 Red (when insufficient balance)

---

### 3️⃣ User Clicks "✅ Confirm Purchase"

---

### 4️⃣ Bot Shows Success Confirmation

```
╔════════════════════════════════════════════════════════╗
║          ✅ Purchase Successful!                        ║
╠════════════════════════════════════════════════════════╣
║                                                         ║
║  Premium+ has been activated!                          ║
║                                                         ║
║  📦 Purchased                                           ║
║  2x Premium+                                            ║
║                                                         ║
║  ⏰ Duration                                            ║
║  14 days                                                ║
║                                                         ║
║  💰 Cost                                                ║
║  2,000 CoflCoins                                        ║
║                                                         ║
║  Thank you for supporting us! 💚                        ║
║  Today at 12:34 PM                                      ║
║                                                         ║
╚════════════════════════════════════════════════════════╝

✅ Purchase completed!
```

**Color:** 🟢 Green

---

## Alternative Flow: Cancellation

### If User Clicks "❌ Cancel"

```
╔════════════════════════════════════════════════════════╗
║              ❌ Purchase Cancelled                      ║
╠════════════════════════════════════════════════════════╣
║                                                         ║
║  Your purchase has been cancelled.                     ║
║                                                         ║
║  Today at 12:34 PM                                      ║
║                                                         ║
╚════════════════════════════════════════════════════════╝
```

**Color:** 🔴 Red

---

## Error Scenarios

### ❌ Error: Account Not Linked

```
╔════════════════════════════════════════════════════════╗
║  ❌ You need to link your Minecraft account first!     ║
╠════════════════════════════════════════════════════════╣
║                                                         ║
║  Use /update-mc-user to link your account.            ║
║                                                         ║
╚════════════════════════════════════════════════════════╝
```

---

### ❌ Error: Insufficient Balance

```
╔════════════════════════════════════════════════════════╗
║                💎 Purchase Premium+                     ║
╠════════════════════════════════════════════════════════╣
║                                                         ║
║  Premium+ subscription with enhanced features          ║
║                                                         ║
║  📦 Quantity          ⏰ Duration        💰 Total Cost  ║
║  2x                   14 days            2,000 CoflCoins║
║                                                         ║
║  💳 Your Balance                                        ║
║  500 CoflCoins                                          ║
║                                                         ║
║  ⚠️ Insufficient Balance                                ║
║  You need 1,500 more CoflCoins.                        ║
║  Use /topup to purchase more CoflCoins.                ║
║                                                         ║
║  Product: premium_plus                                  ║
║  Today at 12:34 PM                                      ║
║                                                         ║
╚════════════════════════════════════════════════════════╝

Do you want to proceed with this purchase?

[   ✅ Confirm Purchase   ]  [   ❌ Cancel   ]
```

**Color:** 🔴 Red (buttons still present but confirmation will fail)

---

## Special Features Display

### 🎉 When Discount/Special Pricing is Available

```
╔════════════════════════════════════════════════════════╗
║                💎 Purchase Premium+                     ║
╠════════════════════════════════════════════════════════╣
║                                                         ║
║  Premium+ subscription with enhanced features          ║
║                                                         ║
║  📦 Quantity          ⏰ Duration        💰 Total Cost  ║
║  1x                   7 days             900 CoflCoins  ║
║                                                         ║
║  💳 Your Balance                                        ║
║  5,000 CoflCoins                                        ║
║                                                         ║
║  🎉 Special Pricing                                     ║
║  A discount or special pricing has been applied        ║
║  to your purchase!                                      ║
║                                                         ║
║  Product: premium_plus                                  ║
║  Today at 12:34 PM                                      ║
║                                                         ║
╚════════════════════════════════════════════════════════╝

Do you want to proceed with this purchase?

[   ✅ Confirm Purchase   ]  [   ❌ Cancel   ]
```

---

## Product Selection in Discord

When typing `/purchase`, the user sees:

```
/purchase product:
```

**Dropdown shows:**
- Premium (Monthly) - `premium`
- Premium+ (Weekly) - `premium_plus`
- Premium+ (4 Weeks - 33% cheaper!) - `premium_plus-weeks`  ⭐
- Premium+ (Hour) - `premium_plus-hour`
- Pre API Access - `pre_api`
- Starter Premium (Day) - `starter_premium-day`
- Starter Premium (180 Days) - `starter_premium`

```
quantity: [1] (Number between 1-12)
```

---

## Comparison: Old vs New

### 🔴 Before (Text-based)
```
User: !buy premium 2
Bot: Do you want to buy premium 2x for 2000 coins? Type !confirm
User: !confirm
Bot: Purchase successful
```

### 🟢 After (Modern Discord UI)
```
User: /purchase product:premium quantity:2
Bot: [Beautiful embed with all details]
     [Interactive buttons]
User: *clicks button*
Bot: [Success embed with confirmation]
```

**Benefits:**
- ✅ Visual clarity
- ✅ All info at a glance
- ✅ No typing errors
- ✅ Professional look
- ✅ Mobile-friendly
- ✅ Reduced steps

---

## Mobile Experience

On mobile devices, the command works perfectly:
1. Tap `/purchase`
2. Select product from dropdown
3. Enter quantity with number picker
4. Review beautiful card-style embed
5. Tap one of the large buttons
6. See success confirmation

**Optimized for:**
- 📱 iOS Discord app
- 🤖 Android Discord app
- 💻 Desktop app
- 🌐 Web browser

---

## Accessibility Features

- **Clear Icons**: Emojis convey meaning visually
- **Color Coding**: Gold (good), Red (warning), Green (success)
- **Large Buttons**: Easy to tap on any device
- **Descriptive Text**: Each field clearly labeled
- **Confirmation Step**: Prevents accidental purchases
- **Error Messages**: Clear and actionable

---

**This is what a modern, user-friendly purchase command looks like! 🚀**
