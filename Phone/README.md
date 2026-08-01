# Twilio to Discord voice

This integration accepts a Twilio call only while Discord user
`267680402594988033` is in waiting-room voice channel `939528464455835719`.
After a disclosure is played there, the bot moves the user and itself to
private call channel `1041286026259345449`.

The inbound flow is:

1. Validate Twilio's request signature.
2. Atomically allow at most three calls from a caller in a rolling 24-hour
   window. Redis stores only an HMAC of the E.164 caller number. Anonymous
   callers and later calls receive a Twilio `<Reject>` before answer.
3. Select German recordings for callers whose E.164 `From` number starts with
   `+49`; use English for all other callers.
4. If the target user is absent, play the localized unavailable message and
   hang up.
5. Play the localized anti-bot prompt and require DTMF `1`.
6. Recheck presence, reserve the single available bridge, and start a
   bidirectional Twilio Media Stream.
7. Join the waiting room and play a localized disclosure to the target and
   everyone else currently there.
8. Move the target and bot to the private call channel.
9. Bridge 8 kHz G.711 μ-law audio to
   Discord's 48 kHz stereo PCM in both directions.
10. End the call when the caller disconnects, the target leaves, or the
    configured duration expires, then return the target to the waiting room.

## Configuration

Keep the feature disabled until the number, public endpoints, and recordings
are ready. Supply these through OpenBao/environment configuration; do not add
secrets to `appsettings.json`.

```text
TwilioVoice__Enabled=true
TwilioVoice__PublicBaseUrl=https://PUBLIC_BOT_HOST
TwilioVoice__MediaStreamUrl=wss://PUBLIC_BOT_HOST/api/twilio/voice/media
TwilioVoice__EnglishPressOneAudioUrl=https://PUBLIC_ASSET_HOST/press-one-en.wav
TwilioVoice__GermanPressOneAudioUrl=https://PUBLIC_ASSET_HOST/press-one-de.wav
TwilioVoice__EnglishUnavailableAudioUrl=https://PUBLIC_ASSET_HOST/unavailable-en.wav
TwilioVoice__GermanUnavailableAudioUrl=https://PUBLIC_ASSET_HOST/unavailable-de.wav
TwilioVoice__EnglishDiscordNoticeAudioUrl=https://PUBLIC_ASSET_HOST/notice-en.wav
TwilioVoice__GermanDiscordNoticeAudioUrl=https://PUBLIC_ASSET_HOST/notice-de.wav
TwilioVoice__AuthToken=TWILIO_AUTH_TOKEN
TwilioVoice__CallerHashKey=AT_LEAST_32_RANDOM_CHARACTERS
TwilioVoice__StreamSigningKey=ANOTHER_32_RANDOM_CHARACTERS
```

Suggested caller recordings:

- English: “Please press 1 to continue.”
- German: “Bitte drücken Sie die 1, um fortzufahren.”
- English: “This telephone service is currently not reachable. Please try
  again later.”
- German: “Dieser Telefondienst ist derzeit nicht erreichbar. Bitte versuchen
  Sie es später erneut.”

Suggested waiting-room disclosures:

- English: “A telephone caller is about to be connected. Audio will be relayed
  through the support bot and is not recorded by this service.”
- German: “Ein Telefonanrufer wird gleich verbunden. Der Ton wird über den
  Support-Bot übertragen und von diesem Dienst nicht aufgezeichnet.”

The two Discord notice files must be RIFF/WAVE, 48 kHz, stereo, signed 16-bit
PCM, no longer than 15 seconds, and at most 5 MiB. Twilio's four caller-facing
files may use any audio format supported by Twilio `<Play>`.

The application must be reachable over HTTPS/WSS on port 443. The reverse
proxy must allow WebSocket upgrades on `/api/twilio/voice/media` and preserve
the request path. Do not rewrite the public URL: Twilio signature validation
uses the exact configured URL.

The production image copies only the native Opus and libsodium libraries
needed by Discord.Net voice into the existing chiseled runtime image.

## Discord permissions and test

The bot needs these permissions:

- `View Channel`, `Connect`, and `Speak` in both voice channels.
- `Move Members` in the guild to move the configured target.
- `Use Application Commands` where administrators invoke the test.

Restrict the private channel's `View Channel` and `Connect` permissions to the
target, the bot, and any deliberately authorized staff. A handoff is refused
when another non-bot user is already in that channel.

The `Guilds` and `Guild Voice States` gateway intents are also required; both
are already present in the bot's current unprivileged intents.

An administrator can run `/test-phone-call german:false` or
`/test-phone-call german:true`. It uses the same disclosure, movement, and
cleanup path without accepting a Twilio call. The target must begin in the
waiting room. The test holds the same single-call lock, spends five seconds in
the private channel, and then returns the target to the waiting room.

Configure the number's incoming voice webhook as:

```text
POST https://PUBLIC_BOT_HOST/api/twilio/voice/incoming
```

Twilio's IncomingPhoneNumbers REST resource can set this after the number has
been provisioned:

```csharp
TwilioClient.Init(accountSid, authToken);
await IncomingPhoneNumberResource.UpdateAsync(
    pathSid: incomingPhoneNumberSid,
    voiceUrl: new Uri("https://PUBLIC_BOT_HOST/api/twilio/voice/incoming"),
    voiceMethod: Twilio.Http.HttpMethod.Post);
```

Do not automate purchasing a number at application startup. A German local
number first needs a Twilio-approved regulatory bundle for the correct end-user
and number type, and its locality/address must be appropriate. Purchase or
assign the number only after that approval, then make the REST update above.

## Legal deployment note

For German distance contracts, Article 246a section 1(1)(3) EGBGB currently
lists the trader's telephone number among the pre-contract information.
Article 246a section 4 requires the information to be clear, comprehensible,
and provided before the consumer is bound.

This implementation provides a real contact route when the responsible person
is available and an explanatory recording otherwise. It is not a legal opinion
that limited hours, a three-call abuse limit, or Discord-only availability
satisfies every business's duty to enable quick and effective consumer
communication. Have the final availability wording and operating hours
approved by the legal reviewer, and publish email/postal complaint routes
alongside the number.

Relevant primary documentation:

- https://www.gesetze-im-internet.de/bgbeg/art_246a__1.html
- https://www.gesetze-im-internet.de/bgbeg/art_246a__4.html
- https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:02011L0083-20220528
- https://www.twilio.com/docs/phone-numbers/regulatory/getting-started
- https://www.twilio.com/docs/voice/media-streams
- https://www.twilio.com/docs/usage/webhooks/webhooks-security
