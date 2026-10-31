using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OrchidUI;
using Twitch.Common.Models.Api;

#if EXTERNAL_EDITOR
public class AmeShoutout : CPHInlineBase
#else
public class CPHInline
#endif
{
    private const string Name = "AmeShoutout";
    private const string Version = "4.0.0";
    private const string LogPrefix = "[AmeShoutout]";
    private const string Website = "https://shoutout.sinist3r.lol/";
    private const string AutoGroup = "Auto Shoutout";
    private const string TemplateVar = "shoutoutTemplate";
    private const string RaidCountVar = "raidCount";
    private const string DefaultTemplate = "Go show @{STREAMER} some love, {LIVE_STATUS:lower} {GAME:title}!";
    private const string DefaultTextTemplate = "Go show {STREAMER} some love!";

    // IDs of the commands in the import. The aliases cover commands the streamer made themselves.
    private const string ShoutoutCommandId = "e351ea77-00b7-45aa-8be6-a42caed2f543";
    private const string AutoCommandId = "a90b8ff9-9e66-4ac3-99b2-f1c6fcf32c0b";
    private const string MessageCommandId = "8acd384e-eecf-4d6c-8cb7-8a9fb4f3b6e1";
    private static readonly string[] ShoutoutAliases = { "!so", "!shoutout" };
    private static readonly string[] AutoAliases = { "!as", "!autoshout", "!autoshoutout" };
    private static readonly string[] MessageAliases = { "!scm", "!setcustommessage", "!setcustommsg", "!custommessage", "!custommsg" };

    private static readonly string[] TypeLabels = { "Chat message", "Twitch shoutout", "Both" };

    // Twitch allows one /shoutout every 2 minutes, and the same streamer once an hour.
    private static readonly TimeSpan TwitchCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TwitchUserCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromHours(1);
    private static readonly TimeSpan RaidOverlap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TeamCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan HashRefresh = TimeSpan.FromHours(1);
    private static readonly TimeSpan HashRetry = TimeSpan.FromMinutes(5);
    private const int ObsSettleMs = 300;

    private const string GqlEndpoint = "https://gql.twitch.tv/gql";
    private const string HashUrl = "https://raw.githubusercontent.com/AmeliaFromFinance/Sapphire/main/hashes.json";
    private const string HashVar = "ameShoutoutHashes";
    private const string ClientIdVar = "ameShoutoutClientId";
    private const string FallbackClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Random Rng = new Random();
    private static readonly object RngLock = new object();
    private static readonly object StateLock = new object();
    private static readonly object HashLock = new object();
    private static readonly object ChatLock = new object();

    private static readonly Dictionary<string, DateTime> LastShoutout = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> TwitchSent = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<PendingShoutout> TwitchQueue = new Queue<PendingShoutout>();
    private static DateTime _lastTwitchShoutout = DateTime.MinValue;
    private static bool _twitchWorker;
    private static Task _playback = Task.FromResult(true);

    private static string _broadcaster;
    private static List<TeamInfo> _myTeams;
    private static DateTime _myTeamsAt = DateTime.MinValue;

    private static string _liveHash = "", _clipHash = "", _accessHash = "", _clientId = "";
    private static DateTime _hashesFetchedAt = DateTime.MinValue, _hashAttemptAt = DateTime.MinValue;
    private static bool _hashesRestored;
    private static Task _hashTask;

    private static readonly Regex TokenRe = new Regex(@"\{(\w+)(?::(\w+))?\}", RegexOptions.Compiled);
    private static readonly Regex SlugRe = new Regex(@"^[A-Za-z0-9_-]{10,100}$", RegexOptions.Compiled);
    private static readonly HashSet<string> SmallWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "and", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "so", "the", "to", "up", "yet" };

    private static class Keys
    {
        public const string Type = "shoutoutType", Template = "template", RaidCount = "raidCount", SharedTeams = "sharedTeams",
            TeamShoutouts = "teamShoutouts", ErrorsInChat = "errorsInChat", ChatBot = "chatBot", ChatBotAction = "chatBotAction",
            TestUser = "testUser", DisplayClips = "displayClips", ClipLength = "clipLength", ClipDays = "clipDays",
            OlderClips = "olderClips", PreferPopular = "preferPopular", Volume = "volume", Scene = "scene", Source = "source",
            TextSource = "textSource", TextTemplate = "textTemplate";
    }

    private enum Reason { Command, Raid, FirstWords, Test }

    private sealed class Config
    {
        public bool SendMessage, SendTwitch, RaidCount, SharedTeams, TeamShoutouts, ErrorsInChat, ChatBot;
        public bool DisplayClips, OlderClips, PreferPopular;
        public string Template, ChatBotAction, Scene, Source, TextSource, TextTemplate;
        public int ClipLength, ClipDays, Volume;
    }

    private sealed class Pronouns
    {
        public string Subject = "they", Object = "them";
        public bool Plural { get { return Subject == "they"; } }
        public string Be(bool now) { return now ? (Plural ? "are" : "is") : (Plural ? "were" : "was"); }
        public string Has { get { return Plural ? "have" : "has"; } }
    }

    private sealed class PendingShoutout
    {
        public string Login, Name;
        public Config Config;
    }

    private sealed class ClipPlayback
    {
        public string Url, Signature, Token;
    }

    public bool Execute()
    {
        // Each run works from its own copy of the arguments.
        Dictionary<string, object> a = args == null ? new Dictionary<string, object>() : new Dictionary<string, object>(args);
        string source = Arg(a, "__source");
        try
        {
            switch (source)
            {
                case "CommandTriggered": return RunCommand(LoadConfig(), a);
                case "TwitchRaid": return Raid(LoadConfig(), a);
                case "TwitchFirstWord": return FirstWords(LoadConfig(), a);
                default: return OpenSettings();
            }
        }
        catch (Exception ex)
        {
            CPH.LogError(LogPrefix + " " + ex);
            return false;
        }
    }

    // Settings window

    private bool OpenSettings()
    {
        if (Orchid.AlreadyOpened(Name)) return true;

        Orchid ui = new Orchid(CPH, Name, Version)
            .SetTheme("Sakura")
            .SetAutoSave(true)
            .SetWindowSize(1100, 760)
            .SetSubtitle("by AmeliaFromFinance")
            .AddLink("Guide", Website);

        Func<OrchidValues, bool> hasMessage = v => v.Get(Keys.Type, TypeLabels[2]) != TypeLabels[1];
        Func<OrchidValues, bool> hasText = v => !string.IsNullOrWhiteSpace(v.Get(Keys.TextSource, ""));

        OrchidTab main = ui.Tab("Shoutouts", Icons.Megaphone);
        main.AddTitle("Message");
        main.AddSegmented("Shoutout type", "A chat message, Twitch's own /shoutout, or both.", Keys.Type, TypeLabels, Array.IndexOf(TypeLabels, LegacyType()))
            .WithIcon(Icons.Chat);
        main.AddResponseBox("Default message", "Used unless the streamer has their own, set with `!scm`.", Keys.Template, LegacyString("ameDefaultTemplate", DefaultTemplate))
            .Required("Add a message, or set the type to Twitch shoutout.")
            .ShowWhen(hasMessage);
        main.AddDescription("Tokens: `{STREAMER}` `{USER}` `{GAME}` `{URL}` `{LIVE_STATUS}` `{SUBJECT}` `{OBJECT}` `{VERB}`\nChange the case with `:lower`, `:upper` or `:title`, like `{GAME:title}`.")
            .ShowWhen(hasMessage);
        main.AddButton("Preview", "Shows the message for an example streamer.", "Preview", () => Preview(ui))
            .WithIcon(Icons.Eye)
            .ShowWhen(hasMessage);
        main.AddToggleSwitch("Raid count", "Adds how many times they've raided you.", Keys.RaidCount, LegacyBool("ameRaidCount", true))
            .WithIcon(Icons.Rocket);
        main.AddToggleSwitch("Shared teams", "Mentions the stream teams you're both in.", Keys.SharedTeams, LegacyBool("ameSharedTeams", true))
            .WithIcon(Icons.People);

        main.AddTitle("Automatic shoutouts");
        main.AddInfoBox("People in the **Auto Shoutout** group get a shoutout with their first message each stream. Add them with `!as add <user>`.");
        main.AddToggleSwitch("Stream team members", "Also gives one to anyone in your stream teams.", Keys.TeamShoutouts, LegacyBool("ameTeamShoutouts", true))
            .WithIcon(Icons.Star);

        main.AddTitle("Chat");
        main.AddToggleSwitch("Problems in chat", "Says in chat when something goes wrong, like no clips found.", Keys.ErrorsInChat, LegacyBool("ameOtherMessages", false))
            .WithIcon(Icons.Warning);
        main.AddToggleSwitch("Send through an action", "For chat bots. The action gets the text as `%message%`.", Keys.ChatBot, LegacyBool("ameChatBot", false))
            .WithIcon(Icons.Robot);
        main.AddTextbox("Action name", "", Keys.ChatBotAction, "[ATB] Send Message")
            .Required("Enter the action that sends chat messages.")
            .ShowWhen(Keys.ChatBot, true);

        main.AddTitle("Try it");
        main.AddTextbox("Streamer", "Their Twitch name.", Keys.TestUser, "", "ameliafromfinance")
            .WithIcon(Icons.Person);
        main.AddButton("Test shoutout", "Runs a real shoutout with these settings.", "Send", () => TestShoutout(ui), true)
            .WithIcon(Icons.Play);

        OrchidTab clips = ui.Tab("Clips", Icons.Video);
        clips.AddTitle("Clips");
        clips.AddToggleSwitch("Show clips", "Plays a clip from the streamer in OBS.", Keys.DisplayClips, LegacyBool("ameDisplayClips", true))
            .WithIcon(Icons.Play);
        clips.AddSlider("Max length", "Longer clips are skipped.", Keys.ClipLength, 5, 60, Clamp(LegacyInt("ameClipLength", 30), 5, 60))
            .WithFormat(v => v + " s")
            .WithIcon(Icons.Timer)
            .EnableWhen(Keys.DisplayClips, true);
        clips.AddSlider("Max age", "Only clips from this many days back.", Keys.ClipDays, 1, 365, Clamp(LegacyInt("ameClipDays", 30), 1, 365))
            .WithFormat(v => v == 1 ? "1 day" : v + " days")
            .WithIcon(Icons.Calendar)
            .EnableWhen(Keys.DisplayClips, true);
        clips.AddToggleSwitch("Older clips as backup", "If none are recent enough, use any clip that's short enough.", Keys.OlderClips, true)
            .WithIcon(Icons.Refresh)
            .EnableWhen(Keys.DisplayClips, true);
        clips.AddToggleSwitch("Prefer popular", "Clips with more views are picked more often.", Keys.PreferPopular, LegacyBool("amePreferPopular", false))
            .WithIcon(Icons.Heart)
            .EnableWhen(Keys.DisplayClips, true);
        clips.AddSlider("Volume", "", Keys.Volume, 0, 100, 20)
            .WithFormat(v => v + "%")
            .WithIcon(Icons.Volume)
            .EnableWhen(Keys.DisplayClips, true);

        clips.AddTitle("OBS");
        clips.AddTextbox("Scene", "Add this scene as a source in your main scenes.", Keys.Scene, LegacyString("ameSceneName", "AmeShoutout"))
            .Required("Enter a scene name.")
            .WithIcon(Icons.Video);
        clips.AddTextbox("Media source", "The clip plays here.", Keys.Source, LegacyString("ameSourceName", "ClipsDisplay"))
            .Required("Enter a source name.")
            .WithIcon(Icons.Play);
        clips.AddTextbox("Text source", "Optional. A text source in the same scene, shown with the clip.", Keys.TextSource, LegacyTextSource(), "None")
            .WithIcon(Icons.Font);
        clips.AddResponseBox("Text", "Same tokens as the message.", Keys.TextTemplate, LegacyString("ameTextTemplate", DefaultTextTemplate))
            .ShowWhen(hasText);
        clips.AddButton("Set up OBS", "Creates the scene and media source, or fixes them if they exist.", "Set up", () => SetUpObsFromWindow(ui), true)
            .WithIcon(Icons.Wrench);

        ui.ShowUI();
        return true;
    }

    private void Preview(Orchid ui)
    {
        Pronouns pn = new Pronouns { Subject = "she", Object = "her" };
        Dictionary<string, Func<string>> tokens = Tokens("YourMod", "Amelia", "ameliafromfinance", "Dead by Daylight", pn, true);
        string text = Render(ui.GetPendingValue(Keys.Template, DefaultTemplate), tokens);
        ui.ShowMessage("Preview", text + "\n\nExample: **Amelia**, she/her, live playing Dead by Daylight.");
    }

    private void TestShoutout(Orchid ui)
    {
        string login = CleanLogin(ui.GetPendingValue(Keys.TestUser, ""));
        if (login.Length == 0)
        {
            ui.ShowToast("Type a streamer's name first.", InfoKind.Warning);
            return;
        }
        ui.ShowToast("Sending a shoutout to " + login + "…");
        Task.Run(() =>
        {
            try
            {
                bool ok = Shoutout(LoadConfig(), login, Reason.Test, null, null, false, BroadcasterName());
                ui.ShowToast(ok ? "Shoutout sent." : "That didn't work. The Streamer.bot log says why.", ok ? InfoKind.Success : InfoKind.Danger);
            }
            catch (Exception ex)
            {
                CPH.LogError(LogPrefix + " Test shoutout failed: " + ex);
                ui.ShowToast("That didn't work. The Streamer.bot log says why.", InfoKind.Danger);
            }
        });
    }

    private void SetUpObsFromWindow(Orchid ui)
    {
        string problem = SetUpObs(
            ui.GetPendingValue(Keys.Scene, "AmeShoutout").Trim(),
            ui.GetPendingValue(Keys.Source, "ClipsDisplay").Trim(),
            ui.GetPendingValue(Keys.Volume, 20));
        if (problem == null) ui.ShowToast("OBS is set up.", InfoKind.Success);
        else ui.ShowMessage("OBS setup", problem);
    }

    // Commands

    private bool RunCommand(Config cfg, Dictionary<string, object> a)
    {
        string id = Arg(a, "commandId") ?? "";
        string command = (Arg(a, "command") ?? "").Trim().ToLowerInvariant();
        string msgId = Arg(a, "msgId");

        if (SameId(id, ShoutoutCommandId) || ShoutoutAliases.Contains(command))
        {
            string login = CleanLogin(Arg(a, "input0"));
            if (login.Length == 0) return Reply(cfg, msgId, "Who should I shout out? Use " + Alias(command, "!so") + " <user>.");
            string extra = Arg(a, "input1");
            bool noClip = string.Equals(extra, "no", StringComparison.OrdinalIgnoreCase);
            return Shoutout(cfg, login, Reason.Command, msgId, noClip ? null : extra, noClip, Arg(a, "user"));
        }
        if (SameId(id, AutoCommandId) || AutoAliases.Contains(command)) return AutoShoutoutCommand(cfg, a, msgId, Alias(command, "!as"));
        if (SameId(id, MessageCommandId) || MessageAliases.Contains(command)) return CustomMessageCommand(cfg, a, msgId, Alias(command, "!scm"));

        CPH.LogWarn(LogPrefix + " Unknown command '" + command + "'.");
        return false;
    }

    private bool AutoShoutoutCommand(Config cfg, Dictionary<string, object> a, string msgId, string command)
    {
        string verb = (Arg(a, "input0") ?? "").Trim().ToLowerInvariant();
        string login = CleanLogin(Arg(a, "input1"));
        string usage = "Use " + command + " add <user> or " + command + " remove <user>.";
        bool add = verb == "add";
        bool remove = verb == "remove" || verb == "delete" || verb == "del";
        if ((!add && !remove) || login.Length == 0) return Reply(cfg, msgId, usage);

        TwitchUserInfoEx user = CPH.TwitchGetExtendedUserInfoByLogin(login);
        if (user == null) return Reply(cfg, msgId, "I couldn't find a Twitch user called " + login + ".");

        EnsureAutoGroup();
        string name = user.UserName;
        bool inGroup = CPH.UserInGroup(user.UserLogin, Platform.Twitch, AutoGroup);
        if (add && inGroup) return Reply(cfg, msgId, name + " is already on the auto shoutout list.");
        if (remove && !inGroup) return Reply(cfg, msgId, name + " isn't on the auto shoutout list.");

        bool ok = add
            ? CPH.AddUserToGroup(user.UserLogin, Platform.Twitch, AutoGroup)
            : CPH.RemoveUserFromGroup(user.UserLogin, Platform.Twitch, AutoGroup);
        if (!ok)
        {
            Reply(cfg, msgId, "Streamer.bot couldn't update the " + AutoGroup + " group.");
            return false;
        }
        return Reply(cfg, msgId, name + (add ? " has been added to" : " has been removed from") + " the auto shoutout list.");
    }

    private bool CustomMessageCommand(Config cfg, Dictionary<string, object> a, string msgId, string command)
    {
        string login = CleanLogin(Arg(a, "input0"));
        if (login.Length == 0) return Reply(cfg, msgId, "Use " + command + " <user> <message>, or leave the message out to go back to the default.");

        TwitchUserInfoEx user = CPH.TwitchGetExtendedUserInfoByLogin(login);
        if (user == null) return Reply(cfg, msgId, "I couldn't find a Twitch user called " + login + ".");

        string template = AfterFirstWord(Arg(a, "rawInput"));
        CPH.SetTwitchUserVar(user.UserLogin, TemplateVar, template, true);
        return Reply(cfg, msgId, template.Length == 0
            ? user.UserName + "'s shoutout is back to the default message."
            : user.UserName + "'s shoutout message is now: " + template);
    }

    // First words and shoutouts

    private bool FirstWords(Config cfg, Dictionary<string, object> a)
    {
        string login = (Arg(a, "userName") ?? Arg(a, "user") ?? "").Trim();
        if (login.Length == 0 || login.Equals(BroadcasterLogin(), StringComparison.OrdinalIgnoreCase)) return true;

        string message = (Arg(a, "message") ?? Arg(a, "rawInput") ?? "").TrimStart();
        string first = message.Split(' ')[0].ToLowerInvariant();
        if (ShoutoutAliases.Contains(first)) return true;
        if (ShoutedRecently(login)) return true;

        bool listed = CPH.UserInGroup(login, Platform.Twitch, AutoGroup);
        if (!listed && !(cfg.TeamShoutouts && SharedTeams(login).Count > 0)) return true;
        // Claimed in one step, so a raid landing at the same moment can't double it.
        if (!Claim(login, RepeatGuard)) return true;
        return Shoutout(cfg, login, Reason.FirstWords, null, null, false, Arg(a, "user"));
    }

    private bool Raid(Config cfg, Dictionary<string, object> a)
    {
        string login = CleanLogin(Arg(a, "userName") ?? Arg(a, "user"));
        // Skipped only if their first message beat the raid by a few seconds.
        if (!Claim(login, RaidOverlap)) return true;
        return Shoutout(cfg, login, Reason.Raid, null, null, false, Arg(a, "user"));
    }

    private bool Shoutout(Config cfg, string login, Reason reason, string msgId, string clipArg, bool noClip, string giver)
    {
        return Task.Run(() => ShoutoutAsync(cfg, CleanLogin(login), reason, msgId, clipArg, noClip, giver ?? "")).GetAwaiter().GetResult();
    }

    private async Task<bool> ShoutoutAsync(Config cfg, string login, Reason reason, string msgId, string clipArg, bool noClip, string giver)
    {
        TwitchUserInfoEx target = login.Length == 0 ? null : CPH.TwitchGetExtendedUserInfoByLogin(login);
        if (target == null)
        {
            if (reason == Reason.Command)
            {
                Reply(cfg, msgId, "I couldn't find a Twitch user called " + login + ".");
                return false;
            }
            return Fail(cfg, "Couldn't find the Twitch user '" + login + "'.");
        }

        string targetLogin = string.IsNullOrEmpty(target.UserLogin) ? login.ToLowerInvariant() : target.UserLogin;
        string name = string.IsNullOrEmpty(target.UserName) ? targetLogin : target.UserName;
        lock (StateLock) LastShoutout[targetLogin] = DateTime.UtcNow;

        Task hashes = EnsureHashesAsync();
        int raids = reason == Reason.Raid ? CountRaid(targetLogin) : 0;
        List<string> teams = cfg.SharedTeams ? SharedTeams(targetLogin) : new List<string>();
        Pronouns pn = LookupPronouns(targetLogin);
        await hashes.ConfigureAwait(false);
        bool live = await IsLiveAsync(targetLogin).ConfigureAwait(false);

        Dictionary<string, Func<string>> tokens = Tokens(giver, name, targetLogin, target.Game, pn, live);

        if (cfg.SendMessage)
        {
            string custom = CPH.GetTwitchUserVar<string>(targetLogin, TemplateVar, true);
            string message = Render(string.IsNullOrWhiteSpace(custom) ? cfg.Template : custom, tokens);
            if (raids > 0 && cfg.RaidCount)
                message += " (" + Capitalize(pn.Subject) + " " + pn.Has + " raided " + raids + " time" + (raids == 1 ? "" : "s") + "!)";
            message += TeamsSuffix(teams, pn);
            if (!string.IsNullOrWhiteSpace(message)) SendChat(cfg, message);
        }
        if (cfg.SendTwitch) QueueTwitchShoutout(cfg, targetLogin, name);

        if (noClip || !cfg.DisplayClips) return true;
        return await PlayClipAsync(cfg, targetLogin, name, clipArg, tokens).ConfigureAwait(false);
    }

    private static Dictionary<string, Func<string>> Tokens(string giver, string name, string login, string game, Pronouns pn, bool live)
    {
        return new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "STREAMER", () => name },
            { "USER", () => giver },
            { "GAME", () => game ?? "" },
            { "URL", () => "https://twitch.tv/" + login.ToLowerInvariant() },
            { "LIVE_STATUS", () => pn.Subject + " " + pn.Be(live) + " " + (live ? "currently" : "last") + " streaming" },
            { "SUBJECT", () => pn.Subject },
            { "OBJECT", () => pn.Object },
            { "VERB", () => pn.Be(live) },
        };
    }

    private int CountRaid(string login)
    {
        lock (StateLock)
        {
            int count = CPH.GetTwitchUserVar<int>(login, RaidCountVar, true) + 1;
            CPH.SetTwitchUserVar(login, RaidCountVar, count, true);
            return count;
        }
    }

    private static bool Claim(string login, TimeSpan window)
    {
        lock (StateLock)
        {
            DateTime at;
            if (LastShoutout.TryGetValue(login, out at) && DateTime.UtcNow - at < window) return false;
            LastShoutout[login] = DateTime.UtcNow;
            return true;
        }
    }

    private static bool ShoutedRecently(string login)
    {
        lock (StateLock)
        {
            DateTime at;
            return LastShoutout.TryGetValue(login, out at) && DateTime.UtcNow - at < RepeatGuard;
        }
    }

    private Pronouns LookupPronouns(string login)
    {
        Pronouns result = new Pronouns();
        try
        {
            Dictionary<string, object> p = CPH.PronounLookup(login);
            object subject, obj;
            if (p != null && p.TryGetValue("pronounSubject", out subject) && subject is string && ((string)subject).Length > 0)
                result.Subject = ((string)subject).ToLowerInvariant();
            if (p != null && p.TryGetValue("pronounObject", out obj) && obj is string && ((string)obj).Length > 0)
                result.Object = ((string)obj).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            CPH.LogWarn(LogPrefix + " Pronoun lookup failed for " + login + ": " + ex.Message);
        }
        // "Any" and "Other" aren't pronouns to use in a sentence.
        if (result.Subject == "any" || result.Subject == "other")
        {
            result.Subject = "they";
            result.Object = "them";
        }
        return result;
    }

    // Twitch /shoutout queue: one at a time, two minutes apart, never the same streamer twice in an hour.

    private void QueueTwitchShoutout(Config cfg, string login, string name)
    {
        TimeSpan wait;
        lock (StateLock)
        {
            DateTime sent;
            if (TwitchSent.TryGetValue(login, out sent) && DateTime.UtcNow - sent < TwitchUserCooldown)
            {
                CPH.LogInfo(LogPrefix + " Skipped the Twitch shoutout for " + name + ": they had one in the last hour.");
                return;
            }
            if (TwitchQueue.Any(p => p.Login.Equals(login, StringComparison.OrdinalIgnoreCase))) return;

            TimeSpan sinceLast = DateTime.UtcNow - _lastTwitchShoutout;
            wait = TwitchCooldown - sinceLast;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            if (TwitchQueue.Count > 0 || wait > TimeSpan.Zero)
                wait += TimeSpan.FromTicks(TwitchCooldown.Ticks * TwitchQueue.Count);

            TwitchQueue.Enqueue(new PendingShoutout { Login = login, Name = name, Config = cfg });
            if (!_twitchWorker)
            {
                _twitchWorker = true;
                Task.Run(() => TwitchWorkerAsync());
            }
        }
        if (wait > TimeSpan.FromSeconds(1))
            SendChat(cfg, "Twitch shoutout for " + name + " in " + Humanize(wait) + ".");
    }

    private async Task TwitchWorkerAsync()
    {
        while (true)
        {
            PendingShoutout next = null;
            TimeSpan wait;
            lock (StateLock)
            {
                if (TwitchQueue.Count == 0)
                {
                    _twitchWorker = false;
                    return;
                }
                wait = TwitchCooldown - (DateTime.UtcNow - _lastTwitchShoutout);
                if (wait <= TimeSpan.Zero) next = TwitchQueue.Dequeue();
            }
            if (next == null)
            {
                await Task.Delay(wait + TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (CPH.TwitchSendShoutoutByLogin(next.Login.ToLowerInvariant()))
                {
                    lock (StateLock)
                    {
                        _lastTwitchShoutout = DateTime.UtcNow;
                        TwitchSent[next.Login] = _lastTwitchShoutout;
                    }
                }
                else Fail(next.Config, "Twitch didn't accept the shoutout for " + next.Name + ". It only works while you're live.");
            }
            catch (Exception ex)
            {
                CPH.LogError(LogPrefix + " Twitch shoutout for " + next.Name + " failed: " + ex.Message);
            }
        }
    }

    // Stream teams

    private List<string> SharedTeams(string login)
    {
        string me = BroadcasterLogin();
        if (me.Length == 0 || me.Equals(login, StringComparison.OrdinalIgnoreCase)) return new List<string>();

        List<TeamInfo> mine = MyTeams(me);
        if (mine.Count == 0) return new List<string>();

        List<TeamInfo> theirs;
        try { theirs = CPH.GetTeamInfoByLogin(login) ?? new List<TeamInfo>(); }
        catch (Exception) { theirs = new List<TeamInfo>(); }

        HashSet<string> myIds = new HashSet<string>(mine.Where(t => t != null && !string.IsNullOrEmpty(t.TeamId)).Select(t => t.TeamId), StringComparer.Ordinal);
        return theirs
            .Where(t => t != null && !string.IsNullOrEmpty(t.TeamId) && myIds.Contains(t.TeamId))
            .Select(t => string.IsNullOrEmpty(t.TeamDisplayName) ? t.TeamName : t.TeamDisplayName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<TeamInfo> MyTeams(string me)
    {
        lock (StateLock)
        {
            if (_myTeams != null && DateTime.UtcNow - _myTeamsAt < TeamCacheTtl) return _myTeams;
        }
        List<TeamInfo> teams;
        try { teams = CPH.GetTeamInfoByLogin(me); }
        catch (Exception) { teams = null; }
        if (teams == null) return new List<TeamInfo>();
        lock (StateLock)
        {
            _myTeams = teams;
            _myTeamsAt = DateTime.UtcNow;
        }
        return teams;
    }

    private static string TeamsSuffix(List<string> teams, Pronouns pn)
    {
        if (teams.Count == 0) return "";
        string names = teams.Count == 1 ? teams[0] : string.Join(", ", teams.Take(teams.Count - 1)) + " and " + teams[teams.Count - 1];
        return " " + Capitalize(pn.Subject) + " " + pn.Be(true) + " in the team" + (teams.Count == 1 ? "" : "s") + " " + names + " with me!";
    }

    private string BroadcasterLogin()
    {
        if (_broadcaster == null)
        {
            try
            {
                var me = CPH.TwitchGetBroadcaster();
                if (me != null && !string.IsNullOrEmpty(me.UserLogin)) _broadcaster = me.UserLogin;
            }
            catch (Exception) { }
        }
        return _broadcaster ?? "";
    }

    private string BroadcasterName()
    {
        try
        {
            var me = CPH.TwitchGetBroadcaster();
            if (me != null && !string.IsNullOrEmpty(me.UserName)) return me.UserName;
        }
        catch (Exception) { }
        return BroadcasterLogin();
    }

    // Clips

    private async Task<bool> PlayClipAsync(Config cfg, string login, string name, string clipArg, Dictionary<string, Func<string>> tokens)
    {
        if (!CPH.ObsIsConnected()) return Fail(cfg, "OBS isn't connected, so the clip for " + name + " was skipped.");

        string slug = ExtractSlug(clipArg);
        double seconds = 0;
        ClipPlayback playback = null;
        if (slug.Length > 0)
        {
            playback = await PlaybackAsync(slug).ConfigureAwait(false);
            if (playback != null)
            {
                double duration = await ClipDurationAsync(slug).ConfigureAwait(false);
                seconds = duration > 0 ? duration : cfg.ClipLength;
            }
            else CPH.LogWarn(LogPrefix + " Couldn't find the clip '" + slug + "', so a random one is used.");
        }
        if (playback == null)
        {
            ClipData clip = PickClip(cfg, login);
            if (clip == null) return Fail(cfg, "No clips found for " + name + ".");
            slug = clip.Id;
            seconds = clip.Duration;
            playback = await PlaybackAsync(slug).ConfigureAwait(false);
            if (playback == null) return Fail(cfg, "Couldn't load the clip for " + name + ".");
        }

        string url = playback.Url + "?token=" + Uri.EscapeDataString(playback.Token) + "&sig=" + Uri.EscapeDataString(playback.Signature);
        string text = string.IsNullOrWhiteSpace(cfg.TextSource) ? null : Render(cfg.TextTemplate, tokens);

        // Clips play one after another in the background, so the next trigger doesn't wait for this one.
        lock (StateLock)
        {
            _playback = _playback.ContinueWith(t => ShowClip(cfg, name, slug, url, seconds, text), TaskScheduler.Default);
        }
        return true;
    }

    private void ShowClip(Config cfg, string name, string slug, string url, double seconds, string text)
    {
        try
        {
            CPH.LogInfo(LogPrefix + " Playing clip " + slug + " for " + name + " (" + seconds.ToString("0.#", CultureInfo.InvariantCulture) + "s).");
            CPH.ObsSetSourceVisibility(cfg.Scene, cfg.Source, false);
            CPH.ObsSetMediaSourceFile(cfg.Scene, cfg.Source, url);
            ObsRaw("SetInputVolume", new JObject { ["inputName"] = cfg.Source, ["inputVolumeMul"] = cfg.Volume / 100.0 });
            if (text != null) CPH.ObsSetGdiText(cfg.Scene, cfg.TextSource, text);
            CPH.Wait(ObsSettleMs);
            CPH.ObsSetSourceVisibility(cfg.Scene, cfg.Source, true);
            if (text != null) CPH.ObsSetSourceVisibility(cfg.Scene, cfg.TextSource, true);
            CPH.Wait((int)((seconds + 1) * 1000));
            CPH.ObsSetSourceVisibility(cfg.Scene, cfg.Source, false);
            if (text != null) CPH.ObsSetSourceVisibility(cfg.Scene, cfg.TextSource, false);
            CPH.ObsSetMediaSourceFile(cfg.Scene, cfg.Source, "");
        }
        catch (Exception ex)
        {
            CPH.LogError(LogPrefix + " Clip for " + name + " failed: " + ex.Message);
        }
    }

    private ClipData PickClip(Config cfg, string login)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-cfg.ClipDays);
        List<ClipData> pool = Usable(Clips(() => CPH.GetClipsForUser(login, TimeSpan.FromDays(cfg.ClipDays), (bool?)null)), cfg)
            .Where(c => c.CreatedAt >= cutoff)
            .ToList();
        if (pool.Count == 0 && cfg.OlderClips)
            pool = Usable(Clips(() => CPH.GetClipsForUser(login, 100, (bool?)null)), cfg).ToList();
        if (pool.Count == 0) return null;

        lock (RngLock)
        {
            if (!cfg.PreferPopular) return pool[Rng.Next(pool.Count)];
            double roll = Rng.NextDouble() * pool.Sum(c => c.ViewCount + 1.0);
            foreach (ClipData clip in pool)
            {
                roll -= clip.ViewCount + 1.0;
                if (roll <= 0) return clip;
            }
            return pool[pool.Count - 1];
        }
    }

    private List<ClipData> Clips(Func<List<ClipData>> fetch)
    {
        try { return fetch() ?? new List<ClipData>(); }
        catch (Exception ex)
        {
            CPH.LogWarn(LogPrefix + " Couldn't get clips: " + ex.Message);
            return new List<ClipData>();
        }
    }

    private static IEnumerable<ClipData> Usable(IEnumerable<ClipData> clips, Config cfg)
    {
        return clips.Where(c => c != null && !string.IsNullOrEmpty(c.Id) && c.Duration > 0 && c.Duration <= cfg.ClipLength);
    }

    private static string ExtractSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string text = input.Trim().Trim('"', '\'', '<', '>');
        if (SlugRe.IsMatch(text)) return text;
        if (!text.Contains("://")) text = "https://" + text;
        Uri uri;
        if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return "";
        if (!uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase)) return "";
        Match query = Regex.Match(uri.Query, @"[?&]clip=([^&]+)");
        if (query.Success && SlugRe.IsMatch(Uri.UnescapeDataString(query.Groups[1].Value))) return Uri.UnescapeDataString(query.Groups[1].Value);
        string last = uri.AbsolutePath.Trim('/').Split('/').Last();
        return SlugRe.IsMatch(last) ? last : "";
    }

    // Twitch GQL: clip playback links and live status

    private async Task<ClipPlayback> PlaybackAsync(string slug)
    {
        JObject response = await GqlAsync("VideoAccessToken_Clip", _accessHash, new JObject { ["slug"] = slug, ["platform"] = "web" }).ConfigureAwait(false);
        JObject clip = response == null ? null : response.SelectToken("data.clip") as JObject;
        JArray qualities = clip == null ? null : clip["videoQualities"] as JArray;
        if (qualities == null) return null;

        string url = qualities
            .Select(q => new { Url = (string)q["sourceURL"], Quality = ToDouble(q["quality"]), Fps = ToDouble(q["frameRate"]) })
            .Where(q => !string.IsNullOrEmpty(q.Url))
            .OrderByDescending(q => q.Quality).ThenByDescending(q => q.Fps)
            .Select(q => q.Url)
            .FirstOrDefault();
        string sig = (string)clip.SelectToken("playbackAccessToken.signature");
        string token = (string)clip.SelectToken("playbackAccessToken.value");
        if (url == null || sig == null || token == null) return null;
        return new ClipPlayback { Url = url, Signature = sig, Token = token };
    }

    private async Task<double> ClipDurationAsync(string slug)
    {
        JObject response = await GqlAsync("ChatClip", _clipHash, new JObject { ["clipSlug"] = slug }).ConfigureAwait(false);
        return response == null ? 0 : ToDouble(response.SelectToken("data.clip.durationSeconds"));
    }

    private async Task<bool> IsLiveAsync(string login)
    {
        JObject response = await GqlAsync("UseLive", _liveHash, new JObject { ["channelLogin"] = login }).ConfigureAwait(false);
        return response != null && response.SelectToken("data.user.stream") is JObject;
    }

    private async Task<JObject> GqlAsync(string operation, string hash, JObject variables)
    {
        if (string.IsNullOrEmpty(hash))
        {
            CPH.LogWarn(LogPrefix + " No query hash for '" + operation + "' yet.");
            return null;
        }
        try
        {
            JObject body = new JObject
            {
                ["operationName"] = operation,
                ["variables"] = variables,
                ["extensions"] = new JObject { ["persistedQuery"] = new JObject { ["version"] = 1, ["sha256Hash"] = hash } },
            };
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, GqlEndpoint))
            {
                request.Headers.TryAddWithoutValidation("Client-ID", string.IsNullOrEmpty(_clientId) ? FallbackClientId : _clientId);
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        CPH.LogWarn(LogPrefix + " Twitch GQL '" + operation + "' returned " + (int)response.StatusCode + ".");
                        return null;
                    }
                    JToken json = JToken.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    JArray array = json as JArray;
                    return (array != null ? array.FirstOrDefault() : json) as JObject;
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn(LogPrefix + " Twitch GQL '" + operation + "' failed: " + ex.Message);
            return null;
        }
    }

    // GQL query hashes change now and then, so they're fetched from GitHub and kept in two global variables.

    private Task EnsureHashesAsync()
    {
        lock (HashLock)
        {
            if (!_hashesRestored)
            {
                _hashesRestored = true;
                RestoreHashes();
            }
            DateTime now = DateTime.UtcNow;
            bool due = now - _hashesFetchedAt >= HashRefresh && now - _hashAttemptAt >= HashRetry;
            if (due && (_hashTask == null || _hashTask.IsCompleted))
            {
                _hashAttemptAt = now;
                _hashTask = Task.Run(() => FetchHashesAsync());
            }
            if (HashesLoaded || _hashTask == null) return Task.FromResult(true);
            return _hashTask;
        }
    }

    private static bool HashesLoaded
    {
        get { return _accessHash.Length > 0 && _clipHash.Length > 0 && _liveHash.Length > 0; }
    }

    private void RestoreHashes()
    {
        try
        {
            string id = CPH.GetGlobalVar<string>(ClientIdVar, true);
            if (!string.IsNullOrEmpty(id)) _clientId = id;
            string stored = CPH.GetGlobalVar<string>(HashVar, true);
            if (string.IsNullOrEmpty(stored)) return;
            JObject json = JObject.Parse(stored);
            JObject hashes = json["hashes"] as JObject;
            if (hashes != null) ApplyHashes(hashes);
            DateTime at;
            if (DateTime.TryParse((string)json["fetchedAt"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at))
                _hashesFetchedAt = at;
        }
        catch (Exception ex)
        {
            CPH.LogWarn(LogPrefix + " Stored query hashes were unreadable: " + ex.Message);
        }
    }

    private async Task FetchHashesAsync()
    {
        try
        {
            using (HttpResponseMessage response = await Http.GetAsync(HashUrl).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    CPH.LogWarn(LogPrefix + " Query hashes returned " + (int)response.StatusCode + ".");
                    return;
                }
                JObject data = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                string id = (string)data["ClientId"];
                data.Remove("ClientId");
                lock (HashLock)
                {
                    if (!string.IsNullOrEmpty(id)) _clientId = id;
                    ApplyHashes(data);
                    _hashesFetchedAt = DateTime.UtcNow;
                }
                if (!string.IsNullOrEmpty(id)) CPH.SetGlobalVar(ClientIdVar, id, true);
                CPH.SetGlobalVar(HashVar, new JObject { ["fetchedAt"] = DateTime.UtcNow.ToString("o"), ["hashes"] = data }.ToString(Formatting.None), true);
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn(LogPrefix + " Couldn't fetch query hashes: " + ex.Message);
        }
    }

    private static void ApplyHashes(JObject data)
    {
        _accessHash = (string)data["VideoAccessToken_Clip"] ?? _accessHash;
        _clipHash = (string)data["ChatClip"] ?? _clipHash;
        _liveHash = (string)data["UseLive"] ?? _liveHash;
    }

    // OBS setup

    private string SetUpObs(string scene, string source, int volume)
    {
        if (scene.Length == 0 || source.Length == 0) return "Enter a scene and a media source name first.";
        if (!CPH.ObsIsConnected()) return "OBS isn't connected. Connect it in Streamer.bot (Stream Apps > OBS), then try again.";

        ObsRaw("CreateScene", new JObject { ["sceneName"] = scene });
        JObject created = ObsRaw("CreateInput", new JObject
        {
            ["sceneName"] = scene,
            ["inputName"] = source,
            ["inputKind"] = "ffmpeg_source",
            ["sceneItemEnabled"] = false,
            ["inputSettings"] = new JObject
            {
                ["is_local_file"] = false,
                ["input"] = "",
                ["buffering_mb"] = 10,
                ["restart_on_activate"] = true,
                ["hw_decode"] = true,
                ["clear_on_media_end"] = true,
                ["close_when_inactive"] = true,
            },
        });

        int itemId = ToInt(created, "sceneItemId");
        if (itemId <= 0) itemId = ToInt(ObsRaw("GetSceneItemId", new JObject { ["sceneName"] = scene, ["sourceName"] = source }), "sceneItemId");
        if (itemId <= 0) itemId = ToInt(ObsRaw("CreateSceneItem", new JObject { ["sceneName"] = scene, ["sourceName"] = source, ["sceneItemEnabled"] = false }), "sceneItemId");
        if (itemId <= 0) return "Couldn't add '" + source + "' to the '" + scene + "' scene. If '" + source + "' is already used for something else, pick another name.";

        JObject video = ObsRaw("GetVideoSettings", new JObject());
        int width = ToInt(video, "baseWidth"), height = ToInt(video, "baseHeight");
        if (width <= 0 || height <= 0) { width = 1920; height = 1080; }

        ObsRaw("SetSceneItemTransform", new JObject
        {
            ["sceneName"] = scene,
            ["sceneItemId"] = itemId,
            ["sceneItemTransform"] = new JObject
            {
                ["boundsType"] = "OBS_BOUNDS_SCALE_INNER",
                ["boundsWidth"] = width,
                ["boundsHeight"] = height,
                ["boundsAlignment"] = 0,
                ["alignment"] = 0,
                ["positionX"] = width / 2.0,
                ["positionY"] = height / 2.0,
            },
        });
        ObsRaw("SetInputVolume", new JObject { ["inputName"] = source, ["inputVolumeMul"] = Clamp(volume, 0, 100) / 100.0 });
        ObsRaw("SetInputAudioMonitorType", new JObject { ["inputName"] = source, ["monitorType"] = "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" });
        CPH.LogInfo(LogPrefix + " OBS set up: scene '" + scene + "', media source '" + source + "' (item " + itemId + ").");
        return null;
    }

    private JObject ObsRaw(string request, JObject data)
    {
        try
        {
            string response = CPH.ObsSendRaw(request, data.ToString(Formatting.None));
            if (string.IsNullOrWhiteSpace(response)) return null;
            return JToken.Parse(response) as JObject;
        }
        catch (Exception ex)
        {
            CPH.LogDebug(LogPrefix + " OBS " + request + ": " + ex.Message);
            return null;
        }
    }

    // Settings

    private Config LoadConfig()
    {
        OrchidSettings s = OrchidSettings.Load(CPH, Name);
        string type = s.GetString(Keys.Type, LegacyType());
        return new Config
        {
            SendMessage = type != TypeLabels[1],
            SendTwitch = type != TypeLabels[0],
            Template = s.GetString(Keys.Template, LegacyString("ameDefaultTemplate", DefaultTemplate)),
            RaidCount = s.GetBool(Keys.RaidCount, LegacyBool("ameRaidCount", true)),
            SharedTeams = s.GetBool(Keys.SharedTeams, LegacyBool("ameSharedTeams", true)),
            TeamShoutouts = s.GetBool(Keys.TeamShoutouts, LegacyBool("ameTeamShoutouts", true)),
            ErrorsInChat = s.GetBool(Keys.ErrorsInChat, LegacyBool("ameOtherMessages", false)),
            ChatBot = s.GetBool(Keys.ChatBot, LegacyBool("ameChatBot", false)),
            ChatBotAction = s.GetString(Keys.ChatBotAction, "[ATB] Send Message"),
            DisplayClips = s.GetBool(Keys.DisplayClips, LegacyBool("ameDisplayClips", true)),
            ClipLength = Clamp(s.GetInt(Keys.ClipLength, LegacyInt("ameClipLength", 30)), 5, 60),
            ClipDays = Clamp(s.GetInt(Keys.ClipDays, LegacyInt("ameClipDays", 30)), 1, 365),
            OlderClips = s.GetBool(Keys.OlderClips, true),
            PreferPopular = s.GetBool(Keys.PreferPopular, LegacyBool("amePreferPopular", false)),
            Volume = Clamp(s.GetInt(Keys.Volume, 20), 0, 100),
            Scene = s.GetString(Keys.Scene, LegacyString("ameSceneName", "AmeShoutout")).Trim(),
            Source = s.GetString(Keys.Source, LegacyString("ameSourceName", "ClipsDisplay")).Trim(),
            TextSource = s.GetString(Keys.TextSource, LegacyTextSource()).Trim(),
            TextTemplate = s.GetString(Keys.TextTemplate, LegacyString("ameTextTemplate", DefaultTextTemplate)),
        };
    }

    // Settings from version 3 (the web configurator) are used until the window has saved its own.

    private object Legacy(string name)
    {
        try { return CPH.GetGlobalVar<object>(name, true); }
        catch (Exception) { return null; }
    }

    private string LegacyString(string name, string fallback)
    {
        object v = Legacy(name);
        string s = v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(s) ? fallback : s;
    }

    private bool LegacyBool(string name, bool fallback)
    {
        object v = Legacy(name);
        if (v is bool) return (bool)v;
        bool parsed;
        return v != null && bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
    }

    private int LegacyInt(string name, int fallback)
    {
        double d;
        object v = Legacy(name);
        return v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d)
            ? (int)Math.Round(d)
            : fallback;
    }

    private string LegacyType()
    {
        switch (LegacyString("ameShoutoutType", "both").Trim().ToLowerInvariant())
        {
            case "message": return TypeLabels[0];
            case "twitch": return TypeLabels[1];
            default: return TypeLabels[2];
        }
    }

    private string LegacyTextSource()
    {
        string s = LegacyString("ameTextSourceName", "");
        return s.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : s;
    }

    private void EnsureAutoGroup()
    {
        try { if (!CPH.GroupExists(AutoGroup)) CPH.AddGroup(AutoGroup); }
        catch (Exception ex) { CPH.LogWarn(LogPrefix + " Couldn't create the " + AutoGroup + " group: " + ex.Message); }
    }

    // Chat

    private void SendChat(Config cfg, string message)
    {
        if (cfg.ChatBot && !string.IsNullOrWhiteSpace(cfg.ChatBotAction))
        {
            // The argument and the action run together, so overlapping messages don't swap text.
            lock (ChatLock)
            {
                CPH.SetArgument("message", message);
                if (CPH.RunAction(cfg.ChatBotAction, true)) return;
            }
            CPH.LogWarn(LogPrefix + " The action '" + cfg.ChatBotAction + "' couldn't run, so the message went out directly.");
        }
        CPH.SendMessage(message, true, true);
    }

    private bool Reply(Config cfg, string msgId, string message)
    {
        if (string.IsNullOrEmpty(msgId)) SendChat(cfg, message);
        else CPH.TwitchReplyToMessage(message, msgId, true, true);
        return true;
    }

    private bool Fail(Config cfg, string message)
    {
        if (cfg.ErrorsInChat) SendChat(cfg, message);
        CPH.LogWarn(LogPrefix + " " + message);
        return false;
    }

    // Text

    private static string Render(string template, Dictionary<string, Func<string>> tokens)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return TokenRe.Replace(template, match =>
        {
            Func<string> value;
            if (!tokens.TryGetValue(match.Groups[1].Value, out value)) return match.Value;
            string text = value() ?? "";
            switch (match.Groups[2].Value.ToLowerInvariant())
            {
                case "lower": return text.ToLowerInvariant();
                case "upper": return text.ToUpperInvariant();
                case "title": return TitleCase(text);
                default: return text;
            }
        });
    }

    private static string TitleCase(string text)
    {
        string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select((w, i) => i > 0 && i < words.Length - 1 && SmallWords.Contains(w)
            ? w.ToLowerInvariant()
            : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant()));
    }

    private static string Capitalize(string s)
    {
        return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    private static string Humanize(TimeSpan t)
    {
        int seconds = (int)Math.Ceiling(t.TotalSeconds);
        if (seconds < 60) return seconds + "s";
        return seconds / 60 + "m " + (seconds % 60).ToString("00") + "s";
    }

    private static string CleanLogin(string input)
    {
        return (input ?? "").Trim().Trim('"', '\'', ',').TrimStart('@').Trim();
    }

    private static string AfterFirstWord(string text)
    {
        string t = (text ?? "").Trim();
        int space = t.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? "" : t.Substring(space + 1).Trim();
    }

    private static bool SameId(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Alias(string used, string fallback)
    {
        return string.IsNullOrEmpty(used) ? fallback : used;
    }

    private static int Clamp(int v, int min, int max)
    {
        return Math.Max(min, Math.Min(max, v));
    }

    private static double ToDouble(JToken token)
    {
        double d;
        return token != null && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    private static int ToInt(JObject json, string key)
    {
        return json == null ? 0 : (int)Math.Round(ToDouble(json[key]));
    }

    // Arguments

    private static string Arg(Dictionary<string, object> a, string name)
    {
        object value;
        if (!a.TryGetValue(name, out value) || value == null) return null;
        string s = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

}
