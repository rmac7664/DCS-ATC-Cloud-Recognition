using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DcsAtc;

/// #11 (issue): speech recognition over an OpenAI-compatible endpoint (POST .../v1/audio/transcriptions, multipart: file, model, language, prompt,
/// response_format) - a cloud service or a whisper server on another PC with a GPU, for servers without one. Same prompt as local whisper;
/// confidence from the segments (verbose_json, exp of the mean avg_logprob), otherwise 1. Errors: null -> local whisper for this call, after
/// three in a row local for the rest of the session.
static class AsrApi
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static int fails;

    public static (string Text, double Conf)? Transcribe(string wav, string prompt)
    {
        if (fails >= 3) return null;
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(File.ReadAllBytes(wav));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", Path.GetFileName(wav));
            form.Add(new StringContent(Program.Cfg.AsrApiModel), "model");
            form.Add(new StringContent("en"), "language");
            form.Add(new StringContent(prompt), "prompt");
            form.Add(new StringContent(Program.Cfg.AsrApiModel.StartsWith("whisper") ? "verbose_json" : "json"), "response_format");   // gpt-4o-*-transcribe: json only
            using var req = new HttpRequestMessage(HttpMethod.Post, Program.Cfg.AsrApiUrl) { Content = form };
            if (Program.Cfg.AsrApiKey != "") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Program.Cfg.AsrApiKey);
            using var res = Http.Send(req);
            var body = res.Content.ReadAsStringAsync().Result;
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {body[..Math.Min(200, body.Length)]}");
            using var d = JsonDocument.Parse(body);
            var text = d.RootElement.GetProperty("text").GetString() ?? "";
            double conf = d.RootElement.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array && segs.GetArrayLength() > 0
                ? Math.Exp(segs.EnumerateArray().Average(s => s.GetProperty("avg_logprob").GetDouble())) : 1;
            fails = 0;
            return (text.Trim(), Math.Round(conf, 2));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or AggregateException or JsonException or IOException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            Program.Trace("FEHLER", $"Spracherkennung API {Program.Cfg.AsrApiUrl}: {e.GetType().Name}: {e.Message}");
            if (++fails == 3) Program.Log(L("[ASR] Spracherkennungs-Dienst dreimal fehlgeschlagen, ab jetzt lokal", "[ASR] Speech recognition service failed three times, local from now on"));
            return null;
        }
    }

    /// Self-test: fake endpoint (TcpListener) checks the request, answers verbose_json; text and confidence; error -> null; three errors -> off.
    internal static void SelfTest()
    {
        var (url, key, model) = (Program.Cfg.AsrApiUrl, Program.Cfg.AsrApiKey, Program.Cfg.AsrApiModel);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        string request = "";
        var srv = new Thread(() =>
        {
            for (int n = 0; n < 2; n++)
            {
                using var c = l.AcceptTcpClient();
                var s = c.GetStream();
                var buf = new MemoryStream();
                var b = new byte[65536];
                int len = -1;
                while (len < 0 || buf.Length < len)   // headers, then Content-Length bytes of body
                {
                    int k = s.Read(b, 0, b.Length);
                    if (k == 0) break;
                    buf.Write(b, 0, k);
                    var txt = Encoding.Latin1.GetString(buf.ToArray());
                    int h = txt.IndexOf("\r\n\r\n");
                    if (h >= 0 && len < 0) len = h + 4 + int.Parse(System.Text.RegularExpressions.Regex.Match(txt, @"Content-Length: (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value);
                }
                request = Encoding.Latin1.GetString(buf.ToArray());
                var body = n == 0 ? """{"text":" Kutaisi Tower, Enfield 1-1, ready for departure. ","segments":[{"avg_logprob":-0.1},{"avg_logprob":-0.3}]}""" : """{"error":"bad key"}""";
                s.Write(Encoding.UTF8.GetBytes($"HTTP/1.1 {(n == 0 ? "200 OK" : "401 Unauthorized")}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
            }
        }) { IsBackground = true };
        srv.Start();
        var wav = Path.Combine(Path.GetTempPath(), "dcsatc-asrapi.wav");
        File.WriteAllBytes(wav, new byte[] { 82, 73, 70, 70, 1, 2, 3, 4 });
        try
        {
            (Program.Cfg.AsrApiUrl, Program.Cfg.AsrApiKey, Program.Cfg.AsrApiModel) = ($"http://127.0.0.1:{port}/v1/audio/transcriptions", "sk-test", "whisper-1");
            fails = 0;
            var ok = Transcribe(wav, "Kutaisi Tower, Enfield 1-1");
            var req1 = request;
            var bad = Transcribe(wav, "x");
            srv.Join();
            l.Stop();
            bool off = Transcribe(wav, "x") == null && Transcribe(wav, "x") == null && fails >= 3;   // fake stopped (refused): two more errors -> off
            if (ok is not { Text: "Kutaisi Tower, Enfield 1-1, ready for departure.", Conf: 0.82 } || bad != null || !off
                || !req1.StartsWith("POST /v1/audio/transcriptions") || !req1.Contains("Bearer sk-test") || !req1.Contains("name=model") || !req1.Contains("whisper-1")
                || !req1.Contains("name=prompt") || !req1.Contains("Kutaisi Tower, Enfield 1-1") || !req1.Contains("verbose_json") || !req1.Contains("name=file"))
                throw new Exception($"AsrApi: {ok} | {bad} | off {off} | {req1[..Math.Min(300, req1.Length)]}");
        }
        finally
        {
            l.Stop();
            (Program.Cfg.AsrApiUrl, Program.Cfg.AsrApiKey, Program.Cfg.AsrApiModel) = (url, key, model);
            fails = 0;
            File.Delete(wav);
        }
        Console.WriteLine("OK   #11 Spracherkennung über API: Anfrage mit Modell/Prompt/Schlüssel, Text und Sicherheit, Fehler -> lokal, dreimal -> aus");
    }
}
