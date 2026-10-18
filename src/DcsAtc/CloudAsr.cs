using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

/// Fork (cloud-asr): speech recognition through an OpenAI-compatible transcription endpoint
/// (POST {url} multipart: file, model, language, prompt, response_format) instead of whisper-cli on this PC.
/// Works with api.openai.com (whisper-1, gpt-4o-mini-transcribe, gpt-4o-transcribe) and self-hosted
/// OpenAI-compatible Whisper servers (faster-whisper-server/speaches, whisper.cpp server with --inference-path).
/// No dependency on the rest of the app, so it can be tested on its own (CloudAsrTest).
static class CloudAsr
{
    public record Result(string Text, double Conf, long Ms, string? Error);

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // per-request timeout via CancellationToken

    /// whisper-1 and most self-hosted servers: verbose_json (segments with avg_logprob/no_speech_prob);
    /// gpt-4o*-transcribe: json + include[]=logprobs (token log-probabilities).
    public static bool IsGpt4o(string model) => model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase);

    public static Result Transcribe(string url, string key, string model, string wav, string prompt, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var form = new MultipartFormDataContent();
            var audio = new ByteArrayContent(File.ReadAllBytes(wav));
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", Path.GetFileName(wav));
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("en"), "language");
            form.Add(new StringContent("0"), "temperature");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
            if (IsGpt4o(model)) { form.Add(new StringContent("json"), "response_format"); form.Add(new StringContent("logprobs"), "include[]"); }
            else form.Add(new StringContent("verbose_json"), "response_format");

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var cts = new CancellationTokenSource(Math.Max(500, timeoutMs));
            using var resp = Http.Send(req, cts.Token);
            var body = new StreamReader(resp.Content.ReadAsStream(cts.Token)).ReadToEnd();
            if (!resp.IsSuccessStatusCode)
                return new("", 0, sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}: {Short(ErrorMessage(body))}");
            var (text, conf) = Parse(body, prompt);
            return new(text, conf, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) { return new("", 0, sw.ElapsedMilliseconds, $"timeout after {timeoutMs} ms"); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return new("", 0, sw.ElapsedMilliseconds, $"{e.GetType().Name}: {Short(e.Message)}"); }
    }

    /// Response -> (text, confidence 0–1). Silence/no speech -> "[BLANK_AUDIO]" like whisper.cpp, so the app drops it as before.
    internal static (string Text, double Conf) Parse(string body, string? prompt = null)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";

        double conf = 0.85;   // no probabilities in the response: count as understood (MinConf is 0.65)
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array && segs.GetArrayLength() > 0)
        {
            double sum = 0, weight = 0; bool allSilent = true;
            foreach (var s in segs.EnumerateArray())
            {
                double lp = s.TryGetProperty("avg_logprob", out var a) ? a.GetDouble() : 0;
                double ns = s.TryGetProperty("no_speech_prob", out var n) ? n.GetDouble() : 0;
                double w = Math.Max(1, (s.TryGetProperty("text", out var st) ? st.GetString() ?? "" : "").Length);
                sum += Math.Clamp(1 + 0.35 * lp, 0, 1) * w; weight += w;   // calibrated to whisper.cpp's word confidence: Whisper's own "decode failed" mark (avg_logprob -1.0) -> 0.65 = MinConf
                if (!(ns > 0.6 && lp < -0.8)) allSilent = false;   // Whisper's own no-speech rule
            }
            conf = sum / weight;
            if (allSilent) text = "";
        }
        else if (root.TryGetProperty("logprobs", out var lps) && lps.ValueKind == JsonValueKind.Array && lps.GetArrayLength() > 0)
        {
            var words = new List<double>();   // like Program.Confidence: a word counts with its weakest token
            foreach (var tok in lps.EnumerateArray())
            {
                var s = tok.TryGetProperty("token", out var tt) ? tt.GetString() ?? "" : "";
                if (!s.Any(char.IsLetterOrDigit)) continue;
                double p = Math.Exp(tok.GetProperty("logprob").GetDouble());
                if (s.StartsWith(' ') || words.Count == 0) words.Add(p); else words[^1] = Math.Min(words[^1], p);
            }
            if (words.Count > 0) conf = words.Average();
        }
        text = StripPromptEcho(text, prompt);   // gpt-4o*-transcribe tends to read a long prompt back instead of (or before) the speech
        if (IsHallucination(text)) text = "";
        return (text.Length == 0 ? "[BLANK_AUDIO]" : text, Math.Clamp(conf, 0, 1));
    }

    /// Whisper's well-known output on noise/an open mic without speech.
    static readonly string[] Ghosts = { "you", "thank you", "thanks for watching", "thank you for watching", "bye", ".", "okay" };
    static bool IsHallucination(string text)
    {
        var s = new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        return s.Length == 0 ? text.Length > 0 : Ghosts.Contains(s) && s != "okay";
    }

    /// Removes the prompt when the model returned it (whole or as a prefix); what remains is the actual speech.
    internal static string StripPromptEcho(string text, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || text.Length == 0) return text;
        var p = prompt.Trim();
        int i = text.IndexOf(p, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return (text[..i] + " " + text[(i + p.Length)..]).Trim().TrimStart('.', ',', ' ');
        static string N(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());   // letters and digits only: "start up" = "startup"
        string nt = N(text), np = N(p);
        if (np.Length >= 20 && nt.StartsWith(np[..Math.Min(30, np.Length)]))
        {   // echo with small differences: cut after the prompt's last words (usually the callsign)
            var tail = p.TrimEnd('.', ' ');
            tail = tail[Math.Max(0, tail.LastIndexOf(", ") + 2)..];
            int j = text.LastIndexOf(tail, StringComparison.OrdinalIgnoreCase);
            return j >= 0 ? text[(j + tail.Length)..].Trim().TrimStart('.', ',', ' ') : "";
        }
        return text;
    }

    static string ErrorMessage(string body)
    {
        try { using var d = JsonDocument.Parse(body); return d.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return body; }
    }
    static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// Self-test without network: response parsing (verbose_json, gpt-4o logprobs, silence, errors).
    public static string? SelfTest()
    {
        var v = Parse("{\"text\":\" Kutaisi Tower, Enfield 1-1, ready for departure.\",\"segments\":[{\"text\":\" Kutaisi Tower, Enfield 1-1, ready for departure.\",\"avg_logprob\":-0.12,\"no_speech_prob\":0.01}]}");
        if (v.Text != "Kutaisi Tower, Enfield 1-1, ready for departure." || v.Conf is < 0.94 or > 0.97) return $"verbose_json: {v}";
        var real = Parse("{\"text\":\"Focus, Dodge 5-3, bogey dope.\",\"segments\":[{\"text\":\"Focus, Dodge 5-3, bogey dope.\",\"avg_logprob\":-0.62,\"no_speech_prob\":0.05}]}");
        if (real.Conf < 0.65) return $"clear call (avg_logprob -0.62, first real test) must not ask back: {real}";
        var q = Parse("{\"text\":\"Tower\",\"segments\":[{\"text\":\"Tower\",\"avg_logprob\":-1.3,\"no_speech_prob\":0.2}]}");
        if (q.Conf >= 0.65) return $"unsure segment should be below MinConf: {q}";
        var s = Parse("{\"text\":\" you\",\"segments\":[{\"text\":\" you\",\"avg_logprob\":-1.2,\"no_speech_prob\":0.9}]}");
        if (s.Text != "[BLANK_AUDIO]") return $"silence: {s}";
        var g = Parse("{\"text\":\"Thank you.\"}");
        if (g.Text != "[BLANK_AUDIO]") return $"hallucination: {g}";
        var o = Parse("{\"text\":\"Okay.\"}");
        if (o.Text != "Okay.") return $"okay must stay: {o}";
        var l = Parse("{\"text\":\"Magic, Dodge 5-1, picture.\",\"logprobs\":[{\"token\":\"Magic\",\"logprob\":-0.01},{\"token\":\",\",\"logprob\":0},{\"token\":\" Dodge\",\"logprob\":-0.05},{\"token\":\" 5\",\"logprob\":-0.4},{\"token\":\"-1\",\"logprob\":-0.02},{\"token\":\" picture\",\"logprob\":-0.03}]}");
        if (l.Text != "Magic, Dodge 5-1, picture." || l.Conf is < 0.85 or > 0.95) return $"gpt-4o logprobs: {l}";
        var e = Parse("{\"text\":\"\"}");
        if (e.Text != "[BLANK_AUDIO]") return $"empty: {e}";
        const string pr = "Request startup. Request taxi. Kutaisi Ground, Kutaisi Tower, Enfield 1-1.";
        var echo = Parse("{\"text\":\"Request startup. Request taxi. Kutaisi Ground, Kutaisi Tower, Enfield 1-1.\"}", pr);
        if (echo.Text != "[BLANK_AUDIO]") return $"prompt echo: {echo}";
        var echo2 = Parse("{\"text\":\"Request startup. Request taxi. Kutaisi Ground, Kutaisi Tower, Enfield 1-1. Focus, Dodge 53, declare bullseye 342, 66.\"}", pr);
        if (echo2.Text != "Focus, Dodge 53, declare bullseye 342, 66.") return $"prompt echo + speech: {echo2}";
        var echo3 = Parse("{\"text\":\"Request start up, request taxi, Kutaisi Ground, Kutaisi Tower, Enfield 1-1, Dodge 5-1 radio check.\"}", pr);
        if (echo3.Text != "Dodge 5-1 radio check.") return $"fuzzy prompt echo: {echo3}";
        if (Parse("{\"text\":\"Kutaisi Tower, Enfield 1-1, request taxi.\"}", pr).Text != "Kutaisi Tower, Enfield 1-1, request taxi.") return "normal call must stay";
        if (ErrorMessage("{\"error\":{\"message\":\"Incorrect API key provided\"}}") != "Incorrect API key provided") return "error message";
        return null;
    }
}
