# Twitch Shoutout with Random Clips for Streamer.bot

This action from Streamer.bot makes your shoutouts more engaging by pulling a random clip from the streamer you're shouting out and playing it directly in OBS — along with a customizable chat message.

It also integrates Twitch's native `/shoutout` feature, automatically respecting Twitch's cooldowns and limits.

Instead of only typing a message in chat, your community will actually see and hear a highlight from the person you're shouting out.

---


## Features

- Shoutouts can be triggered manually, automatically, or on raid.
- Plays a random clip from the target streamer (30 seconds or less).
- Weighted random (by view count) is available via `PreferPopular = true`.
- Can disable clip playback entirely via `DisplayClips = false`.
- Integrates Twitch’s native `/shoutout` with built-in cooldown handling.
- Fully customizable per-user shoutout messages.
- Pronoun support via [pr.alejo.io](https://pr.alejo.io/)

---

## Twitch Shoutout Behavior

This action also uses Twitch’s built-in `/shoutout` system.

- Twitch allows one `/shoutout` per **2 minutes** across the channel.
- Each user can only be shouted out once per **hour**.
- If `ShoutoutType = "twitch"`, the script will announce in chat when it’s waiting for the cooldown (e.g., “I will shoutout @Name in 34.5s…”).
- If `ShoutoutType = "both"`, it sends both the Twitch /shoutout and your formatted chat message.
- If the `/shoutout` fails (e.g., per-user hourly limit hit), a failure message is displayed in chat.

---

## Quick Setup

### Streamer.bot setup

1. Copy everything from `ImportString.txt`.  
2. In **Streamer.bot**, click **Import**, paste, and confirm.  
3. Go to **Commands**, find **[Ame] Shoutout**, right-click the group and click **Enable All**
4. Go to **Settings** → **Groups**, type `Auto Shoutout` in the **Add Group** box and then click **Add**.
4. Open **Actions → [Ame] Shoutout → Shoutout → Configuration** and configure the following:

| Variable | Description |
|-----------|-------------|
| **UseChatBot** | `True` if you use my chat bot code, otherwise `False` - Default: `False`|
| **DisplayClips** | `True` to display clips on streamer, otherwise `False` - Default: `True` |
| **PreferPopular** | `True` to prefer clips with higher view counts, otherwise `False` - Default: `False` |
| **ShowRaidCount** | `True` to state how many times the user has raided your channel, otherwise `False` - Default: `True` |
| **ShoutoutType** | Set to your desired option to control the behaviour, read the note below - Options: `message`, `twitch`, `both` |
| **DefaultTemplate** | The default message template that will be used when giving a shoutout, which will be used if the user doesn't have a custom message set. - Read the **Tokens** section below to see what values can be used in the message |
| **ClipDays** | Control how recent clips will be by setting a max days to retrieve clips from - Default: `30` |
| **ClipLength** | Control how long clips can be, in seconds - Default: `30` |
| **OtherMessages** | `True` if you wish error messages to be sent in chat, otherwise `False` - Default `True` |

> [!NOTE]
> `ShoutoutType` when set to `message` will send a shoutout message in the Twitch chat.
> 
> When set to `twitch` will send a shoutout using the Twitch integrated `/shoutout` command, which can only be used once every 2 minutes, or hourly for the same user.
>
> When set to `both` will send a chat message and use the `/shoutout` command to give a shoutout.

> [!NOTE]
> If you want text to display above the clip then go into OBS to the `AmeShoutout` scene and add a `Text (GDI+)` source, positioning it wherever you want it to display, and then going back to streamer.bot to the `[Ame] Shoutout` -> `Shoutout` action, then expand the `OBS Settings` group and change `gdiTextSourceName` to whatever you named the `Text (GDI+)` source, and `textTemplate` to whatever you want the text to say.
>
> If you don't have the `AmeShoutout` scene then make sure OBS is open, go to the `[Ame] Shoutout` -> `Shoutout` action on SB, right click the `Test` trigger and click `Test Trigger`. This will create the scene and source for displaying clips.

---

## Commands

| Command | Description |
|----------|-------------|
| `!so <username> [no]` | Gives a specified user a shoutout, with the option to put `no` at the end if you don't want a clip to be displayed. |
| `!as <add/remove> <username>` | Adds a user to the `Auto Shoutout` group on streamer.bot. Users in this group will receive a shoutout on their first message of each stream. |
| `!scm <username> [template]` | Sets a custom shoutout message for a user. If no template is provided, their custom message is cleared and the default is used. |

---

## Tokens

| Token | Description |
|-------|-------------|
| **{USER}** | The name of the person giving the shoutout |
| **{STREAMER}** | The name of the person receiving the shoutout |
| **{GAME}** | The game that the streamer was last playing |
| **{URL}** | The URL to their stream. |
| **{LIVE_STATUS}** | Read the first note below
| **SUBJECT** | The subject pronoun (They, He, She, Fae, etc...) - Defaults to **they** if no pronoun is set.
| **OBJECT** | The object pronoun (Them, Him, Her, Faer, etc...) - Defaults to **them** if no pronoun is set.
| **VERB** | Read the second note below |

> [!NOTE]
> **{LIVE_STATUS}**
> 
> If the person is currently **LIVE** at the time of the shoutout, then:
> - If their subject pronoun is **they**, or they don't have a pronoun set, then output **they are currently streaming**
> - If their subject pronoun is something other than **they**, then output **she is currently streaming**, where **she** is replaced with whatever their subject pronoun is.
>
> If the person is **NOT LIVE** at the time of the shoutout, then:
> - If their subject pronoun is **they**, or they don't have a pronoun set, then output **they were last streaming**
> - If their subject pronoun is something other than **they**, then output **she was last streaming**, where **she** is replaced with whatever their subject pronoun is.

> [!NOTE]
> **{VERB}**
> 
> If the person is currently **LIVE** at the time of the shoutout, then:
> - If their subject pronoun is **they**, or they don't have a pronoun set, then output **are**
> - If their subject pronoun is something other than **they**, then output **is**
>
> If the person is **NOT LIVE** at the time of the shoutout, then:
> - If their subject pronoun is **they**, or they don't have a pronoun set, then output **were**
> - If their subject pronoun is something other than **they**, then output **was**

---

## Modifiers

| Modifier | Description |
|----------|-------------|
| **:lower** | Changes to all lowercase |
| **:upper** | Changes to all uppercase |
| **:title** | Changes to title case |

For example:
`{GAME:upper}` will be **VALORANT**, or whatever game they were playing, displayed in all uppercase.
*Or* `{GAME:lower}` will be **valorant** in all lowercase.
`{GAME:title}` will be **Dead by Daylight**, while retaining the grammatically correct lowercasing of words like **the**, **and**, **by**, etc...

The default template is **Go show @{STREAMER} some love, {LIVE_STATUS:lower} {GAME:title}!**, which if for example **Amelia** was to get a shoutout:
- If **LIVE**:
  - Subject pronoun is **they**, or no pronoun set, then the output would be **Go show @Amelia some love, they are currently streaming Valorant!**
  - Subject pronoun is not **they**, then the output would be **Go show @Amelia some love, she is currently streaming Valorant!** (Where **she** is replaced with whatever pronoun they have set)
- If **NOT LIVE**:
  - Subject pronoun is **they**, or no pronoun set, then the output would be **Go show @Amelia some love, they were last streaming Valorant!**
  - Subject pronoun is not **they**, then the output would be **Go show @Amelia some love, she was last streaming Valorant!** (Where **she** is replaced with whatever pronoun they have set)
