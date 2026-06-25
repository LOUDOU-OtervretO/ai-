using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.IO;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5000"); // 开发阶段绑定本地 HTTP
var app = builder.Build();

// 配置（可通过环境变量覆盖）
string openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new Exception("OPENAI_API_KEY not set");
string openAiBase = Environment.GetEnvironmentVariable("OPENAI_API_BASE") ?? "https://api.openai.com";
string openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4"; // 可改为 gpt-4o 或其他
string execConfirmToken = Environment.GetEnvironmentVariable("EXEC_CONFIRM_TOKEN") ?? "confirm-token-sample";

// in-memory store for actions produced by analyze
var actionsStore = new ConcurrentDictionary<string, string>();

// Simple whitelist (actionId -> allowed command templates)
// In production: load from secure configuration and avoid free-form commands.
var whitelist = new Dictionary<string, string[]>
{
    // template supports placeholder replacement like {pid}
    ["A1"] = new[] { "taskkill /PID {pid} /F" }, 
    ["A2"] = new[] { "powershell -NoProfile -Command \"Get-Process | Sort-Object CPU -Descending | Select -First 5 | Format-Table -AutoSize\"" }
};

// Upload audio endpoint: multipart/form-data with field "audio"
app.MapPost("/api/uploadAudio", async (HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "expect multipart/form-data" });
    var form = await req.ReadFormAsync();
    var file = form.Files["audio"];
    if (file == null) return Results.BadRequest(new { error = "no audio file (form field 'audio')" });

    var folder = Path.Combine(AppContext.BaseDirectory, "uploads");
    Directory.CreateDirectory(folder);
    var filePath = Path.Combine(folder, $"{Guid.NewGuid()}.wav");
    await using (var fs = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(fs);
    }

    // Call OpenAI Whisper transcription
    string transcript = "";
    try
    {
        transcript = await TranscribeWithOpenAIAsync(filePath, openAiKey, openAiBase);
    }
    catch (Exception ex)
    {
        return Results.StatusCode(500, new { error = "transcription_failed", detail = ex.Message });
    }

    return Results.Ok(new { transcript, transcription_engine = "openai-whisper", file = filePath });
});

// Diagnostics endpoint: returns local diagnostics snapshot (basic)
app.MapGet("/api/diagnostics", () =>
{
    var diags = new Dictionary<string, object>();
    diags["cpu_percent"] = GetCpuUsage();
    diags["memory"] = GetMemoryInfo();
    diags["disk"] = GetDiskInfo();
    diags["top_processes"] = GetTopProcesses(8);
    diags["timestamp_utc"] = DateTime.UtcNow;
    return Results.Ok(diags);
});

// Analyze endpoint: accepts { transcript: "...", diagnostics: {...} }
app.MapPost("/api/analyze", async (HttpRequest req) =>
{
    using var sr = new StreamReader(req.Body);
    var body = await sr.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest(new { error = "empty body" });

    JsonNode? incoming;
    try { incoming = JsonNode.Parse(body); }
    catch (Exception ex) { return Results.BadRequest(new { error = "invalid json", detail = ex.Message }); }

    var transcript = incoming?["transcript"]?.ToString() ?? "";
    var diagnosticsNode = incoming?["diagnostics"];
    if (string.IsNullOrWhiteSpace(transcript) || diagnosticsNode == null)
        return Results.BadRequest(new { error = "transcript and diagnostics fields required" });

    // Redact diagnostics before sending to OpenAI
    var diagnosticsJson = diagnosticsNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    var redacted = RedactDiagnostics(diagnosticsJson);

    string rawResponse;
    try
    {
        rawResponse = await AnalyzeWithOpenAIAsync(transcript, redacted, openAiKey, openAiBase, openAiModel);
    }
    catch (Exception ex)
    {
        return Results.StatusCode(500, new { error = "analysis_failed", detail = ex.Message });
    }

    // Ensure the model returned valid JSON object
    JsonDocument parsed;
    try { parsed = JsonDocument.Parse(rawResponse); }
    catch (Exception ex)
    {
        return Results.StatusCode(500, new { error = "llm_returned_non_json", detail = ex.Message, raw = rawResponse });
    }

    var root = parsed.RootElement;
    if (!root.TryGetProperty("issue_summary", out _) || !root.TryGetProperty("recommended_actions", out _))
    {
        return Results.StatusCode(500, new { error = "llm_json_missing_required_fields", raw = rawResponse });
    }

    // Store each recommended action in memory store for later execution
    var actions = root.GetProperty("recommended_actions");
    foreach (var a in actions.EnumerateArray())
    {
        string id = a.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
        actionsStore[id] = a.GetRawText();
    }

    return Results.Ok(JsonSerializer.Deserialize<object>(rawResponse)!);
});

