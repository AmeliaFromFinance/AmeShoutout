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
public class Shoutout : CPHInlineBase
#else
public class CPHInline
#endif
{
    // --------------- Shared ---------------
    private static readonly HttpClient Http = new();
    private static readonly Random Rng = new();
    private static readonly object SoLock = new();

    // --------------- Settings ---------------
    private const float MaxClipDuration = 30.0f;
    private const float DurationEpsilon = 0.05f;
    private const float PlaybackPadSeconds = 1.5f;

    private const string FallbackTemplate =
        "Go show @{STREAMER} some love, {LIVE_STATUS:lower} {GAME:title|something awesome}!";

    // Regex: {TOKEN[:modifier][|default]}
    // Groups: (1)=token (2)=modifier (3)=default
    private static readonly Regex TokenRe = new(
        @"\{([A-Za-z0-9_]+)(?::([A-Za-z0-9_]+))?(?:\|([^}]*))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ClipSlugOnlyRe = new(@"^[A-Za-z0-9-]{10,100}$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // --------------- Twitch shoutout rate-limit ---------------
    private static readonly object TwitchShoutLock = new();
    private static DateTime _lastTwitchShoutUtc = DateTime.MinValue;
    private static readonly TimeSpan TwitchCooldown = TimeSpan.FromMinutes(2);

    // --------------- Clip picking status ---------------
    private enum ClipPickStatus
    {
        Success,
        NoRecentClips,
        NoEligibleShortClips
    }

    public bool Execute()
    {
        // ----- GraphQL Hashes / Client ID -----
        string VideoAccessTokenHash = GetGlobal<string>("ShoutoutVideoAccessTokenHash");
        string UseLiveHash          = GetGlobal<string>("ShoutoutUseLiveHash");
        string ChatClipHash         = GetGlobal<string>("ShoutoutChatClipHash");
        string clientId             = GetGlobal<string>("ShoutoutGqlClientId");
        
        // ----- OBS Vars -----
        string scene  = GetGlobal<string>("ShoutoutSceneName");
        string source = GetGlobal<string>("ShoutoutSourceName");
        
        // ----- Setting Vars -----
        int  days           = GetGlobal<int>("ShoutoutClipsWithinDays");
        bool preferPopular  = GetGlobal<bool>("ShoutoutPreferPopular");
        bool showClips      = GetGlobal<bool>("ShoutoutShowClips");
        string shoutoutType = GetGlobal<string>("ShoutoutType");

        // ----- Event args -----
        string user       = GetArg<string>("user")?.Trim();
        string input0     = GetArg<string>("input0")?.Trim();
        string msgId      = GetArg<string>("msgId");
        string sourceType = GetArg<string>("__source");
        string showClip   = GetArg<string>("input1");

        // ----- Get User Information -----
        TwitchUserInfoEx targetData;
        if (sourceType == "CommandTriggered") targetData = CPH.TwitchGetExtendedUserInfoByLogin(input0);
        else targetData = CPH.TwitchGetExtendedUserInfoByLogin(user);

        if (targetData == null)
        {
            CPH.TwitchReplyToMessage($"I was unable to find data for the user '{input0}', check that you typed the name correctly.", msgId);
            return false;
        }

        string targetUsername = targetData.UserName;
        string targetGame = targetData.Game;
        string targetTitle = targetData.ChannelTitle;

        Dictionary<string, object> pronounData = CPH.PronounLookup(targetUsername);
        string pronounSubject = null;
        string pronounObject = null;

        if (pronounData is not null)
        {
            if (pronounData.TryGetValue("pronounSubject", out var subj))
                pronounSubject = subj?.ToString();
            if (pronounData.TryGetValue("pronounObject", out var obj))
                pronounObject = obj?.ToString();
        }

        pronounSubject ??= "They";
        pronounObject  ??= "Them";
        
        if (days <= 0) days = 90;

        if (sourceType == "TwitchFirstWord" && !CPH.GetTwitchUserVar<bool>(targetUsername, "autoShoutout"))
            return true;

        bool isLive = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(targetUsername))
                isLive = IsChannelLiveAsync(targetUsername!.ToLowerInvariant(), clientId, UseLiveHash).GetAwaiter().GetResult();
        }
        catch (Exception ex) { CPH.LogWarn($"SO - Live check failed: {ex.Message}"); }

        // ----- Template -> Chat -----
        Dictionary<string, Func<string>> tokens = BuildTokens(user, targetUsername, targetGame, targetTitle, pronounSubject, pronounObject, isLive);
        string template = FirstNonEmpty(
            CPH.GetTwitchUserVar<string>(targetUsername, "shoutoutTemplate"),
            FallbackTemplate
        );
        string output = RenderTemplate(template, tokens);

        if (new[] { "both", "twitch" }.Contains(shoutoutType))
        {
            bool canContinue = EnqueueTwitchShout(targetUsername, shoutoutType);
            if (!canContinue && shoutoutType == "twitch") return false;
        }

        if (!string.IsNullOrWhiteSpace(output) && new[] { "both", "message" }.Contains(shoutoutType)) CPH.SendMessage(output);

        if (showClip is "noclip" or "no-clip" || !showClips) return true;

        // ----- Clip selection & playback -----
        string clipOverrideSlug = ExtractClipSlug(showClip);
        if (!string.IsNullOrWhiteSpace(clipOverrideSlug))
        {
            try
            {
                var (srcUrl, sig, tok) = GetClipInfo(clipOverrideSlug, VideoAccessTokenHash, clientId).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(tok))
                    return Fail("I couldn't play that clip (bad URL/slug).", "Clip override: failed to retrieve access token");

                float? durSec = GetClipDurationSecondsAsync(clipOverrideSlug, clientId, ChatClipHash).GetAwaiter().GetResult();
                float duration = ClampDuration(durSec.GetValueOrDefault(20f) + 1f);

                CPH.LogInfo($"SO - Override clip slug={clipOverrideSlug} duration={(durSec.HasValue ? $"{durSec.Value:0.##}s" : "unknown")}");

                lock (SoLock) PlayClip(scene, source, srcUrl, sig, tok, duration);
                return true;
            }
            catch (Exception ex)
            {
                CPH.LogError($"SO - Override clip exception: {ex}");
                return Fail("Something went wrong playing that clip. Sadge");
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(targetUsername))
            {
                CPH.LogWarn("SO - targetUser was empty; skipping clip.");
                return true;
            }

            (string slug, float duration, ClipPickStatus status) = PickClipSlug(targetUsername, days, preferPopular);
            if (string.IsNullOrEmpty(slug))
            {
                if (status == ClipPickStatus.NoRecentClips)
                    return Fail(
                        $"{targetUsername} doesn't have any clips within the last {days} days.",
                        $"No clips within last {days} days for {targetUsername}"
                    );
                else
                    return Fail(
                        $"{targetUsername} has clips within the last {days} days, but none are {MaxClipDuration:0.#}s or less.",
                        $"No eligible short clips (≤ {MaxClipDuration}s) within last {days} days for {targetUsername}"
                    );
            }

            (string srcUrl, string sig, string tok) = GetClipInfo(slug, VideoAccessTokenHash, clientId).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(tok))
                return Fail("I couldn't find that clip! Sadge", "Failed to retrieve clip info from GraphQL");

            CPH.LogInfo($"SO - user={targetUsername} clip={slug} dur={duration:0.##}s");
            lock (SoLock) PlayClip(scene, source, srcUrl!, sig!, tok!, duration);
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError($"SO - Exception: {ex}");
            return Fail("Something went wrong fetching that clip. Sadge");
        }
    }

    // --------------- Handle Twitch Shoutout ---------------
    private bool EnqueueTwitchShout(string login, string shoutoutType)
    {
        if (string.IsNullOrWhiteSpace(login)) return true;

        try
        {
            lock (TwitchShoutLock)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastTwitchShoutUtc;
                var remaining = TwitchCooldown - elapsed;

                if (remaining < TimeSpan.Zero)  remaining = TimeSpan.Zero;
                if (remaining > TwitchCooldown) remaining = TwitchCooldown;

                bool pureTwitch = string.Equals(shoutoutType, "twitch", StringComparison.OrdinalIgnoreCase);
                bool bothMode = string.Equals(shoutoutType, "both", StringComparison.OrdinalIgnoreCase);
                
                if (pureTwitch && remaining > TimeSpan.Zero)
                {
                    double waitSec = remaining.TotalSeconds;
                    CPH.SendMessage($"I will shoutout {login} in {waitSec:0.#}s when Twitch shoutout cooldown is finished.");
                    CPH.LogInfo($"SO - Waiting {(int)remaining.TotalMilliseconds}ms to respect Twitch /shoutout cooldown.");
                    CPH.Wait((int)remaining.TotalMilliseconds);
                } else if (bothMode && remaining > TimeSpan.Zero)
                {
                    CPH.LogInfo($"SO - Skipping Twitch /shoutout for {login} due to cooldown (both mode).");
                    return true;
                }

                string loginNorm = NormLogin(login);
                bool wasSuccessful = CPH.TwitchSendShoutoutByLogin(loginNorm);

                if (!wasSuccessful)
                {
                    if (pureTwitch)
                    {
                        CPH.SendMessage($"I was unable to give {login} a shoutout, you can only shoutout this user once per hour.");
                        return false;
                    }

                    CPH.LogInfo($"SO - Twitch /shoutout failed for {loginNorm} (both mode). Continuing without Twitch shout.");
                    return true;
                }

                _lastTwitchShoutUtc = DateTime.UtcNow;
                CPH.LogInfo($"SO - /shoutout sent to {loginNorm} at {_lastTwitchShoutUtc:O}");
                return true;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"SO - EnqueueTwitchShout exception: {ex}");
            return !string.Equals(shoutoutType, "twitch", StringComparison.OrdinalIgnoreCase);
        }
    }

    // --------------- Template rendering ---------------
    private static string RenderTemplate(string template, IDictionary<string, Func<string>> registry)
        => string.IsNullOrEmpty(template)
            ? ""
            : TokenRe.Replace(template, m =>
            {
                string key = m.Groups[1].Value;
                string modifier = m.Groups[2].Success ? m.Groups[2].Value : null;
                string fallback = m.Groups[3].Success ? m.Groups[3].Value : null;

                registry.TryGetValue(key, out Func<string> provider);
                string value = provider?.Invoke();
                value = string.IsNullOrWhiteSpace(value) ? fallback : value;

                return ApplyModifier(value, modifier) ?? m.Value;
            });

    private static string ApplyModifier(string value, string modifier) => 
        string.IsNullOrWhiteSpace(modifier) ? value :
        modifier switch
        {
            "upper" => value?.ToUpperInvariant(),
            "lower" => value?.ToLowerInvariant(),
            "title" => value is null ? null : ToTitleCase(value.ToLowerInvariant()),
            "trim"  => value?.Trim(),
            _       => value
        };

    private static string ToTitleCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        HashSet<string> smallWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "after", "along", "an", "and", "around", "at", 
            "but", "by", "down", "for", "from", "in", "into", 
            "like", "nor", "of", "on", "or", "out", "over", 
            "per", "so", "the", "to", "up", "with", "without", "yet"
        };

        string[] words = input.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];

            if (i == 0 || i == words.Length - 1 || !smallWords.Contains(w))
            {
                words[i] = char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant();
            }
            else
            {
                words[i] = w.ToLowerInvariant();
            }
        }

        return string.Join(" ", words);
    }

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a :
        !string.IsNullOrWhiteSpace(b) ? b : null;
        
    private static string OrDefault(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static Dictionary<string, Func<string>> BuildTokens(
        string user,
        string targetUser,
        string game,
        string targetChannelTitle,
        string pronounSubject,
        string pronounObject,
        bool isLive)
    {
        string subj = OrDefault(pronounSubject, "they").Trim();
        string obj = OrDefault(pronounObject, "them").Trim();
        bool isThey = subj.Equals("they", StringComparison.OrdinalIgnoreCase);
        string AreIs(bool present) => present ? (isThey ? "are" : "is") : (isThey ? "were" : "was");

        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["USER"]             = () => user,
            ["STREAMER"]         = () => targetUser,
            ["GAME"]             = () => game,
            ["TITLE"]            = () => targetChannelTitle,
            ["URL"]              = () => string.IsNullOrWhiteSpace(targetUser) ? "" : $"https://twitch.tv/{targetUser.Replace(" ", "").ToLowerInvariant()}",
            ["PRONOUN_SUBJECT"]  = () => subj,
            ["PRONOUN_OBJECT"]   = () => obj,
            ["SUBJECT_WASWERE"]  = () => $"{subj} {AreIs(false)}",
            ["SUBJECT_ISARE"]    = () => $"{subj} {AreIs(true)}",
            ["LIVE_STATUS"]      = () => $"{subj} {AreIs(isLive)} {(isLive ? "currently streaming" : "last streaming")}"
        };
    }

    // --------------- Get specified clip ---------------
    private static string ExtractClipSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim().Trim('"', '\'');

        if (ClipSlugOnlyRe.IsMatch(input)) return input;

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segs = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host.EndsWith("clips.twitch.tv", StringComparison.OrdinalIgnoreCase))
                return segs.LastOrDefault();

            if (uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < segs.Length - 1; i++)
                    if (segs[i].Equals("clip", StringComparison.OrdinalIgnoreCase))
                        return segs[i + 1];
            }
        }

        return null;
    }

    private async Task<float?> GetClipDurationSecondsAsync(string slug, string clientId, string hash)
    {
        using HttpResponseMessage resp = await PostGqlAsync(clientId, new
        {
            operationName = "ChatClip",
            variables = new { clipSlug = slug },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        }).ConfigureAwait(false);
        
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            CPH.LogWarn($"SO - ChatClip duration request failed: {resp.StatusCode} {body}");
            return null;
        }

        try
        {
            JObject json = JObject.Parse(body);
            JToken durTok = json["data"]?["clip"]?["durationSeconds"];
            if (durTok != null && durTok.Type != JTokenType.Null &&
                float.TryParse(durTok.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v;
        }
        catch (Exception ex) { CPH.LogWarn($"SO - ChatClip parse error: {ex.Message}"); }
        return null;
    }

    // --------------- Clip selection ---------------
    private (string slug, float duration, ClipPickStatus status) PickClipSlug(string username, int days, bool preferPopular)
        => preferPopular
            ? PickWeightedClipSlug(username, days)
            : PickRandomClipSlug(username, days);

    // Weighted random: purely based on view count
    private (string slug, float duration, ClipPickStatus status) PickWeightedClipSlug(string username, int days)
    {
        List<ClipData> clips = [.. CPH.GetClipsForUser(username)
            .Where(c => c.CreatedAt >= DateTime.UtcNow.AddDays(-days))];

        if (clips.Count == 0) return (null, 0, ClipPickStatus.NoRecentClips);

        List<ClipData> eligible = [.. clips.Where(c => c.Duration <= (MaxClipDuration + DurationEpsilon))];
        if (eligible.Count == 0)
        {
            CPH.LogInfo($"SO - No clips ≤ {MaxClipDuration}s for {username}");
            return (null, 0, ClipPickStatus.NoEligibleShortClips);
        }

        List<(ClipData c, double w)> scored =
        [
            .. eligible.Select(c =>
            {
                long views = Math.Max(0, c.ViewCount);
                double w = views + 1.0;
                return (c, w);
            })
        ];

        double total = scored.Sum(x => x.w);
        if (total <= 0.0)
        {
            CPH.LogInfo($"SO - Weighted sum=0 for {username}; falling back to uniform.");
            return PickRandomClipSlug(username, days);
        }

        double pick = Rng.NextDouble() * total;
        foreach (var (c, w) in scored)
        {
            pick -= w;
            if (pick > 0) continue;

            string id = c.Id;
            float dur = c.Duration + 1.5f;

            CPH.LogInfo($"SO - Weighted pick id={id} dur={dur:0.##}s views={c.ViewCount} weight={w:0.##}");
            return (id, dur, ClipPickStatus.Success);
        }

        var last = scored[scored.Count - 1];
        {
            string id = last.c.Id;
            float dur = last.c.Duration + PlaybackPadSeconds;
            CPH.LogInfo($"SO - Weighted pick (fallback last) id={id} dur={dur:0.##}s views={last.c.ViewCount} weight={last.w:0.##}");
            return (id, dur, ClipPickStatus.Success);
        }
    }

    // Randomly pick a clip ≤ 30s from the last 'days' days (uniform via reservoir sampling)
    private (string slug, float duration, ClipPickStatus status) PickRandomClipSlug(string username, int days)
    {
        List<ClipData> clips = [.. CPH.GetClipsForUser(username).Where(c => c.CreatedAt >= DateTime.UtcNow.AddDays(-days))];
        if (clips.Count == 0) return (null, 0, ClipPickStatus.NoRecentClips);

        int count = 0;
        string pickedId = null;
        float pickedDur = 0f;

        foreach (ClipData c in clips)
        {
            if (c.Duration > (MaxClipDuration + DurationEpsilon)) continue;
            count++;
            if (Rng.Next(count) == 0) { pickedId = c.Id; pickedDur = ClampDuration(c.Duration + PlaybackPadSeconds); }
        }

        if (pickedId == null)
        {
            CPH.LogInfo($"SO - No clips ≤ {MaxClipDuration}s for {username}");
            return (null, 0, ClipPickStatus.NoEligibleShortClips);
        }

        CPH.LogInfo($"SO - Picked clip id={pickedId} dur={pickedDur:0.##}");
        return (pickedId, pickedDur, ClipPickStatus.Success);
    }

    // --------------- Twitch API ---------------
    private async Task<(string sourceUrl, string signature, string token)> GetClipInfo(string clipId, string hash, string clientId)
    {
        using HttpResponseMessage resp = await PostGqlAsync(clientId, new
        {
            operationName = "VideoAccessToken_Clip",
            variables     = new { slug = clipId, platform = "web" },
            extensions    = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        }).ConfigureAwait(false);

        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            CPH.LogError($"SO - GraphQL {resp.StatusCode}: {body}");
            return (null, null, null);
        }

        JObject json = JObject.Parse(body);
        JToken pact  = json["data"]?["clip"]?["playbackAccessToken"];
        string sig   = pact?["signature"]?.ToString();
        string token = pact?["value"]?.ToString();

        if (json["data"]?["clip"]?["videoQualities"] is not JArray arr || arr.Count == 0)
            return (null, null, null);

        (int h, double fps, string url) = ChooseClipQuality(arr, preferredMax: 720);
        if (url == null) return (null, null, null);

        CPH.LogInfo($"SO - Chosen quality: {h}p @ ~{fps:0.##}fps (preferredMax=720)");
        return (url, sig, token);
    }

    private static (int h, double fps, string url) ChooseClipQuality(JArray qualities, int preferredMax)
    {
        (int h, double fps, string url) bestUnder = default, bestAny  = default;

        foreach (JToken item in qualities)
        {
            string qualityStr = item?["quality"]?.ToString();
            string url        = item?["sourceURL"]?.ToString();
            string fpsStr     = item?["frameRate"]?.ToString();

            if (string.IsNullOrWhiteSpace(qualityStr) || string.IsNullOrWhiteSpace(url)) continue;
            if (!int.TryParse(qualityStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) continue;
            _ = double.TryParse(fpsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps);

            bool betterAny   = bestAny.url == null   || h > bestAny.h   || (h == bestAny.h   && Math.Abs(fps - 60.0) < Math.Abs(bestAny.fps - 60.0));
            bool betterUnder = h <= preferredMax && (bestUnder.url == null || h > bestUnder.h || (h == bestUnder.h && Math.Abs(fps - 60.0) < Math.Abs(bestUnder.fps - 60.0)));

            if (betterAny)   bestAny   = (h, fps, url);
            if (betterUnder) bestUnder = (h, fps, url);
        }
        return bestUnder.url != null ? bestUnder : bestAny;
    }

    private static async Task<bool> IsChannelLiveAsync(string username, string clientId, string hash)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(clientId)) return false;

        using HttpResponseMessage resp = await PostGqlAsync(clientId, new
        {
            operationName = "UseLive",
            variables     = new { channelLogin = username },
            extensions    = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        }).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode) return false;

        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        JObject json = JObject.Parse(body);
        return json["data"]?["user"]?["stream"]?.Type is not null and not JTokenType.Null;
    }

    // --------------- OBS playback ---------------
    private void PlayClip(string scene, string source, string sourceUrl, string signature, string token, float duration)
    {
        string url = $"{sourceUrl}?token={Uri.EscapeDataString(token)}&sig={signature}";
        int delayMs = (int)(ClampDuration(duration) * 1000);

        CPH.LogInfo($"SO - Final URL: {url}");

        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, url);
        CPH.Wait(300);
        CPH.ObsSetSourceVisibility(scene, source, true);
        CPH.Wait(delayMs);
        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, "");
    }

    // --------------- HTTP helper ---------------
    private static async Task<HttpResponseMessage> PostGqlAsync(string clientId, object payloadObj)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://gql.twitch.tv/gql");
        req.Headers.TryAddWithoutValidation("Client-ID", clientId);
        req.Content = new StringContent(JsonConvert.SerializeObject(payloadObj), Encoding.UTF8, "application/json");
        return await Http.SendAsync(req).ConfigureAwait(false);
    }

    // --------------- Helpers ---------------
    private static string NormLogin(string login) =>
        login?.Replace(" ", "").ToLowerInvariant();

    private static float ClampDuration(float seconds) =>
        Math.Min(Math.Max(0f, seconds), MaxClipDuration);

    private T GetGlobal<T>(string key) => CPH.GetGlobalVar<T>(key);

    private T GetArg<T>(string key)
    {
        CPH.TryGetArg(key, out T value);
        return value;
    }

    // --------------- Fail helper ---------------
    private bool Fail(string chatMsg, string logMsg = null)
    {
        if (!string.IsNullOrEmpty(chatMsg)) CPH.SendMessage(chatMsg);
        if (!string.IsNullOrEmpty(logMsg))  CPH.LogError($"SO - {logMsg}");
        return false;
    }
}
