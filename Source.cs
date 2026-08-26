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
using Twitch.Common.Models.Api;

#if EXTERNAL_EDITOR
public class YASS : CPHInlineBase
#else
public class CPHInline
#endif
{
    private const string LogPrefix   = "[Ame-Shoutout]";
    private const string GqlEndpoint = "https://gql.twitch.tv/gql";
    private const string HashUrl     = "https://raw.githubusercontent.com/AmeliaFromFinance/Sapphire/main/hashes.json";
    private const int ObsSettleMs    = 300;
    private static readonly TimeSpan TwitchShoutCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HashRefreshInterval = TimeSpan.FromHours(1);

    private static readonly HttpClient Http     = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Random Rng          = new();
    private static readonly object PlaybackLock = new();
    private static readonly object ShoutLock    = new();
    private static readonly object HashLock     = new();
    private static DateTime _lastTwitchShout    = DateTime.MinValue;
    private static DateTime _lastHashRefresh    = DateTime.MinValue;

    private static readonly object TeamCacheLock    = new();
    private static readonly TimeSpan TeamCacheTtl   = TimeSpan.FromHours(1);
    private static List<TeamInfo> _broadcasterTeams = [];
    private static DateTime _broadcasterTeamsAt     = DateTime.MinValue;

    private const string HashVar     = "ameShoutoutHashes";
    private const string ClientIdVar = "ameShoutoutClientId";
    private static string _liveHash = "", _clipHash = "", _accHash = "", _clientId = "";

    private static readonly Regex TokenRe = new(@"\{(\w+)(?::(\w+))?\}", RegexOptions.Compiled);
    private static readonly Regex SlugRe  = new(@"^[A-Za-z0-9-]{10,100}$", RegexOptions.Compiled);
    private static readonly HashSet<string> TitleIgnore = new(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "and", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "so", "the", "to", "up", "yet" };

    private sealed class Config
    {
        public string Scene = "", Source = "", TextSource = "none", Type = "message";
        public string? User, Input0, Input1, Raw, SourceType, DefaultTemplate, TextTemplate;
        public int ClipDays = 60;
        public float ClipLength = 30f;
        public bool PreferPopular, DisplayClips = true, UseChatBot, ShowRaidCount = true, ShowSharedTeams = true, TeamsEnabled = true;
        public string TargetLogin => (SourceType == "CommandTriggered" ? Input0 : User) ?? "";
    }

    private Config ReadConfig() => new()
    {
        Scene           = Arg<string>("sceneName") ?? "",
        Source          = Arg<string>("sourceName") ?? "",
        TextSource      = Arg<string>("gdiTextSourceName") ?? "none",
        Type            = Arg<string>("shoutoutType")?.ToLowerInvariant() ?? "message",
        User            = Arg<string>("user"),
        Input0          = Arg<string>("input0"),
        Input1          = Arg<string>("input1"),
        Raw             = Arg<string>("rawInput"),
        SourceType      = Arg<string>("__source"),
        DefaultTemplate = Arg<string>("defaultTemplate"),
        TextTemplate    = Arg<string>("textTemplate"),
        ClipDays        = Arg<int?>("clipDays") ?? 60,
        ClipLength      = Arg<float?>("clipLength") ?? 30f,
        PreferPopular   = Arg<bool?>("preferPopular") ?? false,
        DisplayClips    = Arg<bool?>("displayClips") ?? true,
        UseChatBot      = Arg<bool?>("useChatBot") ?? false,
        ShowRaidCount   = Arg<bool?>("showRaidCount") ?? true,
        ShowSharedTeams = Arg<bool?>("showSharedTeams") ?? true,
        TeamsEnabled    = Arg<bool?>("enableTeamShoutouts") ?? true,
    };

    private sealed class Pronouns
    {
        public string Subject          = "they";
        public string Object           = "them";
        public bool Plural             => Subject.Equals("they", StringComparison.OrdinalIgnoreCase);
        public string Be(bool present) => present ? (Plural ? "are" : "is") : (Plural ? "were" : "was");
        public string Has              => Plural ? "have" : "has";
    }

    private Pronouns LookupPronouns(string user)
    {
        Pronouns result = new();
        Dictionary<string, object>? p = CPH.PronounLookup(user);
        if (p != null)
        {
            if (p.TryGetValue("pronounSubject", out var s) && s is string subj && subj.Length > 0) result.Subject = subj.ToLowerInvariant();
            if (p.TryGetValue("pronounObject", out var o) && o is string obj && obj.Length > 0) result.Object     = obj.ToLowerInvariant();
        }
        CPH.LogDebug($"{LogPrefix} Pronouns for '{user}': {result.Subject}/{result.Object}");
        return result;
    }

    public bool Execute()
    {
        try
        {
            Config cfg = ReadConfig();
            return Task.Run(() => ExecuteAsync(cfg)).GetAwaiter().GetResult();
        } catch (Exception ex)
        {
            CPH.LogError($"{LogPrefix} Unhandled exception: {ex}");
            return false;
        }
    }

    private async Task<bool> ExecuteAsync(Config cfg)
    {
        await RefreshHashesIfStaleAsync();

        TwitchUserInfoEx? target = CPH.TwitchGetExtendedUserInfoByLogin(cfg.TargetLogin);
        if (target == null) return Fail(cfg, $"User data for '{cfg.TargetLogin}' is null.");

        string targetUser = target.UserName;
        CPH.LogDebug($"{LogPrefix} Source '{cfg.SourceType}' -> target '{targetUser}'.");

        bool needTeams = cfg.ShowSharedTeams || (cfg.TeamsEnabled && cfg.SourceType == "TwitchFirstWord");
        List<string> sharedTeams = needTeams ? GetSharedTeams(targetUser) : [];

        if (cfg.SourceType == "TwitchFirstWord")
        {
            bool inGroup = CPH.UserInGroup(targetUser, Platform.Twitch, "Auto Shoutout");
            bool inTeam   = cfg.TeamsEnabled && sharedTeams.Count > 0;
            if (!inGroup && !inTeam) return true;
            if (cfg.Raw!.StartsWith("!so")) return true;
        }

        Pronouns pn = LookupPronouns(targetUser);
        bool isLive = await IsLiveAsync(targetUser);
        Dictionary<string, Func<string>> tokens = BuildTokens(cfg, target, pn, isLive);

        string message = BuildMessage(cfg, targetUser, pn, tokens, sharedTeams);
        if (!string.IsNullOrWhiteSpace(message) && cfg.Type is "both" or "message")
            SendChat(message, cfg.UseChatBot);

        if (cfg.Type is "both" or "twitch" && !SendTwitchShoutout(targetUser, cfg.UseChatBot) && cfg.Type == "twitch")
            return true;

        if (cfg.Input1?.Equals("no", StringComparison.OrdinalIgnoreCase) == true || !cfg.DisplayClips)
            return true;

        return await PlayClipAsync(cfg, targetUser, tokens);
    }

    private Dictionary<string, Func<string>> BuildTokens(Config cfg, TwitchUserInfoEx target, Pronouns pn, bool isLive)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            { "STREAMER",    () => target.UserName },
            { "USER",        () => cfg.User ?? "" },
            { "GAME",        () => target.Game ?? "" },
            { "URL",         () => $"https://twitch.tv/{target.UserName.ToLowerInvariant()}" },
            { "LIVE_STATUS", () => $"{pn.Subject} {pn.Be(isLive)} {(isLive ? "currently" : "last")} streaming" },
            { "SUBJECT",     () => pn.Subject },
            { "OBJECT",      () => pn.Object },
            { "VERB",        () => pn.Be(isLive) },
        };

    private string BuildMessage(Config cfg, string targetUser, Pronouns pn, Dictionary<string, Func<string>> tokens, List<string> sharedTeams)
    {
        string template = CPH.GetTwitchUserVar<string>(targetUser, "shoutoutTemplate") is string t && t.Length > 0
            ? t
            : cfg.DefaultTemplate ?? "Go show @{STREAMER} some love!";
        string message = Render(template, tokens);

        if (cfg.SourceType == "TwitchRaid" && cfg.ShowRaidCount)
        {
            int count = CPH.GetTwitchUserVar<int>(targetUser, "raidCount") + 1;
            CPH.SetTwitchUserVar(targetUser, "raidCount", count);
            message += $" ({Capitalize(pn.Subject)} {pn.Has} raided {count} time{(count == 1 ? "" : "s")}!)";
        }

        if (cfg.ShowSharedTeams)
            message += SharedTeamsSuffix(sharedTeams, pn);

        return message;
    }

    private List<string> GetSharedTeams(string targetUser)
    {
        string broadcaster = CPH.TwitchGetBroadcaster()?.UserLogin ?? "";
        if (broadcaster.Length == 0 || broadcaster.Equals(targetUser, StringComparison.OrdinalIgnoreCase)) return [];

        List<TeamInfo> targetTeams = CPH.GetTeamInfoByLogin(targetUser) ?? [];
        if (targetTeams.Count == 0) return [];

        List<TeamInfo> broadcasterTeams = GetBroadcasterTeamsCached(broadcaster);
        if (broadcasterTeams.Count == 0) return [];

        HashSet<string> broadcasterTeamIds = new(
            broadcasterTeams.Select(t => t.TeamId).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.Ordinal);

        return [.. targetTeams
            .Where(t => !string.IsNullOrEmpty(t.TeamId) && broadcasterTeamIds.Contains(t.TeamId))
            .Select(t => string.IsNullOrEmpty(t.TeamDisplayName) ? t.TeamName : t.TeamDisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private string SharedTeamsSuffix(List<string> shared, Pronouns pn)
    {
        if (shared.Count == 0) return "";

        string names = shared.Count == 1
            ? shared[0]
            : string.Join(", ", shared.Take(shared.Count - 1)) + " and " + shared[shared.Count - 1];

        return $" {Capitalize(pn.Subject)} {pn.Be(true)} in the team{(shared.Count == 1 ? "" : "s")} {names} with me!";
    }

    private List<TeamInfo> GetBroadcasterTeamsCached(string broadcaster)
    {
        lock (TeamCacheLock)
        {
            if (_broadcasterTeams != null && DateTime.UtcNow - _broadcasterTeamsAt < TeamCacheTtl)
                return _broadcasterTeams;
            _broadcasterTeams   = CPH.GetTeamInfoByLogin(broadcaster) ?? [];
            _broadcasterTeamsAt = DateTime.UtcNow;
            return _broadcasterTeams;
        }
    }

    private bool SendTwitchShoutout(string login, bool chatBot)
    {
        while (true)
        {
            TimeSpan wait;
            lock (ShoutLock)
            {
                TimeSpan elapsed = DateTime.UtcNow - _lastTwitchShout;
                if (elapsed >= TwitchShoutCooldown)
                {
                    if (!CPH.TwitchSendShoutoutByLogin(login.ToLowerInvariant()))
                    {
                        CPH.LogError($"{LogPrefix} Twitch shoutout failed for {login}");
                        return false;
                    }
                    _lastTwitchShout = DateTime.UtcNow;
                    return true;
                }
                wait = TwitchShoutCooldown - elapsed;
            }
            // Wait OUTSIDE the lock so other actions aren't blocked, then re-check.
            SendChat($"Shoutout to {login} delayed by {wait.TotalSeconds:0.#}s.", chatBot);
            CPH.Wait((int)wait.TotalMilliseconds + 500);
        }
    }

    private async Task<bool> PlayClipAsync(Config cfg, string targetUser, Dictionary<string, Func<string>> tokens)
    {
        string slug = ExtractSlug(cfg.Input1);
        float duration;

        if (!string.IsNullOrEmpty(slug))
        {
            float dur = await GetClipDurationAsync(slug);
            duration = Math.Min(Math.Max(0, dur) + 1f, cfg.ClipLength);
        }
        else
        {
            (slug, duration) = PickClip(targetUser, cfg.ClipDays, cfg.ClipLength, cfg.PreferPopular);
            if (string.IsNullOrEmpty(slug)) return Fail(cfg, $"No clips for '{targetUser}'");
        }

        ClipPlayback? clip = await GetClipPlaybackAsync(slug);
        if (clip == null) return Fail(cfg, $"Failed to get playback data for clip '{slug}'");

        string playUrl = $"{clip.Url}?token={Uri.EscapeDataString(clip.Token)}&sig={Uri.EscapeDataString(clip.Signature)}";
        CPH.LogInfo($"{LogPrefix} Playing '{slug}' ({duration:0.#}s) | {playUrl}");

        bool showText = cfg.TextSource != "none";
        lock (PlaybackLock) // serialize OBS playback across concurrent shoutouts
        {
            CPH.ObsSetSourceVisibility(cfg.Scene, cfg.Source, false);
            CPH.ObsSetMediaSourceFile(cfg.Scene, cfg.Source, playUrl);
            if (showText) CPH.ObsSetGdiText(cfg.Scene, cfg.TextSource, Render(cfg.TextTemplate ?? "Go show {STREAMER} some love!", tokens));
            CPH.Wait(ObsSettleMs);
            CPH.ObsSetSourceVisibility(cfg.Scene, cfg.Source, true);
            if (showText) CPH.ObsSetSourceVisibility(cfg.Scene, cfg.TextSource, true);
            CPH.Wait((int)(duration * 1000));
            CPH.ObsSetSourceVisibility(cfg.Scene, cfg.Source, false);
            if (showText) CPH.ObsSetSourceVisibility(cfg.Scene, cfg.TextSource, false);
            CPH.ObsSetMediaSourceFile(cfg.Scene, cfg.Source, "");
        }
        return true;
    }

    private (string slug, float dur) PickClip(string user, int days, float maxLength, bool weighted)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        List<ClipData> list = [.. CPH.GetClipsForUser(user).Where(c => c.CreatedAt >= cutoff && c.Duration <= maxLength)];
        if (list.Count == 0) return ("", 0);

        ClipData clip = weighted ? PickWeighted(list) : list[Rng.Next(list.Count)];
        return (clip.Id, clip.Duration + 1f);
    }

    private static ClipData PickWeighted(List<ClipData> list)
    {
        double total = list.Sum(x => x.ViewCount + 1.0);
        double roll = Rng.NextDouble() * total;
        foreach (ClipData item in list)
        {
            roll -= item.ViewCount + 1.0;
            if (roll <= 0) return item;
        }
        return list[list.Count - 1];
    }

    private sealed class ClipPlayback
    {
        public readonly string Url;
        public readonly string Signature;
        public readonly string Token;

        public ClipPlayback(string url, string signature, string token)
        {
            Url = url;
            Signature = signature;
            Token = token;
        }
    }

    private async Task<ClipPlayback?> GetClipPlaybackAsync(string slug)
    {
        JObject? obj = await GqlAsync("VideoAccessToken_Clip", _accHash, new { slug, platform = "web" });
        JToken? clip = obj?["data"]?["clip"];
        if (clip?["videoQualities"] is not JArray qualities) return null;

        string? url = qualities
            .Select(x => new { Url = x["sourceURL"]?.ToString(), Quality = (int?)x["quality"], Fps = (double?)x["frameRate"] })
            .Where(x => !string.IsNullOrEmpty(x.Url) && x.Quality.HasValue)
            .OrderByDescending(x => x.Quality).ThenByDescending(x => x.Fps)
            .FirstOrDefault()?.Url;

        string? sig = clip["playbackAccessToken"]?["signature"]?.ToString();
        string? token = clip["playbackAccessToken"]?["value"]?.ToString();

        return url == null || sig == null || token == null ? null : new ClipPlayback(url, sig, token);
    }

    private async Task<float> GetClipDurationAsync(string slug)
    {
        JObject? obj = await GqlAsync("ChatClip", _clipHash, new { clipSlug = slug });
        JToken? t = obj?["data"]?["clip"]?["durationSeconds"];
        return float.TryParse(t?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;
    }

    private async Task<bool> IsLiveAsync(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return false;
        JObject? obj = await GqlAsync("UseLive", _liveHash, new { channelLogin = login });
        // Only a real stream object means live; null/missing means offline.
        return obj?["data"]?["user"]?["stream"] is JObject;
    }

    private async Task<JObject?> GqlAsync(string operation, string hash, object variables)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, GqlEndpoint);
            request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(new { operationName = operation, variables, extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } } }),
                Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                CPH.LogWarn($"{LogPrefix} GQL '{operation}' returned {(int)response.StatusCode}");
                return null;
            }
            JToken token = JToken.Parse(await response.Content.ReadAsStringAsync());
            if (token is JArray array) token = array.FirstOrDefault() ?? token;
            return token as JObject;
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"{LogPrefix} GQL '{operation}' failed: {ex.Message}");
            return null;
        }
    }

    private static void ApplyHashes(JObject data)
    {
        _accHash  = data["VideoAccessToken_Clip"]?.ToString() ?? _accHash;
        _clipHash = data["ChatClip"]?.ToString() ?? _clipHash;
        _liveHash = data["UseLive"]?.ToString() ?? _liveHash;
    }

    private static bool HashesLoaded =>
        _accHash.Length > 0 && _clipHash.Length > 0 && _liveHash.Length > 0 && _clientId.Length > 0;

    private async Task RefreshHashesIfStaleAsync()
    {
        lock (HashLock)
        {
            if (!HashesLoaded)
            {
                try
                {
                    string cachedId = CPH.GetGlobalVar<string>(ClientIdVar);
                    if (!string.IsNullOrEmpty(cachedId)) _clientId = cachedId;

                    string cached = CPH.GetGlobalVar<string>(HashVar);
                    if (!string.IsNullOrEmpty(cached))
                    {
                        JObject stored = JObject.Parse(cached);
                        if (stored["hashes"] is JObject h) ApplyHashes(h);
                        if (DateTime.TryParse(stored["fetchedAt"]?.ToString(), null,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out DateTime at) && at > _lastHashRefresh) _lastHashRefresh = at;
                    }
                } catch {}
            }

            if (DateTime.UtcNow - _lastHashRefresh < HashRefreshInterval) return;
            _lastHashRefresh = DateTime.UtcNow;
        }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(HashUrl);
            if (!response.IsSuccessStatusCode) return;

            JObject data = JObject.Parse(await response.Content.ReadAsStringAsync());
            lock (HashLock)
            {
                string? clientId = data["ClientId"]?.ToString();
                if (!string.IsNullOrEmpty(clientId))
                {
                    _clientId = clientId!;
                    CPH.SetGlobalVar(ClientIdVar, _clientId);
                }
                data.Remove("ClientId");

                ApplyHashes(data);
                JObject stored = new()
                {
                    ["fetchedAt"] = DateTime.UtcNow.ToString("o"),
                    ["hashes"] = data
                };
                CPH.SetGlobalVar(HashVar, stored.ToString(Formatting.None));
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"{LogPrefix} Failed to fetch latest hashes: {ex.Message}");
        }
    }

    private static bool TryUpdate(ref string field, string? value)
    {
        if (string.IsNullOrEmpty(value) || field == value) return false;
        field = value!;
        return true;
    }

    private string Render(string template, Dictionary<string, Func<string>> tokens)
        => string.IsNullOrEmpty(template) ? "" : TokenRe.Replace(template, match =>
        {
            if (!tokens.TryGetValue(match.Groups[1].Value, out var func)) return match.Value;
            string value = func() ?? "";
            return match.Groups[2].Value.ToLowerInvariant() switch
            {
                "lower" => value.ToLowerInvariant(),
                "upper" => value.ToUpperInvariant(),
                "title" => TitleCase(value),
                _ => value
            };
        });

    private static string TitleCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        string[] words = str.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select((w, i) =>
            (i == 0 || i == words.Length - 1 || !TitleIgnore.Contains(w))
                ? char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant()
                : w.ToLowerInvariant()));
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    private static string ExtractSlug(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string trimmed = input!.Trim('"', '\'');
        if (SlugRe.IsMatch(trimmed)) return trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? u) && u != null &&
            (u.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
             u.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase)))
        {
            string candidate = u.AbsolutePath.Trim('/').Split('/').Last();
            return SlugRe.IsMatch(candidate) ? candidate : "";
        }
        return "";
    }

    private void SendChat(string message, bool chatBot)
    {
        if (chatBot) { CPH.SetArgument("message", message); CPH.RunAction("[ATB] Send Message"); }
        else CPH.SendMessage(message);
    }

    private bool Fail(Config cfg, string message)
    {
        if (Arg<bool?>("otherMessages") ?? true) SendChat(message, cfg.UseChatBot);
        CPH.LogError($"{LogPrefix} {message}");
        return false;
    }

    private T Arg<T>(string key) { CPH.TryGetArg(key, out T value); return value; }
}