# Twitch Shoutout with Random Clips for Streamer.bot
This is a **Streamer.bot** action that lets you shout out other Twitch streamers with:

`!shoutout <username>`

Instead of only posting a chat message, it grabs a random clip (up to 30 seconds long) from the streamer and plays it in your OBS scene.

---

## Commands
#### [] = optional, <> = required
- `!shoutout <username> [no-clip / noclip]` - Shouts out the user
   - Aliases: `!so`
   - Paramaters:
     - `<username>` - The username of the person you want to shoutout, with or without mention (@)
     - `[no-clip / noclip]` - Doesn't display a clip for the shoutout
- `!autoshoutout <type> <username>` - Adds a user to the auto shout out list so that they get shouted out on their first message of each stream.
  - Aliases: `!autoshout`, `!as`
  - Paramaters:
    - `<type>` - add, remove, or delete
    - `<username>` - The username of the person you want to add to the list
- `!setcustommessage <username> [template]` - Sets a custom message for the user, which you can spice up with the placeholders and modifiers below.
  - Aliases: `!custommsg`, `!custommessage`, `!setcustommsg`, `!setcustommessage`, `!scm`
  - Parameters:
    - `<username>` - The username of the person you want to give a custom message
    - `[template]` - The custom message you want the user to have
  - Note: `[template]` is optional because if you don't specify a template then it will clear the users custom message and use the default instead.
  
---

## Features
- Shouts out users with the command, on raid, or on their first message on each stream if they are added to the auto shoutout list
- Plays a random clip of the person getting shouted out
- Has pronoun support so you can spice up custom messages with the users pronouns
- Custom message for each user so you can give special people their own shoutout messages

---

## OBS Setup
1. Create a **Media Source** in OBS.  
   (Doesn’t matter what file it points to — Streamer.bot will handle it.)
2. Right-click the source → **Transform** → **Edit Transform**.
3. Change **Bounding Box Type** to `Stretch to bounds`.
4. Set the **Bounding Box Size**:
   - Width (first box) → e.g. `1920.0000`  
   - Height (second box) → e.g. `1080.0000`
5. Place/resize the source where you want clips to show.

That’s all you need to do in OBS.

---

## Streamer.bot Setup
1. Copy everything from `ImportString.txt`.
2. In **Streamer.bot**, click **Import** at the top → paste → click **Import**.
3. Go to **Commands** → find **Ame - Shoutout** → expand → right-click each command (**Auto Shoutout**, **Set Custom Message**, and **Shoutout**) → check **Enabled**.
4. Go to **Actions & Queues** → **Actions** → find **Ame - Shoutout** → expand → click **Shoutout**.
5. Update these global variables under **Sub-Actions** → **Variables** (click the arrow to open folder):
   - `ShoutoutSceneName` → name of the OBS scene with your media source.
   - `ShoutoutSourceName` → name of the OBS media source you made.
6. Leave `ShoutoutHash` and `ShoutoutClientId` alone unless Twitch changes them.

---

## Variables
- `{USER}` - The username of the person **giving** the shoutout
- `{STREAMER}` - The username of the person **receiving** the shoutout
- `{GAME}` - The name of the game that the person receiving the shoutout was playing
- `{TITLE}` - The title of their stream
- `{URL}` - The link to their channel
- `{PRONOUN_SUBJECT}` - The subject pronoun that they have set on `https://pr.alejo.io/` (She/He/Fae/etc...)
- `{PRONOUN_OBJECT}` - The object pronoun that they have set on `https://pr.alejo.io/` (Her/Him/Faer/etc...)
- `{SUBJECT_WASWERE}` - The subject pronoun + was/were, for example, if their subject pronoun is 'they' then it will say 'they were', and if it's 'she' then it will say 'she was'

---

## Modifiers / Default value
Explanation:
Any modifers/default applied to a variable will change how it gets outputted.
Example, if I put {GAME:upper|something awesome}, then it means that **if** the game is found then it will display the game in all uppercase, and if it's not found then it will just say 'something awesome', I prefer to use `:title` for {GAME} because some games like 'Valorant' are set to all uppercase on Twitch, so this ensures only the first character of each word is uppercase to make it more consistent.
- `:upper` - Puts to all uppercase
- `:lower` - Puts to all lowercase
- `:title` - Puts the first character of each word to uppercase
- `:trim` - Removes all leading/trailing spaces so that spacing is exact
- `|<whatever>` - Sets the default value to output if other value isn't found

---

## Notes

- Twitch’s GraphQL API isn’t intended for external use, so things may break if Twitch changes stuff.
- If that happens, you may need to update:
  - `ShoutoutHash`
  - `ShoutoutClientId`

---

## Support

If you run into problems, DM **`eintr`** on Discord.
