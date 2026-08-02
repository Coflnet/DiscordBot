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
4. If the target user is absent, play the localized unavailable message,
   disclose recording, and offer a voicemail.
5. Play the localized anti-bot prompt and require DTMF `1`.
6. Recheck presence, reserve the single available bridge, and start the Discord
   handoff while Twilio tells the caller that the call is being connected.
7. Start a bidirectional Twilio Media Stream when the caller message finishes;
   the Discord handoff continues concurrently if it is not ready yet.
8. Join the waiting room, play a localized disclosure to everyone there, and
   require two seconds of microphone audio from the target within 15 seconds.
   If it is not received, play the caller's unavailable message and offer
   voicemail.
9. Move the target and bot to the private call channel.
10. Bridge 8 kHz G.711 μ-law audio to
   Discord's 48 kHz stereo PCM in both directions.
11. End the call when the caller disconnects, the target leaves, or the
    configured duration expires, then return the target to the waiting room.

The bridge forwards each 20 ms target-user audio frame to Twilio in order. A
bounded 500 ms queue absorbs short network bursts without allowing latency to
grow indefinitely; other Discord users' audio is never placed on the phone
stream. Hangup cleanup moves the target back before disposing audio streams or
releasing Redis state so slow secondary cleanup cannot delay the move.
On the first Discord gateway-ready event after a process start, the bot also
runs this return/disconnect cleanup and removes any stale active-call lease.

Calls that cannot connect because the target is unavailable or another call is
active are retained in Redis for up to 30 days (maximum 50 entries). The bot
stores only the time, reason, and a short HMAC-derived caller reference. An
administrator can review or clear them with `/missed-phone-calls`.

Completed voicemails are indexed in Redis by their Twilio Recording SID and a
notice is posted to the configured Discord channel. Only the configured target
Discord user can open `/phone-voicemails`; its ephemeral menu downloads audio
server-side and offers Play and Delete buttons. Delete removes the Twilio media
and its Redis index. An hourly cleanup removes recordings older than the
configured retention period.

## Configuration

Keep the feature disabled until the number, public endpoints, and recordings
are ready. Supply these through OpenBao/environment configuration; do not add
secrets to `appsettings.json`.

```text
TwilioVoice__Enabled=true
TwilioVoice__PublicBaseUrl=https://PUBLIC_BOT_HOST
TwilioVoice__MediaStreamUrl=wss://PUBLIC_BOT_HOST/api/twilio/voice/media
TwilioVoice__Region=ie1
TwilioVoice__VoicemailNotificationChannelId=DISCORD_CHANNEL_ID
TwilioVoice__VoicemailMaxSeconds=120
TwilioVoice__VoicemailRetentionDays=30
TwilioVoice__EnglishPressOneAudioUrl=https://PUBLIC_ASSET_HOST/press-one-en.wav
TwilioVoice__GermanPressOneAudioUrl=https://PUBLIC_ASSET_HOST/press-one-de.wav
TwilioVoice__EnglishUnavailableAudioUrl=https://PUBLIC_ASSET_HOST/unavailable-en.wav
TwilioVoice__GermanUnavailableAudioUrl=https://PUBLIC_ASSET_HOST/unavailable-de.wav
TwilioVoice__EnglishDiscordNoticeAudioUrl=https://PUBLIC_ASSET_HOST/notice-en.wav
TwilioVoice__GermanDiscordNoticeAudioUrl=https://PUBLIC_ASSET_HOST/notice-de.wav
TwilioVoice__AuthToken=TWILIO_AUTH_TOKEN
TwilioVoice__ApiKeySid=OPTIONAL_TWILIO_API_KEY_SID
TwilioVoice__ApiKeySecret=OPTIONAL_TWILIO_API_KEY_SECRET
TwilioVoice__CallerHashKey=AT_LEAST_32_RANDOM_CHARACTERS
TwilioVoice__StreamSigningKey=ANOTHER_32_RANDOM_CHARACTERS
```

The Auth Token remains required for validating Twilio webhook signatures. For
recording downloads and deletion, configure a region-matching restricted API
key when possible. Supported regions are `us1`, `ie1`, and `au1`; regional
requests use Twilio's current edge-specific API hostnames. When the optional
API key pair is absent, the application
uses the Account SID reported in Twilio's signed recording callback together
with the Auth Token.

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

The production image copies the native Opus, libsodium, and libdave libraries
needed by Discord.Net voice into the existing chiseled runtime image. DAVE
encrypts the Discord voice hop, but the bot terminates it to relay Twilio, so
the phone-to-Discord-user path is not end-to-end encrypted.

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
