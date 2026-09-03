Coflnet Discord Bot

The `/creator review`, `/creator request-guardian`, `/creator set-status`, and
`/creator show` commands are restricted at runtime solely to Discord account
`267680402594988033`.
They manage immutable Expert seller reviews through SkyReferral. Applicants
must have linked their Discord and Minecraft accounts. The review command hashes
the referenced Discord application message and attachment metadata; message
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
