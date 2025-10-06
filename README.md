# Twitch Shoutout with Random Clips for Streamer.bot

This action for **Streamer.bot** makes your shoutouts more engaging by pulling a random clip (up to 30 seconds) from the streamer you’re shouting out and playing it directly in OBS — along with a customizable chat message.

Instead of only typing a message in chat, your community will actually see and hear a highlight from the person you’re shouting out.

---

## Quick Setup

### 1. OBS Setup
1. Create a **Media Source** in OBS (the file path doesn’t matter).
2. Right-click the source → **Transform → Edit Transform**.
3. Change **Bounding Box Type** to `Stretch to bounds`.
4. Set **Bounding Box Size** to match your canvas (e.g. `1920x1080`).
5. Resize and position the source where you’d like clips to play.

---

### 2. Streamer.bot Setup  
1. Copy everything from `ImportString.txt`.
2. In **Streamer.bot**, click **Import**, paste, and confirm.  
3. Enable the commands:
   - `!shoutout` (alias: `!so`)
   - `!autoshoutout` (alias: `!as`)
   - `!setcustommessage` (alias: `!scm`)
4. Open **Actions → Ame - Shoutout → Shoutout → Variables** and set:  
   - `ShoutoutSceneName` → the OBS scene with your media source  
   - `ShoutoutSourceName` → the name of the OBS media source  
   - `clipsWithinDays` → how recent clips must be (default: 90 days)
   - `preferPopularClips` → Set to **true** if you want popular clips to be preferred
      - Uses a weighted system to prefer clips with higher views, but lower view clips still have a non-zero chance of showing.
5. Leave `ShoutoutHash` and `ShoutoutClientId` alone unless Twitch changes their API.

---

## Commands  

| Command | Aliases | What it does |
|---------|---------|--------------|
| `!shoutout <username> [clip_url / noclip]` | `!so` | Shoutouts a user. Optionally play a specific clip (`clip_url`) or no clip at all (`noclip`). |
| `!autoshoutout <add/remove> <username>` | `!autoshout`, `!as` | Manage an auto-shoutout list. Streamers on this list are shouted out automatically when they send their first message of the stream. |
| `!setcustommessage <username> [template]` | `!custommsg`, `!scm` | Sets a custom shoutout message for a user. If no template is provided, their custom message is cleared and the default is used. |

---

## Features

- Shoutouts can be triggered manually, on raid, or automatically.
- Plays a random clip from the target streamer (≤ 30 seconds).
- Supports pronouns via [pr.alejo.io](https://pr.alejo.io/).
- Allows fully customizable shoutout messages per user.

---

## Message Tokens

You can customize shoutout templates with tokens.

| Token | Description |
|-------|-------------|
| `{USER}` | The person giving the shoutout |
| `{STREAMER}` | The person receiving the shoutout |
| `{GAME}` | The game they were playing |
| `{TITLE}` | Their stream title |
| `{URL}` | A link to their channel |
| `{PRONOUN_SUBJECT}` | Subject pronoun (she/he/they/fae) |
| `{PRONOUN_OBJECT}` | Object pronoun (her/him/them/faer) |
| `{SUBJECT_WASWERE}` | e.g. “she was” / “they were” |
| `{SUBJECT_ISARE}` | e.g. “he is” / “they are” |
| `{LIVE_STATUS}` | Shows if they’re live or not (e.g. “she is currently streaming” or “they were last streaming”) |

**Example:**
```
Go show @{STREAMER} some love, {LIVE_STATUS:lower} {GAME:title|something awesome}!
```
Will output the following if 'AmeliaFromFinance' is currently live:  
> Go show @AmeliaFromFinance some love, she is currently streaming Software and Game Development!

Or, will output the following if 'AmeliaFromFinance' is not live:  
> Go show @AmeliaFromFinance some love, she was last streaming Software and Game Development!

---

## Modifiers and Defaults

You can adjust how tokens are displayed with modifiers and default values.

- `:upper` → convert to UPPERCASE
- `:lower` → convert to lowercase
- `:title` → capitalize first letter of each word
- `:trim` → remove extra spaces
- `|default` → provide fallback text if token is empty

**Example:**
```
{GAME:upper|something awesome}
```
- If the game is found → `VALORANT`
- If not → `something awesome`

---

## Notes

- This uses Twitch’s GraphQL API. It may break if Twitch changes things.
- If clips stop working, you may need to update:
  - `ShoutoutHash`
  - `ShoutoutClientId`

---

## Support

If you run into issues, reach out on Discord: **eintr**
