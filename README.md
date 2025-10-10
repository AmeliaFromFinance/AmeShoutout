# Twitch Shoutout with Random Clips for Streamer.bot

This action for **Streamer.bot** makes your shoutouts more engaging by pulling a random clip (up to 30 seconds) from the streamer you’re shouting out and playing it directly in OBS — along with a customizable chat message.

It also integrates Twitch’s native `/shoutout` feature, automatically respecting Twitch’s cooldowns and limits.

Instead of only typing a message in chat, your community will actually see and hear a highlight from the person you’re shouting out.

---

## Quick Setup

### 1. OBS Setup

1. Create a **Media Source** in OBS (the file path doesn’t matter).  
2. Right-click the source → **Transform → Edit Transform**.  
3. Change **Bounding Box Type** to `Stretch to bounds`.  
4. Set **Bounding Box Size** to match your canvas (for example, `1920x1080`).  
5. Resize and position the source where you’d like clips to play.

---

### 2. Streamer.bot Setup

1. Copy everything from `ImportString.txt`.  
2. In **Streamer.bot**, click **Import**, paste, and confirm.  
3. Enable the commands:
   - `!shoutout` (alias: `!so`)
   - `!autoshoutout` (alias: `!as`)
   - `!setcustommessage` (alias: `!scm`)
4. Open **Actions → Ame - Shoutout → Shoutout → Variables** and configure the following:

| Variable | Description |
|-----------|-------------|
| **ShoutoutSceneName** | The OBS scene with your media source. |
| **ShoutoutSourceName** | The name of the OBS media source. |
| **ShoutoutClipsWithinDays** | How recent clips must be (default: `90`). |
| **ShoutoutPreferPopular** | Set to `true` to prefer clips with higher view counts (weighted random). |
| **ShoutoutShowClips** | Set to `true` to enable clip playback, or `false` to disable all clip playback. |
| **ShoutoutType** | Controls where the shoutout goes: `both`, `twitch`, or `message`. |
| **ShoutoutVideoAccessTokenHash** | Hash for `VideoAccessToken_Clip` GraphQL query. |
| **ShoutoutUseLiveHash** | Hash for `UseLive` GraphQL query (live status check). |
| **ShoutoutChatClipHash** | Hash for `ChatClip` GraphQL query (duration lookup). |
| **ShoutoutGqlClientId** | GraphQL Client ID for Twitch GQL calls. |

> Note: For **ShoutoutType**, `message` will send a shoutout message to chat, `twitch` will use the Twitch integrated shoutout (**/shoutout**), and `both` will send a message *and* use the **/shoutout** command.

---

## Commands

| Command | Aliases | Description |
|----------|----------|-------------|
| `!shoutout <username> [clip_url / noclip / no-clip]` | `!so` | Shoutouts a user. Optionally play a specific clip (paste a clip URL or slug), skip clip playback (`noclip`/`no-clip`), or let it pick a random clip. |
| `!autoshoutout <add/remove> <username>` | `!autoshout`, `!as` | Manages the auto-shoutout list. Streamers on this list are automatically shouted out when they send their first message of the stream. |
| `!setcustommessage <username> [template]` | `!custommsg`, `!scm` | Sets a custom shoutout message for a user. If no template is provided, their custom message is cleared and the default is used. |

---

## Features

- Shoutouts can be triggered manually, automatically, or on raid.  
- Plays a random clip from the target streamer (30 seconds or less).  
- Weighted random (by view count) is available via `ShoutoutPreferPopular = true`.
- Can disable clip playback entirely via `ShoutoutShowClips = false`.
- Integrates Twitch’s native `/shoutout` with built-in cooldown handling.
- Fully customizable per-user shoutout messages.
- Pronoun support via [pr.alejo.io](https://pr.alejo.io/)

---

## Twitch Shoutout Behavior

This action also uses Twitch’s built-in `/shoutout` system.

- Twitch allows one `/shoutout` per **2 minutes** across the channel.  
- Each streamer can only be shouted out once per **hour**.  
- If `ShoutoutType = "twitch"`, the script will announce in chat when it’s waiting for the cooldown (e.g., “I will shoutout @Name in 34.5s…”).
- If `ShoutoutType = "both"`, it sends both the Twitch /shoutout and your formatted chat message.
- If the `/shoutout` fails (e.g., per-user hourly limit hit), a failure message is displayed in chat.

---

## Message Tokens

You can customize shoutout templates with tokens.

| Token | Description |
|--------|-------------|
| `{USER}` | The person giving the shoutout. |
| `{STREAMER}` | The person receiving the shoutout. |
| `{GAME}` | The game they were playing. |
| `{TITLE}` | Their stream title. |
| `{URL}` | A link to their channel. |
| `{PRONOUN_SUBJECT}` | Subject pronoun (she/he/they/fae). |
| `{PRONOUN_OBJECT}` | Object pronoun (her/him/them/faer). |
| `{SUBJECT_WASWERE}` | Example: “she was” / “they were.” |
| `{SUBJECT_ISARE}` | Example: “he is” / “they are.” |
| `{LIVE_STATUS}` | “she is currently streaming” or “they were last streaming” depending on live state. |

Example template:

```text
Go show @{STREAMER} some love, {LIVE_STATUS:lower} {GAME:title|something awesome}!
```
If `AmeliaFromFinance` is live:
> Go show @AmeliaFromFinance some love, she is currently streaming Software and Game Development!

If not live:
> Go show @AmeliaFromFinance some love, she was last streaming Software and Game Development!

## Modifiers and Defaults

You can adjust how tokens are displayed with modifiers and default values.

| Modifier | Description |
|-----------|-------------|
| `:upper` | Convert to uppercase. |
| `:lower` | Convert to lowercase. |
| `:title` | Title-style capitalization (common small words like “and”, “of”, “to” remain lowercase). |
| `:trim` | Remove extra spaces. |
| `\|default` | Provide fallback text if the token is empty. |

Example:
```text
{GAME:upper|something awesome}
```
- If the game is found → `VALORANT`
- If not → `something awesome`

## Clip Selection Details

- Clips must be **30 seconds or less** (with a small 0.05s tolerance).  
- A small 1.5-second padding is added to ensure playback completes smoothly in OBS.  
- If `ShoutoutPreferPopular = true`, the script uses a **weighted random** system based on view counts.
- If false, it uses uniform random (reservoir sampling) over eligible clips.
- Preferred clip quality is up to **720p**; falls back to the best available if none ≤ 720p.

---

## Notes

- Uses Twitch’s **GraphQL API** — it may break if Twitch changes queries.  
- If clips or live checks stop working, update these variables:
  - `ShoutoutVideoAccessTokenHash`
  - `ShoutoutChatClipHash`
  - `ShoutoutUseLiveHash`
  - `ShoutoutGqlClientId`

---

## Support

If you run into issues, reach out on Discord: **eintr**
