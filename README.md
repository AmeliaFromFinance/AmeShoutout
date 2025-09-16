# Twitch Shoutout with Random Clips for Streamer.bot

This is a **Streamer.bot** action that lets you shout out other Twitch streamers with:

`!shoutout <username>`

Instead of only posting a chat message, it grabs a random clip (up to 30 seconds long) from the streamer and plays it in your OBS scene.

---

## Features
- Picks a random clip from the streamer (30s or less)
- Plays directly in OBS through a media source
- Sends a shoutout message in chat (customizable)
- Posts a fallback message if no clips are found
- Uses Twitch’s GraphQL API to get clip playback URLs

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
3. Go to **Commands** → find **Ame - Shoutout** → expand → right-click **Shoutout** → check **Enabled**.
4. Go to **Actions & Queues** → **Actions** → find **Ame - Shoutout** → expand → click **Shoutout**.
5. Update these global variables under **Sub-Actions**:
   - `ShoutoutSceneName` → name of the OBS scene with your media source.
   - `ShoutoutSourceName` → name of the OBS media source you made.
6. Leave `ShoutoutHash` and `ShoutoutClientId` alone unless Twitch changes them.
7. Update the chat messages if you want:
   - **True Result** → what to say if no username is provided.  
   - **False Result** → what to say when shouting someone out (e.g., “Go show %targetUser% some love 💜”).

---

## Common variables to use for the messages

- %user% - The username of the person that typed the command (all lowercase, e.g. ameliafromfinance)
  - %userName% - Same as above but with exact capitalization (e.g. AmeliaFromFinance)
- %targetUser% - The username of the person that is getting shouted out (all lowercase, e.g. ameliafromfinance)
  - %targetUserName% - Same as above but with exact capitalization (e.g. AmeliaFromFinance)
- %game% - The game that they last streamed.
- %targetChannelTitle% - The title of their last stream.
- For URL type manually `https://twitch.tv/%targetUser%`

---

## Usage

Type in Twitch chat:

`!shoutout <username>`

- If the streamer has clips → one will play in OBS + a shoutout message is sent.  
- If no clips are found → only the fallback message is sent.

---

## Technical Notes

- Written as a C# Inline Script for Streamer.bot
- Pulls clip info from Twitch’s GraphQL API (`https://gql.twitch.tv/gql`)
- Picks the best clip quality up to 720p, prefers ~30fps
- Uses Streamer.bot’s OBS WebSocket integration to control the media source

---

## Notes

- Twitch’s GraphQL API isn’t intended for external use, so things may break if Twitch changes stuff.
- If that happens, you may need to update:
  - `ShoutoutHash`
  - `ShoutoutClientId`

---

## Support

If you run into problems, DM **`eintr`** on Discord.