// Execute endpoint: { actionId, confirmToken, parameters (optional) }
app.MapPost("/api/execute", async (HttpRequest req) =>
{
    ExecuteRequest? payload;
    try { payload = await JsonSerializer.DeserializeAsync<ExecuteRequest>(req.Body); }
    catch { return Results.BadRequest(new { error = "invalid json" }); }
    if (payload == null) return Results.BadRequest(new { error = "bad payload" });

    if (string.IsNullOrEmpty(payload.ConfirmToken) || payload.ConfirmToken != execConfirmToken)
        return Results.StatusCode(403, new { status = "denied", reason = "invalid confirm token" });

    if (!actionsStore.TryGetValue(payload.ActionId, out var actionJson))
        return Results.BadRequest(new { status = "denied", reason = "action not found" });

    // parse stored action
    JsonDocument actDoc;
    try { actDoc = JsonDocument.Parse(actionJson); }
    catch { return Results.StatusCode(500, new { error = "stored action corrupt" }); }

    // get commands.windows
    if (!actDoc.RootElement.TryGetProperty("commands", out var commandsEl) ||
        !commandsEl.TryGetProperty("windows", out var windowsEl) || windowsEl.ValueKind != JsonValueKind.Array)
    {
        return Results.BadRequest(new { status = "denied", reason = "action has no windows commands" });
    }

    var cmd = windowsEl[0].GetString() ?? "";
    // Replace parameters (simple templating)
    if (payload.Parameters != null)
    {
        foreach (var kv in payload.Parameters)
        {
            cmd = cmd.Replace("{" + kv.Key + "}", kv.Value);
        }
    }

    // Safety checks: forbid dangerous keywords
    var dangerous = new[] { "format ", "format:", "del ", "rd /s", "rm -rf", "mkfs", ":\\", "shutdown", "reboot", "diskpart", "bcdedit", "sc delete", "reg delete" };
    var lower = cmd.ToLowerInvariant();
    foreach (var d in dangerous)
    {
        if (lower.Contains(d))
            return Results.BadRequest(new { status = "denied", reason = $"command contains forbidden token: {d.Trim()}" });
    }

    // If command matches whitelist templates, allow; otherwise deny
    bool matched = false;
    foreach (var pair in whitelist)
    {
        foreach (var templ in pair.Value)
        {
            // basic check: remove placeholders, compare startswith or contains
            var normalized = templ.Replace("{pid}", "").Trim();
            if (normalized.Length > 0 && cmd.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }
        if (matched) break;
    }
    if (!matched) return Results.BadRequest(new { status = "denied", reason = "command not whitelisted" });

    // Execute
    var result = RunShellCommand(cmd, 10000);
    return Results.Ok(new { status = "ok", stdout = result.stdout, stderr = result.stderr, exit_code = result.exitCode });
});

app.Run();

record ExecuteRequest(string ActionId, string ConfirmToken, Dictionary<string, string>? Parameters);

// -------------------- Helper methods --------------------

static string RedactDiagnostics(string diagnosticsJson)
{
    var s = diagnosticsJson;
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d{1,3}(\.\d{1,3}){3}\b", "[REDACTED_IP]");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", "[REDACTED_EMAIL]");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"[A-Za-z]:\\[^\r\n]+", "[REDACTED_PATH]");
    return s;
}

