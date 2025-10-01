using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

public class CPHInline
{
    private static readonly HttpClient Http = new();
    private static readonly Random Rng = new();
    private const float MaxClipDuration = 30.0f;
    private const float DurationEpsilon = 0.05f;
    private static readonly TextInfo TextInfo = CultureInfo.CurrentCulture.TextInfo;
    private static readonly object SoLock = new();

    private const string FallbackTemplate =
        "Go show @{STREAMER} some love, {SUBJECT_WASWERE:lower} last streaming {GAME|something awesome}!";

    // Matches: {TOKEN[:modifier][|default]}
    // Groups: 1=token, 2=modifier?, 3=default?
    private static readonly Regex TokenRe = new(
        @"\{([A-Za-z0-9_]+)(?::([A-Za-z0-9_]+))?(?:\|([^}]*))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Execute()
    {
        string hash     = CPH.GetGlobalVar<string>("ShoutoutHash");
        string clientId = CPH.GetGlobalVar<string>("ShoutoutClientId");
        string scene    = CPH.GetGlobalVar<string>("ShoutoutSceneName");
        string source   = CPH.GetGlobalVar<string>("ShoutoutSourceName");

        CPH.TryGetArg("user", out string user);
        CPH.TryGetArg("targetUser", out string targetUser);
        CPH.TryGetArg("game", out string game);
        CPH.TryGetArg("targetChannelTitle", out string targetChannelTitle);
        CPH.TryGetArg("pronounSubject", out string pronoun);
        CPH.TryGetArg("pronounObject", out string pronounObject);
        CPH.TryGetArg("__source", out string sourceType);
        CPH.TryGetArg("input1", out string showClip);

        if (sourceType == "TwitchFirstWord")
        {
            if (!CPH.GetTwitchUserVar<bool>(user, "autoShoutout"))
                return true;
        }

        var tokens = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["USER"]       = () => user,
            ["STREAMER"]   = () => targetUser,
            ["GAME"]       = () => game,
            ["TITLE"]      = () => targetChannelTitle,
            ["URL"]        = () =>
            {
                if (string.IsNullOrWhiteSpace(user)) return "";
                var u = user.Replace(" ", "");
                return string.Concat("https://twitch.tv/", u.ToLowerInvariant());
            },
            ["PRONOUN_SUBJECT"] = () => string.IsNullOrWhiteSpace(pronoun) ? "they" : pronoun,
            ["PRONOUN_OBJECT"]  = () => string.IsNullOrWhiteSpace(pronounObject) ? "them" : pronounObject,
            ["SUBJECT_WASWERE"] = () =>
            {
                var subj = string.IsNullOrWhiteSpace(pronoun) ? "they" : pronoun.Trim();
                var be   = subj.Equals("they", StringComparison.OrdinalIgnoreCase) ? "were" : "was";
                return string.Concat(subj, " ", be);
            }
        };

        var template = FirstNonEmpty(
            CPH.GetTwitchUserVar<string>(targetUser, "shoutoutTemplate"),
            FallbackTemplate
        );

        var output = RenderTemplate(template, tokens);
        if (!string.IsNullOrWhiteSpace(output))
            CPH.SendMessage(output);

        if (showClip is "noclip" or "no-clip")
            return true;

        try
        {
            if (!string.IsNullOrWhiteSpace(targetUser))
            {
                var (slug, duration) = PickRandomClipSlug(targetUser);
                if (string.IsNullOrEmpty(slug))
                    return Fail("This streamer doesn't have any clips ≤ 30s!", $"No short clips for {targetUser}");

                var (srcUrl, sig, tok) = GetClipInfo(slug, hash, clientId).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(tok))
                    return Fail("I couldn't find that clip! Sadge", "Failed to retrieve clip info from GraphQL");

                CPH.LogInfo($"SO - user={targetUser} clip={slug} dur={duration:0.##}s");

                /*
                    Mimics a queue system so that multiple users can be shouted out without risking
                    the new shoutout cutting the previous clip short.
                */
                lock (SoLock)
                {
                    PlayClip(scene, source, srcUrl!, sig!, tok!, duration);
                }
            }
            else
            {
                CPH.LogWarn("SO - targetUser was empty; skipping clip.");
            }

            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError($"SO - Exception: {ex}");
            return Fail("Something went wrong fetching that clip. Sadge");
        }
    }

    private static string RenderTemplate(string template, IDictionary<string, Func<string>> registry)
    {
        if (string.IsNullOrEmpty(template)) return "";

        return TokenRe.Replace(template, m =>
        {
            var key      = m.Groups[1].Value;
            var modifier = m.Groups[2].Success ? m.Groups[2].Value : null;
            var fallback = m.Groups[3].Success ? m.Groups[3].Value : null;

            string value = null;
            if (registry.TryGetValue(key, out var provider))
                value = provider?.Invoke();

            value = string.IsNullOrWhiteSpace(value) ? fallback : value;
            value = ApplyModifier(value, modifier);

            return value ?? m.Value; // if still null, keep the original token literal
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
            "trim" => value.Trim(),
            _ => value,
        };
    }

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a :
        !string.IsNullOrWhiteSpace(b) ? b : null;

    private (string slug, float duration) PickRandomClipSlug(string username)
    {
        var clips = CPH.GetClipsForUser(username, 50);
        if (clips == null || clips.Count == 0) return (null, 0);

        int count = 0;
        string pickedId = null;
        float pickedDur = 0f;

        foreach (var c in clips)
        {
            if (c == null) continue;
            if (c.Duration > (MaxClipDuration + DurationEpsilon)) continue;

            count++;
            if (Rng.Next(count) == 0)
            {
                pickedId  = c.Id;
                pickedDur = c.Duration + 1.5f;
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

    private async Task<(string sourceUrl, string signature, string token)> GetClipInfo(string clipId, string hash, string clientId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql");
        req.Headers.TryAddWithoutValidation("Client-ID", clientId);

        var payloadObj = new
        {
            operationName = "VideoAccessToken_Clip",
            variables     = new { slug = clipId, platform = "web" },
            extensions    = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        };
        var payload = JsonConvert.SerializeObject(payloadObj);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        CPH.LogInfo($"SO - Payload: {payload}");

        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            CPH.LogError($"SO - GraphQL {resp.StatusCode}: {body}");
            return (null, null, null);
        }

        var json = JObject.Parse(body);
        var pact  = json["data"]?["clip"]?["playbackAccessToken"];
        var sig   = pact?["signature"]?.ToString();
        var token = pact?["value"]?.ToString();

        if (json["data"]?["clip"]?["videoQualities"] is not JArray arr || arr.Count == 0)
            return (null, null, null);

        const int preferredMax = 720;

        (int h, double fps, string url) bestUnder = default;
        (int h, double fps, string url) bestAny  = default;

        foreach (var item in arr)
        {
            var qualityStr = item?["quality"]?.ToString();
            var url        = item?["sourceURL"]?.ToString();
            var fpsStr     = item?["frameRate"]?.ToString();

            if (string.IsNullOrWhiteSpace(qualityStr) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!int.TryParse(qualityStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                continue;

            _ = double.TryParse(fpsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps);

            // Track overall best (highest h, then fps closest to 60)
            if (bestAny.url == null ||
                height > bestAny.h ||
                (height == bestAny.h && Math.Abs(fps - 60.0) < Math.Abs(bestAny.fps - 60.0)))
            {
                bestAny = (height, fps, url);
            }

            if (height <= preferredMax &&
                (bestUnder.url == null ||
                 height > bestUnder.h ||
                 (height == bestUnder.h && Math.Abs(fps - 60.0) < Math.Abs(bestUnder.fps - 60.0))))
            {
                bestUnder = (height, fps, url);
            }
        }

        var pick = bestUnder.url != null ? bestUnder : bestAny;
        if (pick.url == null) return (null, null, null);

        CPH.LogInfo($"SO - Chosen quality: {pick.h}p @ ~{pick.fps:0.##}fps (preferredMax={preferredMax})");
        return (pick.url, sig, token);
    }

    private void PlayClip(string scene, string source, string sourceUrl, string signature, string token, float duration)
    {
        var url   = string.Concat(sourceUrl, "?token=", Uri.EscapeDataString(token), "&sig=", signature);
        var delay = Math.Max(0, (int)(Math.Min(duration, MaxClipDuration) * 1000));

        CPH.LogInfo($"SO - Final URL: {url}");

        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, url);
        CPH.Wait(300);
        CPH.ObsSetSourceVisibility(scene, source, true);
        CPH.Wait(delay);
        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, "");
    }

    private bool Fail(string chatMsg, string logMsg = null)
    {
        if (!string.IsNullOrEmpty(chatMsg)) CPH.SendMessage(chatMsg);
        if (!string.IsNullOrEmpty(logMsg))  CPH.LogError($"SO - {logMsg}");
        return false;
    }
}
