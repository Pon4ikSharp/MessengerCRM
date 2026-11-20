using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace MessengerCRM.Server;

public sealed class WhatsAppService : IAsyncDisposable
{
    private readonly ILogger<WhatsAppService> _log;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:18952/"), Timeout = TimeSpan.FromSeconds(50) };
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _engineProcess;

    public WhatsAppService(ILogger<WhatsAppService> log) => _log = log;

    public async Task<ChannelState> GetStateAsync(bool startIfNeeded = false)
    {
        try
        {
            if (startIfNeeded) await EnsureEngineAsync();
            using var r = await _http.GetAsync("status");
            if (!r.IsSuccessStatusCode) return new("whatsapp", true, false, "engine_unavailable");
            var j = await r.Content.ReadFromJsonAsync<JsonElement>();
            var connected = j.TryGetProperty("connected", out var c) && c.GetBoolean();
            var qrReady = j.TryGetProperty("qrReady", out var q) && q.GetBoolean();
            var err = j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var account = j.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            return new("whatsapp", true, connected, connected ? "connected" : qrReady ? "scan_qr_required" : string.IsNullOrWhiteSpace(err) ? "connecting" : "error: " + err, account);
        }
        catch (Exception ex)
        {
            if (startIfNeeded) _log.LogWarning(ex, "WhatsApp engine state failed");
            return new("whatsapp", true, false, "engine_not_started");
        }
    }

    public async Task StartAsync()
    {
        await EnsureEngineAsync();
        try { await _http.PostAsJsonAsync("start", new { }); } catch { }
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var r = await _http.GetAsync("status");
                if (r.IsSuccessStatusCode)
                {
                    var j = await r.Content.ReadFromJsonAsync<JsonElement>();
                    var connected = j.TryGetProperty("connected", out var c) && c.GetBoolean();
                    var qrReady = j.TryGetProperty("qrReady", out var q) && q.GetBoolean();
                    var err = j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if (connected || qrReady || !string.IsNullOrWhiteSpace(err)) return;
                }
            }
            catch { }
            await Task.Delay(500);
        }
    }

    public async Task<byte[]> ScreenshotAsync()
    {
        await EnsureEngineAsync();
        for (var i = 0; i < 30; i++)
        {
            using var r = await _http.GetAsync("qr.png");
            if (r.IsSuccessStatusCode) return await r.Content.ReadAsByteArrayAsync();
            if (r.StatusCode != System.Net.HttpStatusCode.Conflict)
            {
                var body = await r.Content.ReadAsStringAsync();
                throw new InvalidOperationException("WhatsApp QR unavailable: " + body);
            }
            await Task.Delay(500);
        }
        throw new InvalidOperationException("WhatsApp QR ще генерується. Натисни оновити через кілька секунд.");
    }

    public async Task<PresenceResult> CheckContactAsync(string phone)
    {
        await EnsureEngineAsync();
        try
        {
            using var r = await _http.PostAsJsonAsync("check", new { phone = DatabaseService.NormalizePhone(phone) });
            var body = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
            var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (state == "yes" && root.TryGetProperty("jid", out var j) && j.ValueKind == JsonValueKind.String) detail = j.GetString();
            return new(state, detail);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp WebSocket contact check failed for {Phone}", phone);
            return new("unknown", ex.Message);
        }
    }

    public async Task SaveContactAsync(string phone, string name)
    {
        await EnsureEngineAsync();
        var (firstName, lastName) = SplitContactName(name);
        using var r = await _http.PostAsJsonAsync("save-contact", new { phone = DatabaseService.NormalizePhone(phone), firstName, lastName });
        if (!r.IsSuccessStatusCode)
        {
            var body = await r.Content.ReadAsStringAsync();
            throw new InvalidOperationException("WhatsApp contact save failed: " + body);
        }
    }

    public async Task<WhatsAppSendResult> SendAsync(string phone, string text, string? knownJid = null, string? contactName = null)
    {
        await EnsureEngineAsync();
        var (firstName, lastName) = SplitContactName(contactName);
        using var r = await _http.PostAsJsonAsync("send", new { phone = DatabaseService.NormalizePhone(phone), text, jid = knownJid, firstName, lastName });
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
        {
            try
            {
                using var ed = JsonDocument.Parse(body);
                if (ed.RootElement.TryGetProperty("error", out var er)) body = er.GetString() ?? body;
            }
            catch { }
            throw new InvalidOperationException(body);
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new WhatsAppSendResult(
            root.TryGetProperty("jid", out var j) ? j.GetString() ?? "" : "",
            root.TryGetProperty("messageId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
    }

    public Task<MessageObservation> ObserveMessageAsync(string phone, string text) => Task.FromResult(new MessageObservation(false, false));

    private static (string firstName, string lastName) SplitContactName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.Length == 0) return ("", "");
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }

    private async Task EnsureEngineAsync()
    {
        if (await IsEngineAliveAsync()) return;
        await _startGate.WaitAsync();
        try
        {
            if (await IsEngineAliveAsync()) return;
            var exe = File.Exists(@"C:\Program Files\nodejs\node.exe") ? @"C:\Program Files\nodejs\node.exe" : "node";
            _engineProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "\"C:\\MessengerCRMServer\\wa-engine\\server.mjs\"",
                WorkingDirectory = @"C:\MessengerCRMServer\wa-engine",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(300);
                if (await IsEngineAliveAsync()) return;
                if (_engineProcess?.HasExited == true) break;
            }
            throw new InvalidOperationException("WhatsApp WebSocket engine did not start");
        }
        finally { _startGate.Release(); }
    }

    private async Task<bool> IsEngineAliveAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var r = await _http.GetAsync("health", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _startGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record WhatsAppSendResult(string Jid, string? MessageId);