static async Task<string> TranscribeWithOpenAIAsync(string filePath, string openAiKey, string openAiBase)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    using var mp = new MultipartFormDataContent();
    var fileStream = File.OpenRead(filePath);
    var streamContent = new StreamContent(fileStream);
    streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
    mp.Add(streamContent, "file", Path.GetFileName(filePath));
    mp.Add(new StringContent("whisper-1"), "model");

    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAiKey);
    var url = $"{openAiBase.TrimEnd('/')}/v1/audio/transcriptions";
    var resp = await http.PostAsync(url, mp);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new Exception($"OpenAI STT failed: {resp.StatusCode} {body}");

    using var doc = JsonDocument.Parse(body);
    if (doc.RootElement.TryGetProperty("text", out var t)) return t.GetString() ?? "";
    // fallback
    return "";
}

static async Task<string> AnalyzeWithOpenAIAsync(string transcript, string diagnosticsJson, string openAiKey, string openAiBase, string model = "gpt-4")
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAiKey);

    var systemPrompt = @"
You are a Windows system diagnosis assistant. Input includes a user's speech transcript and a diagnostics snapshot.
Return EXACTLY one JSON object (no surrounding text) with these properties:
- issue_summary (string)
- probable_causes (array of { cause:string, confidence:number (0-1), evidence: array[string] })
- recommended_actions (array of actions)
- confidence (number 0-1)

Each action must be:
{ 
  id: string,
  title: string,
  description: string,
  commands: { windows: [string] }, 
  risk: 'low'|'medium'|'high',
  rollback: string,
  estimated_time: string,
  evidence: [string]
}

Do not include any markdown or explanation — ONLY the JSON object. If uncertain, include lower confidence numbers. Keep JSON parseable.
";

    var userPrompt = $"transcript: \"{EscapeForPrompt(transcript)}\"\n\nDiagnostics: {diagnosticsJson}\n\nReturn the JSON described.";

    var payload = new
    {
        model = model,
        messages = new[] {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        max_tokens = 1200,
        temperature = 0.0
    };

    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    var url = $"{openAiBase.TrimEnd('/')}/v1/chat/completions";
    var resp = await http.PostAsync(url, content);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new Exception($"OpenAI analyze failed: {resp.StatusCode} {body}");

    using var doc = JsonDocument.Parse(body);
    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
    {
        var msg = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        msg = StripCodeFence(msg).Trim();
        return msg;
    }
    throw new Exception("OpenAI response missing choices");
}

static string EscapeForPrompt(string s) => s.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

static string StripCodeFence(string s)
{
    var m = System.Text.RegularExpressions.Regex.Match(s, @"^```(?:json)?\s*(.*)```$", System.Text.RegularExpressions.RegexOptions.Singleline);
    if (m.Success) return m.Groups[1].Value;
    return s;
}

static (string stdout, string stderr, int exitCode) RunShellCommand(string cmd, int timeoutMs = 5000)
{
    var psi = new ProcessStartInfo("cmd.exe", $"/C {cmd}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(timeoutMs))
    {
        try { p.Kill(); } catch { }
    }
    return (stdout, stderr, p.ExitCode);
}

static object GetCpuUsage()
{
    try
    {
        using var pc = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total", true);
        pc.NextValue();
        System.Threading.Thread.Sleep(500);
        return pc.NextValue();
    }
    catch
    {
        return -1;
    }
}

static object GetMemoryInfo()
{
    try
    {
        var pc = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
        var avail = pc.NextValue();
        return new { available_mb = avail };
    }
    catch
    {
        return new { available_mb = -1 };
    }
}

static object GetDiskInfo()
{
    try
    {
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { d.Name, d.TotalSize, d.TotalFreeSpace });
        return drives;
    }
    catch
    {
        return Array.Empty<object>();
    }
}

static object GetTopProcesses(int n)
{
    try
    {
        var procs = Process.GetProcesses()
            .Select(p => {
                try { return new { pid = p.Id, name = p.ProcessName }; }
                catch { return null; }
            })
            .Where(x => x != null).Take(n);
        return procs;
    }
    catch
    {
        return Array.Empty<object>();
    }
}
