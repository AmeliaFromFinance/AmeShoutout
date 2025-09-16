using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

public class CPHInline
{
    private static readonly HttpClient Http = new();
    private static readonly Random Rng = new();
    private const float MaxClipDuration = 30.0f;
    private const float DurationEpsilon = 0.05f;

    public bool Execute()
    {
        try
        {
            string hash       = CPH.GetGlobalVar<string>("ShoutoutHash");
            string clientId   = CPH.GetGlobalVar<string>("ShoutoutClientId");
            string scene      = CPH.GetGlobalVar<string>("ShoutoutSceneName");
            string source     = CPH.GetGlobalVar<string>("ShoutoutSourceName");

            string username   = (string)args["user"];
            string msgId      = (string)args["msgId"];
            string targetUser = (string)args["targetUser"];

            var (slug, duration) = PickRandomClipSlug(targetUser);
            if (string.IsNullOrEmpty(slug))
                return Fail("This streamer doesn't have any clips ≤ 30s!");

            var (srcUrl, sig, tok) = GetClipInfo(slug, hash, clientId).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(tok))
                return Fail("I couldn't find that clip! Sadge", "Failed to retrieve clip info from GraphQL");

            CPH.LogInfo($"AmeSO - user={targetUser} clip={slug} dur={duration:0.##}s");
            PlayClip(scene, source, srcUrl!, sig!, tok!, duration);
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError($"AmeSO - Exception: {ex}");
            return Fail("Something went wrong fetching that clip. Sadge");
        }
    }

    private (string slug, float duration) PickRandomClipSlug(string username)
    {
        var clips = CPH.GetClipsForUser(username, 50);
        if (clips == null || clips.Count == 0) return (null, 0);

        var shortClips = clips
            .Where(c => c != null && c.Duration <= (MaxClipDuration + DurationEpsilon))
            .ToList();

        if (shortClips.Count == 0)
        {
            CPH.LogInfo($"AmeSO - No clips ≤ {MaxClipDuration}s for {username}");
            return (null, 0);
        }

        var pick = shortClips[Rng.Next(shortClips.Count)];
        CPH.LogInfo($"AmeSO - Picked clip id={pick.Id} url={pick.Url} dur={pick.Duration:0.##}");
        return (pick.Id, pick.Duration);
    }

    private async Task<(string sourceUrl, string signature, string token)> GetClipInfo(string clipId, string hash, string clientId)
    {
        Http.DefaultRequestHeaders.Clear();
        Http.DefaultRequestHeaders.Add("Client-ID", clientId);

        var payloadObj = new
        {
            operationName = "VideoAccessToken_Clip",
            variables     = new { slug = clipId, platform = "web" },
            extensions    = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        };

        var payload = JsonConvert.SerializeObject(payloadObj);
        CPH.LogInfo($"AmeSO - Payload: {payload}");

        var resp = await Http.PostAsync("https://gql.twitch.tv/gql",
            new StringContent(payload, Encoding.UTF8, "application/json")).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            CPH.LogError($"AmeSO - GraphQL {resp.StatusCode}: {body}");
            return (null, null, null);
        }

        var json = JObject.Parse(body);

        var pact  = json["data"]?["clip"]?["playbackAccessToken"];
        var sig   = pact?["signature"]?.ToString();
        var token = pact?["value"]?.ToString();

        if (json["data"]?["clip"]?["videoQualities"] is not JArray q || q.Count == 0)
            return (null, null, null);

        int preferredMax = 720;

        var qualities = new List<(int height, double fps, string url)>();
        foreach (var item in q)
        {
            string qualityStr = item?["quality"]?.ToString();
            string url        = item?["sourceURL"]?.ToString();
            string fpsStr     = item?["frameRate"]?.ToString();

            if (string.IsNullOrWhiteSpace(qualityStr) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!int.TryParse(qualityStr, out int height))
                continue;

            _ = double.TryParse(fpsStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double fps);

            qualities.Add((height, fps, url));
        }

        if (qualities.Count == 0) return (null, null, null);

        // Pick highest ≤ preferredMax; else highest overall. Prefer fps near 30.
        (int height, double fps, string url) pick =
            qualities
                .Where(qi => qi.height <= preferredMax)
                .OrderByDescending(qi => qi.height)
                .ThenBy(qi => Math.Abs(qi.fps - 30.0))
                .FirstOrDefault();

        if (string.IsNullOrEmpty(pick.url))
        {
            pick = qualities
                .OrderByDescending(qi => qi.height)
                .ThenBy(qi => Math.Abs(qi.fps - 30.0))
                .First();
        }

        CPH.LogInfo($"AmeSO - Chosen quality: {pick.height}p @ ~{pick.fps:0.##}fps (preferredMax={preferredMax})");

        return (pick.url, sig, token);
    }

    private void PlayClip(string scene, string source, string sourceUrl, string signature, string token, float duration)
    {
        var url   = $"{sourceUrl}?token={Uri.EscapeDataString(token)}&sig={signature}";
        var delay = Math.Max(0, (int)(Math.Min(duration, MaxClipDuration) * 1000));

        CPH.LogInfo($"AmeSO - Final URL: {url}");

        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, url);
        CPH.Wait(500);
        CPH.ObsSetSourceVisibility(scene, source, true);
        CPH.Wait(delay);
        CPH.ObsSetSourceVisibility(scene, source, false);
        CPH.ObsSetMediaSourceFile(scene, source, "");
    }

    private bool Fail(string chatMsg, string logMsg = null)
    {
        if (!string.IsNullOrEmpty(chatMsg)) CPH.SendMessage(chatMsg);
        if (!string.IsNullOrEmpty(logMsg))  CPH.LogError($"AmeSO - {logMsg}");
        return false;
    }
}
