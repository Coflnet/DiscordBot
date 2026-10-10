Coflnet Discord Bot

In the bot DM, run `/creator apply application:<your application text>`.
The bot shows a private preview with the privacy notice; press **Submit application**
to send it to reviewer `267680402594988033`. No message link is needed.
The reviewer receives the text in their bot DM and confirms the outcome with
`/creator review applicant:<Discord ID> application:<application text> residence:<country> capacity:<status>`.
`/creator request-guardian`, `/creator set-status`, and `/creator show` also work
in bot DMs. Applicants receive review and status outcomes by DM.

`/creator balance` privately shows the caller's linked account balance in EUR:
pending, outstanding, reserved for payout, available, and payout threshold.
Creators can link their account with `/update-mc-user`. The balance includes
report and referral rewards on the same account and uses the existing
`REFERRAL_BASE_URL` and `REWARDS:WRITE_TOKEN` configuration.

The `/creator review`, `/creator request-guardian`, `/creator set-status`, and
`/creator show` commands are restricted at runtime solely to Discord account
`267680402594988033`.
They manage immutable Expert seller reviews through SkyReferral. Applicants
can link their Discord and Minecraft accounts, or the reviewer can supply the
verified `minecraft-uuid`. The review command hashes
the application text and references the review interaction (legacy message links
are also accepted and hash the message and attachment metadata); message
contents and identity documents are not copied into the application database.
This reviewed-seller record is separate from SkyModCommands' legacy publisher
list: the legacy list permits only free Configs, while paid publication requires
an approved review with the supported country, capacity and tax route.
Residence, capacity and the privacy-notice version are enough for free
publication. Paid publication also requires the correct tax residence and
settlement or invoice route. A private verification reference remains optional
until payout. Do not request or post identity documents in an ordinary Discord
channel. If payout needs more verification, put it only in the separately
access-controlled payout system and record its opaque reference. A minor's
separate legal representative receives the exact Creator agreement by DM and
accepts it through the bound button; the representative needs a Discord account,
not a Coflnet seller or fee owner. An approved creator age 16+ may publish paid
Configs after that acceptance where paid sellers are supported; elsewhere the
creator may publish free Configs. Payout remains unavailable until the ordinary
payout requirements are complete.

Payouts. Any creator can run `/creator payout-request` for themselves (guild or
bot DM) with no options, or with an optional `amount`. The bot opens a form for legal
name, postal address, bank details, optional VAT id and service period (or pass `name`,
`address`, `bank_details` [and `vat_id`, `period`] as slash options, `;` separating
lines, to skip the form; all three or none). The checks are: linked account, approved
review on the `UkSelfBilling` route (UK VAT business: self-billing invoice, reverse charge) or the `Statement` route (private individual, seller type Individual: payout statement without VAT; a VAT id is refused), amount between the threshold and the available
balance. The creator gets the self-billing invoice (Gutschrift) or payout statement (Abrechnung) PDF and a confirmation;
the reviewer is DMed the PDF, the accounting `{invoice}.booking.json`, the amount to
transfer, the bank details and the requester. The invoice number is generated
(`SB-yyyyMMdd-XXXXXX`, or `PS-...` for Statement, one per creator per day). The reviewer may also pass `user` and
`invoice_number` to request on a creator's behalf; then both files are returned directly.
Name, address and bank details appear only in the PDF and those messages; they are never
stored, logged or sent to the ledger. Reviewer only: `/creator payout-status user:` shows
a creator's balance, whether the threshold is reached, the amount payable, the reviewed
tax route/country and open payout requests.
`/creator payout-complete user: invoice_number: payment_reference:` (for example
`paypal:TXID`) appends the matching `Payout` and reports the new balance.
Configure `REWARDS:PAYOUT_TOKEN` (min 32 characters, separate from the write
token). The payer is always Coflnet GmbH; `CREATOR_PAYOUT:ISSUER_NAME`,
`ISSUER_ADDRESS` and `ISSUER_VAT_ID` default to its details in `appsettings.json`.
For a business, review the entity and adult signatory authority and select its
EU invoice, England/Wales VAT self-billing, or settlement-statement route before
paid publication; business status is not a minor-capacity workaround.
The Discord application prompt must show the current privacy URL and its version
before the applicant submits data; record that same version in the review. The
version records delivery of the notice, not privacy-policy consent.
Configure `REFERRAL_BASE_URL` and the OpenBao-backed
`CREATOR_ONBOARDING:REVIEW_TOKEN` (at least 32 characters).

The `/reward record` command is restricted by the same runtime check to Discord
account `267680402594988033`. It appends an immediately available EUR award for
either a Coflnet reward account or a reporter email using only the existing
SkyReferral reward ledger. Email awards use a stable normalized-email hash, so
later reports accrue to the same balance without storing the address in the
ledger or requiring an account. Verify control of the email and complete the
ordinary identity, tax and payout checks only when settlement is requested.
Supply a stable source reference: retries with the same reference and
fields return the existing entry, while conflicting reuse fails. Only `Report`
and `Referral` are exposed. Use `Referral` only under a versioned offer that
expressly promises EUR; the current referred-user onboarding grant remains
promotional CoflCoins. Evidence must be a namespaced opaque reference; never put
raw identity data in the command. Configure the OpenBao-backed
`REWARDS:WRITE_TOKEN` (at least 32 characters).

A security report that has no Coflnet account still accrues to the same
stable email-hash account. `/reward pending` records an unreviewed `Report`
award (ledger kind `Pending`) for the reporter's email with its own reference,
amount, offer-version and reason; nothing is available for payout yet. Once a
report is confirmed, approve it with `/reward record` using the *same*
`recipient`, `source`, `amount-cents` and `offer-version` as the pending entry
plus its `pending` entry ID — this appends the related `Award` (kind `Award`)
that inherits the pending entry's payout threshold, and `reference` must be a
*new*, not-yet-used value (the ledger keys entries by reference, so reusing the
pending entry's own reference is rejected). If a report is rejected instead,
`/reward cancel` records a `Cancellation` related to the pending entry with a
reason — no amount is needed, since a cancellation always closes the pending
amount at zero. `/reward query` looks up an email's or account's balance
(pending, outstanding, available, payout threshold) and its most recent ledger
entries via the read-only `WRITE_TOKEN`-gated balance/ledger endpoints; the raw
email is only ever hashed client-side, never sent to or stored by the ledger.
