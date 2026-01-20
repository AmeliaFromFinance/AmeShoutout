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
public class NewShoutout : CPHInlineBase
#else
public class CPHInline
#endif
{
    private static readonly HttpClient Http = new();
    private static readonly Random Rng = new();
    private static readonly object soLock = new(), twitchLock = new();
    private static DateTime LastTwitchShout = DateTime.MinValue;
    
    private const string LIVE_HASH = "639d5f11bfb8bf3053b424d9ef650d04c4ebb7d94711d644afb08fe9a0fad5d9";
    private const string CLIP_HASH = "9aa558e066a22227c5ef2c0a8fded3aaa57d35181ad15f63df25bff516253a90";
    private const string ACC_HASH = "4195bb9b63a5de46109db204e154d6c20101dc4f4774e968de1755dfb02a80ed";
    private const string CID = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private static readonly Regex TokenRe = new(@"\{(\w+)(?::(\w+))?\}", RegexOptions.Compiled);
    private static readonly Regex SlugRe = new(@"^[A-Za-z0-9-]{10,100}$", RegexOptions.Compiled);

    public bool Execute()
    {
        string scene = Arg<string>("sceneName") ?? "";
        string source = Arg<string>("sourceName") ?? "";
        string textSource = Arg<string>("gdiTextSourceName") ?? "none";
        string type = Arg<string>("shoutoutType")?.ToLower() ?? "message";
        string user = Arg<string>("user");
        string input0 = Arg<string>("input0");
        string sourceType = Arg<string>("__source");
        string? input1 = Arg<string>("input1");
        int days = Arg<int?>("clipDays") ?? 60;
        float length = Arg<float?>("clipLength") ?? 30f;
        bool popular = Arg<bool?>("preferPopular") ?? false;
        bool clips = Arg<bool?>("displayClips") ?? true;
        bool chatBot = Arg<bool?>("useChatBot") ?? false;
        bool raidCount = Arg<bool?>("showRaidCount") ?? true;

        TwitchUserInfoEx targetData = sourceType == "CommandTriggered" ? CPH.TwitchGetExtendedUserInfoByLogin(input0) : CPH.TwitchGetExtendedUserInfoByLogin(user);
        if (targetData == null) return Log($"User data for '{input0 ?? user}' is null.");
        
        string targetUser = targetData.UserName;
        Dictionary<string, object> p = CPH.PronounLookup(targetUser);
        string? pSubject = p != null && p.TryGetValue("pronounSubject", out var sub) ? sub as string : "they";
        string? pObject = p != null && p.TryGetValue("pronounObject", out var obj) ? obj as string : "them";
        string pSubjectLower = pSubject?.ToLower() ?? "they";
        string pObjectLower = pObject?.ToLower() ?? "them";

        CPH.LogDebug($"[Ame-Shoutout] Subject pronoun: '{pSubject}', Object pronoun: '{pObject}' for target user '{targetUser}'.");

        if (sourceType == "TwitchFirstWord" && !CPH.UserInGroup(targetUser, Platform.Twitch, "Auto Shoutout")) return true;

        CPH.LogDebug($"[Ame-Shoutout] Source type: '{sourceType}' for target user '{targetUser}'.");

        bool isLive = IsLive(targetUser).Result;
        
        Dictionary<string, Func<string>> tokens = new(StringComparer.OrdinalIgnoreCase) {
            { "STREAMER", () => targetUser }, { "USER", () => user }, { "GAME", () => targetData.Game },
            { "URL", () => $"https://twitch.tv/{targetUser.ToLower()}" },
            { "LIVE_STATUS", () => $"{pSubject} {(isLive ? (pSubjectLower == "they" ? "are" : "is") : (pSubjectLower == "they" ? "were" : "was"))} {(isLive ? "currently" : "last")} streaming" },
            { "SUBJECT", () => pSubject! }, { "OBJECT", () => pObject! }, { "VERB", () => isLive ? (pSubjectLower == "they" ? "are" : "is") : (pSubjectLower == "they" ? "were" : "was") }
        };
        
        string message = Render(CPH.GetTwitchUserVar<string>(targetUser, "shoutoutTemplate") is string userTemplate && userTemplate.Length > 0 ? userTemplate : Arg<string>("defaultTemplate") ?? "Go show @{STREAMER} some love!", tokens);

        if ((type == "both" || type == "twitch") && !DoTwitchShout(targetUser, chatBot) && type == "twitch") return true;

        if (sourceType == "TwitchRaid" && raidCount) {
            int count = CPH.GetTwitchUserVar<int>(targetUser, "raidCount") + 1;
            CPH.SetTwitchUserVar(targetUser, "raidCount", count);
            message += $" ({(pSubject == "they" ? "They have" : $"{pSubject} has")} raided {count} time{(count == 1 ? "" : "s")}!)";
        }

        if (!string.IsNullOrWhiteSpace(message) && (type == "both" || type == "message")) Send(message, chatBot);

        if (input1?.Equals("no", StringComparison.OrdinalIgnoreCase) == true || !clips) return true;

        string slug = ExtractSlug(input1);
        float duration = 0;
        
        if (!string.IsNullOrEmpty(slug)) {
            float dur = GetClipDuration(slug).Result;
            duration = Math.Min(Math.Max(0, dur) + 1f, length);
        } else {
             (string? slug, float dur, string status) pick = PickClip(targetUser, days, length, popular);
             if (pick.slug == null) return pick.status == "none" ? Log($"No clips for '{targetUser}'") : true;
             slug = pick.slug; duration = pick.dur;
        }

        (string? url, string? signature, string? token) = GetClipData(slug).Result;
        if (string.IsNullOrEmpty(url)) return Log($"Failed to get data for clip '{slug}'");

        CPH.LogInfo($"[Ame-Shoutout] Playing '{slug}' ({duration:0.#}s)");
        lock (soLock) {
            string playUrl = $"{url}?token={Uri.EscapeDataString(token)}&sig={signature}";
            CPH.ObsSetSourceVisibility(scene, source, false);
            CPH.ObsSetMediaSourceFile(scene, source, playUrl);
            if (textSource != "none") CPH.ObsSetGdiText(scene, textSource, Render(Arg<string>("textTemplate") ?? "Go show {STREAMER} some love!", tokens));
            CPH.Wait(300);
            CPH.ObsSetSourceVisibility(scene, source, true);
            if (textSource != "none") CPH.ObsSetSourceVisibility(scene, textSource, true);
            CPH.Wait((int)(duration * 1000));
            CPH.ObsSetSourceVisibility(scene, source, false);
            if (textSource != "none") CPH.ObsSetSourceVisibility(scene, textSource, false);
            CPH.ObsSetMediaSourceFile(scene, source, "");
        }
        return true;
    }

    private (string? slug, float dur, string status) PickClip(string user, int days, float maxLength, bool weighted)
    {
        List<ClipData> list = [.. CPH.GetClipsForUser(user).Where(c => c.CreatedAt >= DateTime.UtcNow.AddDays(-days) && c.Duration <= maxLength)];
        if (list.Count == 0) return (null, 0, "none");
        
        ClipData clip = list[Rng.Next(list.Count)];
        if (weighted) {
            double total = list.Sum(x => x.ViewCount + 1.0);
            double random = Rng.NextDouble() * total;
            foreach (ClipData item in list) {
                random -= item.ViewCount + 1.0;
                if (random <= 0) { clip = item; break; }
            }
        }
        return (clip.Id, clip.Duration + 1f, "ok");
    }

    private bool DoTwitchShout(string login, bool chatBot)
    {
        lock (twitchLock) {
            TimeSpan difference = DateTime.UtcNow - LastTwitchShout;
            if (difference.TotalMinutes < 2) {
                Send($"Shoutout to {login} delayed by {120 - difference.TotalSeconds:0.#}s.", chatBot);
                CPH.Wait((int)(120000 - difference.TotalMilliseconds) + 500);
            }
            if (!CPH.TwitchSendShoutoutByLogin(login.ToLower())) {
                CPH.LogError($"[Ame-Shoutout] Twitch shoutout failed for {login}");
                return false;
            }
            LastTwitchShout = DateTime.UtcNow;
            return true;
        }
    }

    private async Task<(string? u, string? s, string? t)> GetClipData(string slug)
    {
        JObject? obj = await Gql("VideoAccessToken_Clip", ACC_HASH, new { slug, platform = "web" });
        
        JToken? vqToken = Child(Child(Child(obj, "data"), "clip"), "videoQualities");
        if (vqToken is not JArray videoQualities) return ("", "", "");

        var url = videoQualities.Select(x => new { Url = x["sourceURL"]?.ToString(), Quality = (int?)x["quality"], FPS = (double?)x["frameRate"] })
                  .Where(x => x.Url != null && x.Quality.HasValue).OrderByDescending(x => x.Quality).ThenByDescending(x => x.FPS).FirstOrDefault()?.Url;
        return (
            url,
            Child(Child(Child(Child(obj, "data"), "clip"), "playbackAccessToken"), "signature")?.ToString(),
            Child(Child(Child(Child(obj, "data"), "clip"), "playbackAccessToken"), "value")?.ToString()
        );
    }

    private async Task<float> GetClipDuration(string slug)
    {
        JObject? obj = await Gql("ChatClip", CLIP_HASH, new { clipSlug = slug });
        JToken? t = Child(Child(Child(obj, "data"), "clip"), "durationSeconds");
        return float.TryParse(t?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0;
    }

    private async Task<bool> IsLive(string u)
    {
        if (string.IsNullOrWhiteSpace(u)) return false;
        JObject? obj = await Gql("UseLive", LIVE_HASH, new { channelLogin = u });
        JToken? stream = Child(Child(Child(obj, "data"), "user"), "stream");
        return stream?.Type != JTokenType.Null;
    }

    private async Task<JObject?> Gql(string operation, string hash, object variables)
    {
        try {
            using HttpRequestMessage request = new(HttpMethod.Post, "https://gql.twitch.tv/gql");
            request.Headers.TryAddWithoutValidation("Client-ID", CID);
            request.Content = new StringContent(JsonConvert.SerializeObject(new { operationName = operation, variables, extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } } }), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request);
            
            if (!response.IsSuccessStatusCode) return null;
            string json = await response.Content.ReadAsStringAsync();
            JToken token = JToken.Parse(json);
            if (token is JArray array) token = array.FirstOrDefault() ?? token;
            return token as JObject;
        } catch { return null; }
    }

    private string Render(string template, Dictionary<string, Func<string>> tokens) => string.IsNullOrEmpty(template) ? "" : TokenRe.Replace(template, match => {
        if (!tokens.TryGetValue(match.Groups[1].Value, out var func)) return match.Value;
        string value = func() ?? "";
        return match.Groups[2].Value.ToLower() switch { "lower" => value.ToLower(), "upper" => value.ToUpper(), "title" => TitleCase(value), _ => value };
    });

    private string TitleCase(string str) {
        if (string.IsNullOrEmpty(str)) return str;
        HashSet<string> ignore = new(StringComparer.OrdinalIgnoreCase) { "a", "an", "and", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "so", "the", "to", "up", "yet" };
        string[] wordArray = str.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", wordArray.Select((x, i) => (i == 0 || i == wordArray.Length - 1 || !ignore.Contains(x)) ? char.ToUpper(x[0]) + x.Substring(1).ToLower() : x.ToLower()));
    }

    private string ExtractSlug(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url!.Trim('"', '\'');
        if (SlugRe.IsMatch(url)) return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri u) && u.Host.Contains("twitch.tv"))
            return u.AbsolutePath.Trim('/').Split('/').Last();
        return "";
    }

    private void Send(string message, bool chatBot) {
        if (chatBot) { CPH.SetArgument("message", message); CPH.RunAction("[ATB] Send Message"); }
        else CPH.SendMessage(message);
    }
    
    private static JToken? Child(JToken? t, string key) => t is JObject o ? o[key] : null;
    private T Arg<T>(string key) { CPH.TryGetArg(key, out T value); return value; }
    private bool Log(string message) { if (Arg<bool?>("otherMessages") ?? true) Send(message, false); CPH.LogError($"[Ame-Shoutout] {message}"); return false; }
}