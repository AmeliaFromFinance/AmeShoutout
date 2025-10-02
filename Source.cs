using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Twitch.Common.Models.Api;
using System.Linq;

public class CPHInline
{
    // --------------- Shared ---------------
    private static readonly HttpClient Http = new();
    private static readonly Random Rng = new();
    private static readonly TextInfo TextInfo = CultureInfo.CurrentCulture.TextInfo;
    private static readonly object SoLock = new(); // Lock to serialize OBS playback

    // --------------- Settings ---------------
    private const float MaxClipDuration = 30.0f;   // max clip duration to play
    private const float DurationEpsilon = 0.05f;   // small buffer for floating point comparisons
    private const string FallbackTemplate =
        "Go show @{STREAMER} some love, {LIVE_STATUS:lower} {GAME:title|something awesome}!";

    // Regex: {TOKEN[:modifier][|default]}
    // Groups: (1)=token (2)=modifier (3)=default
    private static readonly Regex TokenRe = new(
        @"\{([A-Za-z0-9_]+)(?::([A-Za-z0-9_]+))?(?:\|([^}]*))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClipSlugOnlyRe = new(@"^[A-Za-z0-9-]{10,100}$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // GQL persisted query hashes
    private const string UseLiveSha256   = "639d5f11bfb8bf3053b424d9ef650d04c4ebb7d94711d644afb08fe9a0fad5d9";
    private const string ChatClipSha256  = "9aa558e066a22227c5ef2c0a8fded3aaa57d35181ad15f63df25bff516253a90";

    public bool Execute()
    {
        // ----- Global config -----
        string hash     = CPH.GetGlobalVar<string>("ShoutoutHash");
        string clientId = CPH.GetGlobalVar<string>("ShoutoutClientId");
        string scene    = CPH.GetGlobalVar<string>("ShoutoutSceneName");
        string source   = CPH.GetGlobalVar<string>("ShoutoutSourceName");
        int days        = CPH.GetGlobalVar<int>("clipsWithinDays");

        // ----- Event args -----
        CPH.TryGetArg("user", out string user);
        CPH.TryGetArg("targetUser", out string targetUser);
        CPH.TryGetArg("game", out string game);
        CPH.TryGetArg("targetChannelTitle", out string targetChannelTitle);
        CPH.TryGetArg("pronounSubject", out string pronoun);
        CPH.TryGetArg("pronounObject", out string pronounObject);
        CPH.TryGetArg("__source", out string sourceType);
        CPH.TryGetArg("input1", out string showClip);
        CPH.TryGetArg("broadcastUser", out string broadcastUser);

        targetUser = targetUser?.Trim();
        user       = user?.Trim();
        if (days <= 0) days = 90;

        // Only run if autoShoutout is enabled for the user in FirstWord flow
        if (sourceType == "TwitchFirstWord" && !CPH.GetTwitchUserVar<bool>(user, "autoShoutout"))
            return true;

        // ----- Live check -----
        bool isLive = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(targetUser))
                isLive = IsChannelLiveAsync(targetUser.ToLowerInvariant(), clientId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"SO - Live check failed: {ex.Message}");
        }

        // ----- Template -> Chat -----
        Dictionary<string, Func<string>> tokens = BuildTokens(user, targetUser, game, targetChannelTitle, pronoun, pronounObject, isLive);

        string template = FirstNonEmpty(
            CPH.GetTwitchUserVar<string>(targetUser, "shoutoutTemplate"),
            FallbackTemplate
        );

        string output = RenderTemplate(template, tokens);
        if (!string.IsNullOrWhiteSpace(output))
            CPH.SendMessage(output);

        // Respect explicit "no clip"
        if (showClip is "noclip" or "no-clip")
            return true;

        // ----- Clip selection & playback -----
        string clipOverrideSlug = ExtractClipSlug(showClip);

        if (!string.IsNullOrWhiteSpace(clipOverrideSlug))
        {
            try
            {
                var (srcUrl, sig, tok) = GetClipInfo(clipOverrideSlug, hash, clientId).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(tok))
                    return Fail("I couldn't play that clip (bad URL/slug).", "Clip override: failed to retrieve access token");

                float? durSec = GetClipDurationSecondsAsync(clipOverrideSlug, clientId).GetAwaiter().GetResult();

                float duration = durSec.HasValue
                    ? Math.Min(durSec.Value + 1.0f, MaxClipDuration)
                    : Math.Min(20.0f, MaxClipDuration);

                CPH.LogInfo($"SO - Override clip slug={clipOverrideSlug} duration={(durSec.HasValue ? durSec.Value.ToString("0.##") + "s" : "unknown")}");

                lock (SoLock)
                {
                    PlayClip(scene, source, srcUrl, sig, tok, duration);
                }
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
            if (string.IsNullOrWhiteSpace(targetUser))
            {
                CPH.LogWarn("SO - targetUser was empty; skipping clip.");
                return true;
            }

            (string slug, float duration) = PickRandomClipSlug(targetUser, days);
            if (string.IsNullOrEmpty(slug))
                return Fail($"{targetUser} doesn't have any clips within the last {days} days that are 30s or less!", $"No short clips for {targetUser}");

            (string srcUrl, string sig, string tok) = GetClipInfo(slug, hash, clientId).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(tok))
                return Fail("I couldn't find that clip! Sadge", "Failed to retrieve clip info from GraphQL");

            CPH.LogInfo($"SO - user={targetUser} clip={slug} dur={duration:0.##}s");

            // Serialize clip playback so multiple shoutouts don't overlap
            lock (SoLock)
            {
                PlayClip(scene, source, srcUrl!, sig!, tok!, duration);
            }

            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError($"SO - Exception: {ex}");
            return Fail("Something went wrong fetching that clip. Sadge");
        }
    }

    // --------------- Template rendering ---------------
    private static string RenderTemplate(string template, IDictionary<string, Func<string>> registry)
    {
        if (string.IsNullOrEmpty(template)) return "";

        return TokenRe.Replace(template, m =>
        {
            string key      = m.Groups[1].Value;
            string modifier = m.Groups[2].Success ? m.Groups[2].Value : null;
            string fallback = m.Groups[3].Success ? m.Groups[3].Value : null;

            registry.TryGetValue(key, out Func<string> provider);
            string value = provider?.Invoke();

            value = string.IsNullOrWhiteSpace(value) ? fallback : value;
            value = ApplyModifier(value, modifier);

            return value ?? m.Value;
        });
    }

    private static string ApplyModifier(string value, string modifier)
    {
        if (value == null || string.IsNullOrWhiteSpace(modifier)) return value;

        return modifier switch
        {
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            "title" => TextInfo.ToTitleCase(value.ToLower()),
            "trim"  => value.Trim(),
            _       => value,
        };
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

        // Helper to return correct verb (is/are/was/were)
        string AreIs(bool present) => present ? (isThey ? "are" : "is") : (isThey ? "were" : "was");

        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["USER"] = () => user,
            ["STREAMER"] = () => targetUser,
            ["GAME"] = () => game,
            ["TITLE"] = () => targetChannelTitle,
            ["URL"] = () =>
            {
                if (string.IsNullOrWhiteSpace(user)) return "";
                string u = user.Replace(" ", "");
                return $"https://twitch.tv/{u.ToLowerInvariant()}";
            },
            ["PRONOUN_SUBJECT"] = () => subj,
            ["PRONOUN_OBJECT"] = () => obj,
            ["SUBJECT_WASWERE"] = () => $"{subj} {AreIs(false)}",
            ["SUBJECT_ISARE"] = () => $"{subj} {AreIs(true)}",
            ["LIVE_STATUS"] = () =>
            {
                string verb = AreIs(isLive);
                return isLive
                    ? $"{subj} {verb} currently streaming"
                    : $"{subj} {verb} last streaming";
            }
        };
    }

    // --------------- Get specified clip ---------------
    private static string ExtractClipSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim().Trim('"', '\'');

        if (ClipSlugOnlyRe.IsMatch(input))
            return input;

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Host.EndsWith("clips.twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                var segs = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
                return segs.Length >= 1 ? segs[segs.Length - 1] : null;
            }

            if (uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                var segs = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segs.Length - 1; i++)
                {
                    if (segs[i].Equals("clip", StringComparison.OrdinalIgnoreCase))
                        return segs[i + 1];
                }
            }
        }

        return null;
    }

    private async Task<float?> GetClipDurationSecondsAsync(string slug, string clientId)
    {
        using HttpResponseMessage resp = await PostGqlAsync(clientId, new
        {
            operationName = "ChatClip",
            variables = new { clipSlug = slug },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = ChatClipSha256 } }
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
            var durTok = json["data"]?["clip"]?["durationSeconds"];
            if (durTok != null && durTok.Type != JTokenType.Null)
            {
                if (float.TryParse(durTok.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    return v;
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"SO - ChatClip parse error: {ex.Message}");
        }
        return null;
    }

    // --------------- Clip selection ---------------
    // Randomly pick a clip ≤ 30s from the last 'days' days (uniform via reservoir sampling)
    private (string slug, float duration) PickRandomClipSlug(string username, int days)
    {
        List<ClipData> clips = [.. CPH.GetClipsForUser(username).Where(c => c.CreatedAt >= DateTime.UtcNow.AddDays(-days))];
        if (clips.Count == 0) return (null, 0);

        int count = 0;
        string pickedId = null;
        float pickedDur = 0f;

        foreach (ClipData c in clips)
        {
            if (c.Duration > (MaxClipDuration + DurationEpsilon)) continue;

            count++;
            if (Rng.Next(count) == 0)
            {
                pickedId = c.Id;
                pickedDur = c.Duration + 1.5f; // small buffer to avoid early cut
            }
        }

        if (pickedId == null)
        {
            CPH.LogInfo($"SO - No clips ≤ {MaxClipDuration}s for {username}");
            return (null, 0);
        }

        CPH.LogInfo($"SO - Picked clip id={pickedId} dur={pickedDur:0.##}");
        return (pickedId, pickedDur);
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

        (int h, double fps, string url) pick = ChooseClipQuality(arr, preferredMax: 720);
        if (pick.url == null) return (null, null, null);

        CPH.LogInfo($"SO - Chosen quality: {pick.h}p @ ~{pick.fps:0.##}fps (preferredMax=720)");
        return (pick.url, sig, token);
    }

    private static (int h, double fps, string url) ChooseClipQuality(JArray qualities, int preferredMax)
    {
        (int h, double fps, string url) bestUnder = default;
        (int h, double fps, string url) bestAny  = default;

        foreach (JToken item in qualities)
        {
            string qualityStr = item?["quality"]?.ToString();
            string url        = item?["sourceURL"]?.ToString();
            string fpsStr     = item?["frameRate"]?.ToString();

            if (string.IsNullOrWhiteSpace(qualityStr) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!int.TryParse(qualityStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                continue;

            _ = double.TryParse(fpsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps);

            // best overall
            if (bestAny.url == null ||
                height > bestAny.h ||
                (height == bestAny.h && Math.Abs(fps - 60.0) < Math.Abs(bestAny.fps - 60.0)))
            {
                bestAny = (height, fps, url);
            }

            // best under preferredMax
            if (height <= preferredMax &&
                (bestUnder.url == null ||
                 height > bestUnder.h ||
                 (height == bestUnder.h && Math.Abs(fps - 60.0) < Math.Abs(bestUnder.fps - 60.0))))
            {
                bestUnder = (height, fps, url);
            }
        }

        // Prefer ≤ preferredMax, else fallback to best overall
        return bestUnder.url != null ? bestUnder : bestAny;
    }

    private static async Task<bool> IsChannelLiveAsync(string username, string clientId)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(clientId))
            return false;

        using HttpResponseMessage resp = await PostGqlAsync(clientId, new
        {
            operationName = "UseLive",
            variables     = new { channelLogin = username },
            extensions    = new { persistedQuery = new { version = 1, sha256Hash = UseLiveSha256 } }
        }).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode) return false;

        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        JObject json = JObject.Parse(body);

        // If "stream" exists => live
        return json["data"]?["user"]?["stream"]?.Type is not null and not JTokenType.Null;
    }

    // --------------- OBS playback ---------------
    private void PlayClip(string scene, string source, string sourceUrl, string signature, string token, float duration)
    {
        string url = $"{sourceUrl}?token={Uri.EscapeDataString(token)}&sig={signature}";
        int delay = Math.Max(0, (int)(Math.Min(duration, MaxClipDuration) * 1000));

        CPH.LogInfo($"SO - Final URL: {url}");

        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, url);
        CPH.Wait(300); // small buffer before making visible
        CPH.ObsSetSourceVisibility(scene, source, true);
        CPH.Wait(delay); // wait until clip finishes
        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, "");
    }

    // --------------- HTTP helper ---------------
    private static async Task<HttpResponseMessage> PostGqlAsync(string clientId, object payloadObj)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://gql.twitch.tv/gql");
        req.Headers.TryAddWithoutValidation("Client-ID", clientId);

        string payload = JsonConvert.SerializeObject(payloadObj);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        return await Http.SendAsync(req).ConfigureAwait(false);
    }

    // --------------- Fail helper ---------------
    private bool Fail(string chatMsg, string logMsg = null)
    {
        if (!string.IsNullOrEmpty(chatMsg)) CPH.SendMessage(chatMsg);
        if (!string.IsNullOrEmpty(logMsg))  CPH.LogError($"SO - {logMsg}");
        return false;
    }
}
