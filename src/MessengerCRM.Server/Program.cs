using ClosedXML.Excel;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using MessengerCRM.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:18950");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "mcrm.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("login", x =>
{
    x.PermitLimit = 10;
    x.Window = TimeSpan.FromMinutes(1);
    x.QueueLimit = 0;
    x.AutoReplenishment = true;
}));

builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<ExcelImportService>();
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<WhatsAppService>();
builder.Services.AddHostedService<MessageWorker>();
builder.Services.AddHostedService<MessengerWarmupService>();

var app = builder.Build();
var apiToken = EnsureApiToken();
var engineToken = EnsureEngineToken();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Ð¡Ñ‚Ð°Ñ€Ð¸Ð¹ Windows-ÐºÐ»Ñ–Ñ”Ð½Ñ‚ Ð¼Ð¾Ð¶Ðµ Ð¿Ñ€Ð¾Ð´Ð¾Ð²Ð¶ÑƒÐ²Ð°Ñ‚Ð¸ Ð¿Ñ€Ð°Ñ†ÑŽÐ²Ð°Ñ‚Ð¸ Ñ‡ÐµÑ€ÐµÐ· Ð¾ÐºÑ€ÐµÐ¼Ð¸Ð¹ API-ÐºÐ»ÑŽÑ‡.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(supplied),
                System.Text.Encoding.UTF8.GetBytes(apiToken)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new
{
    service = "MessengerCRM.Server",
    ok = true,
    timeUtc = DateTime.UtcNow
}));

// ÐŸÐµÑ€Ð²Ð¸Ð½Ð½Ðµ ÑÑ‚Ð²Ð¾Ñ€ÐµÐ½Ð½Ñ Ð»Ð¾Ð³Ñ–Ð½Ð°/Ð¿Ð°Ñ€Ð¾Ð»Ñ Ð·Ð°Ñ…Ð¸Ñ‰ÐµÐ½Ðµ Ð¾Ð´Ð½Ð¾Ñ€Ð°Ð·Ð¾Ð²Ð¸Ð¼ setup-code.
app.MapGet("/webapi/setup/status", (AuthService auth) => Results.Ok(new { configured = auth.IsConfigured }))
    .AllowAnonymous();
app.MapPost("/webapi/setup", (SetupRequest request, AuthService auth) =>
{
    auth.Setup(request.SetupCode, request.Username, request.Password);
    return Results.Ok(new { ok = true });
}).AllowAnonymous().RequireRateLimiting("login");

app.MapPost("/webapi/login", async (LoginRequest request, AuthService auth, HttpContext http) =>
{
    if (!auth.IsConfigured)
        return Results.Conflict(new { error = "Ð¡Ð¿Ð¾Ñ‡Ð°Ñ‚ÐºÑƒ ÑÑ‚Ð²Ð¾Ñ€Ñ–Ñ‚ÑŒ Ð»Ð¾Ð³Ñ–Ð½ Ñ– Ð¿Ð°Ñ€Ð¾Ð»ÑŒ." });
    if (!auth.Validate(request.Username, request.Password))
        return Results.Unauthorized();

    var claims = new[] { new Claim(ClaimTypes.Name, request.Username.Trim()) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    return Results.Ok(new { ok = true, user = request.Username.Trim() });
}).AllowAnonymous().RequireRateLimiting("login");

var web = app.MapGroup("/webapi").RequireAuthorization();

web.MapGet("/me", (HttpContext c) => Results.Ok(new { user = c.User.Identity?.Name ?? "" }));
web.MapPost("/logout", async (HttpContext c) =>
{
    await c.SignOutAsync();
    return Results.Ok(new { ok = true });
});
web.MapPost("/password", (ChangePasswordRequest r, AuthService auth, HttpContext c) =>
{
    auth.ChangePassword(c.User.Identity?.Name ?? "", r.CurrentPassword, r.NewPassword);
    return Results.Ok(new { ok = true });
});

web.MapGet("/status", async (TelegramService tg, WhatsAppService wa) => Results.Ok(new
{
    telegram = await tg.GetStateAsync(),
    whatsapp = await wa.GetStateAsync()
}));

web.MapPost("/telegram/qr/start", async (TelegramQrStartRequest request, TelegramService tg) =>
    Results.Ok(await tg.StartQrLoginAsync(request.Password)));
web.MapGet("/telegram/qr", (TelegramService tg) =>
{
    try { return Results.File(tg.GetQrPng(), "image/png"); }
    catch (Exception ex) { return Results.Conflict(new { error = ex.Message }); }
});
web.MapPost("/telegram/password", async (TelegramPasswordRequest request, TelegramService tg) =>
    Results.Ok(await tg.CompleteTwoFactorAsync(request.Password)));
web.MapPost("/telegram/search-phone", async (PhoneLookupRequest request, TelegramService tg) =>
    Results.Ok(await tg.SearchUserByPhoneAsync(request.Phone)));

web.MapGet("/contacts", (bool? includeArchived, DatabaseService db) => Results.Ok(db.GetContacts(includeArchived ?? false)));
web.MapPost("/contacts/check-channels", async (ContactCheckRequest r, DatabaseService db, WhatsAppService wa) =>
{
    var ids = (r.ContactIds ?? Array.Empty<int>()).Distinct().Where(x => x > 0).Take(25).ToArray();
    if (ids.Length == 0) return Results.BadRequest(new { error = "Не вибрано контакти." });
    var contacts = db.GetContactsByIds(ids);

    var waYes = 0; var waNo = 0; var waUnknown = 0;
    foreach (var c in contacts)
    {
        var waResult = await wa.CheckContactAsync(c.Phone);
        db.UpdateWhatsAppChannelState(c.Id, waResult.State, waResult.State == "yes" ? waResult.Detail : null);
        if (waResult.State == "yes")
        {
            waYes++;
            try { await wa.SaveContactAsync(c.Phone, c.Name); } catch { }
        }
        else if (waResult.State == "no") waNo++;
        else waUnknown++;
        await Task.Delay(200);
    }

    return Results.Ok(new { checkedCount = contacts.Count, whatsapp = new { yes = waYes, no = waNo, unknown = waUnknown } });
});
web.MapPost("/contacts", (ContactCreateRequest r, DatabaseService db) =>
{
    try
    {
        return Results.Ok(new { id = db.AddContact(r) });
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { error = "ÐšÐ¾Ð½Ñ‚Ð°ÐºÑ‚ Ð· Ñ‚Ð°ÐºÐ¸Ð¼ Ð½Ð¾Ð¼ÐµÑ€Ð¾Ð¼ ÑƒÐ¶Ðµ Ñ”." });
    }
});
web.MapPost("/contacts/{id:int}/archive", (int id, DatabaseService db) =>
{
    db.ArchiveContact(id, true);
    return Results.Ok(new { ok = true });
});
web.MapPost("/contacts/{id:int}/restore", (int id, DatabaseService db) =>
{
    db.ArchiveContact(id, false);
    return Results.Ok(new { ok = true });
});
web.MapDelete("/contacts/{id:int}", (int id, DatabaseService db) =>
    db.DeleteContact(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Контакт не знайдено." }));
web.MapPost("/contacts/import", async (HttpRequest request, ExcelImportService importer) =>
{
    try
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Excel file expected." });

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "File was not selected." });
        if (file.Length > 25 * 1024 * 1024)
            return Results.BadRequest(new { error = "Excel file is too large (max 25 MB)." });

        var ext = Path.GetExtension(file.FileName);
        if (!ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Supported formats: .xlsx and .xlsm." });

        await using var stream = file.OpenReadStream();
        return Results.Ok(importer.Import(stream));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

web.MapPost("/messages/bulk", (BulkSendRequest r, DatabaseService db) =>
{
    var channel = (r.Channel ?? "").Trim().ToLowerInvariant();
    if (channel is not ("telegram" or "whatsapp"))
        return Results.BadRequest(new { error = "ÐÐµÐ²Ñ–Ð´Ð¾Ð¼Ð¸Ð¹ ÐºÐ°Ð½Ð°Ð»." });
    if (string.IsNullOrWhiteSpace(r.Text))
        return Results.BadRequest(new { error = "Ð’Ð²ÐµÐ´Ñ–Ñ‚ÑŒ Ñ‚ÐµÐºÑÑ‚ Ð¿Ð¾Ð²Ñ–Ð´Ð¾Ð¼Ð»ÐµÐ½Ð½Ñ." });
    if (r.ContactIds is null || r.ContactIds.Length == 0)
        return Results.BadRequest(new { error = "ÐÐµ Ð²Ð¸Ð±Ñ€Ð°Ð½Ð¾ ÐºÐ¾Ð½Ñ‚Ð°ÐºÑ‚Ð¸." });
    if (r.ContactIds.Length > 1000)
        return Results.BadRequest(new { error = "Ð—Ð° Ð¾Ð´Ð¸Ð½ Ð·Ð°Ð¿ÑƒÑÐº Ð¼Ð¾Ð¶Ð½Ð° Ð¿Ð¾ÑÑ‚Ð°Ð²Ð¸Ñ‚Ð¸ Ð² Ñ‡ÐµÑ€Ð³Ñƒ Ð´Ð¾ 1000 ÐºÐ¾Ð½Ñ‚Ð°ÐºÑ‚Ñ–Ð²." });

    var contacts = db.GetContactsByIds(r.ContactIds);
    var count = 0;
    foreach (var c in contacts)
    {
        var text = r.Text.Replace("{Name}", c.Name, StringComparison.OrdinalIgnoreCase);
        db.Enqueue(new SendMessageRequest(c.Name, c.Phone, channel, text));
        count++;
    }
    return Results.Ok(new { queued = count });
});
web.MapGet("/messages/recent", (int? limit, DatabaseService db) => Results.Ok(db.GetRecent(limit ?? 100)));
web.MapGet("/messages/summary", (DatabaseService db) => Results.Ok(db.GetSummary()));
web.MapPost("/messages/delete", (MessageDeleteRequest r, DatabaseService db) =>
{
    var deleted = db.DeleteMessages(r.MessageIds ?? Array.Empty<string>());
    return Results.Ok(new { deleted });
});
web.MapPost("/messages/clear", (DatabaseService db) =>
{
    var deleted = db.ClearMessageHistory();
    return Results.Ok(new { deleted });
});
web.MapPost("/messages/export", (ExportRequest r, DatabaseService db) =>
{
    var rows = db.GetExportResults(r.ContactIds);
    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("Результати");
    ws.Cell(1, 1).Value = "ФІО";
    ws.Cell(1, 2).Value = "Телефон";
    ws.Cell(1, 3).Value = "Результат";
    var row = 2;
    foreach (var x in rows)
    {
        ws.Cell(row, 1).Value = x.Name;
        ws.Cell(row, 2).Value = x.Phone;
        ws.Cell(row, 3).Value = x.Result;
        row++;
    }
    var header = ws.Range(1, 1, 1, 3);
    header.Style.Font.Bold = true;
    header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF8");
    header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    ws.Column(1).Width = 30;
    ws.Column(2).Width = 20;
    ws.Column(3).Width = 28;
    ws.SheetView.FreezeRows(1);
    if (row > 2) ws.Range(1, 1, row - 1, 3).CreateTable("ResultsTable");
    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    var name = $"MessengerCRM_results_{DateTime.Now:yyyy-MM-dd_HH-mm}.xlsx";
    return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
});

web.MapPost("/whatsapp/start", async (WhatsAppService wa) =>
{
    await wa.StartAsync();
    return Results.Ok(await wa.GetStateAsync());
});
web.MapGet("/whatsapp/screenshot", async (WhatsAppService wa) =>
    Results.File(await wa.ScreenshotAsync(), "image/png"));

app.MapPost("/internal/whatsapp/incoming", (WhatsAppIncomingRequest request, DatabaseService db, HttpContext http) =>
{
    if (!EngineAuthorized(http, engineToken)) return Results.Unauthorized();
    db.AddIncomingByKey("whatsapp", request.Jid ?? "", request.MessageId, request.Text ?? "", request.Phone);
    if (!string.IsNullOrWhiteSpace(request.Phone)) db.MarkContactChannelConfirmed(request.Phone, "whatsapp");
    return Results.Ok(new { ok = true });
}).AllowAnonymous();

app.MapPost("/internal/whatsapp/read", (WhatsAppReadRequest request, DatabaseService db, HttpContext http) =>
{
    if (!EngineAuthorized(http, engineToken)) return Results.Unauthorized();
    if (!string.IsNullOrWhiteSpace(request.MessageId)) db.MarkReadByExternalKey("whatsapp", request.MessageId);
    return Results.Ok(new { ok = true });
}).AllowAnonymous();

// Legacy API Ð´Ð»Ñ Ð¿Ð¾Ð¿ÐµÑ€ÐµÐ´Ð½ÑŒÐ¾Ð³Ð¾ Windows-ÐºÐ»Ñ–Ñ”Ð½Ñ‚Ð°.
app.MapGet("/api/status", async (TelegramService tg, WhatsAppService wa) => Results.Ok(new
{
    telegram = await tg.GetStateAsync(),
    whatsapp = await wa.GetStateAsync()
}));
app.MapGet("/api/telegram/config", (TelegramService tg) => Results.Ok(tg.GetPublicConfig()));
app.MapPost("/api/telegram/config", async (TelegramConfigRequest request, TelegramService tg) =>
{
    await tg.ConfigureAsync(request);
    return Results.Ok(new { saved = true });
});
app.MapPost("/api/telegram/login/start", async (TelegramService tg) => Results.Ok(await tg.StartLoginAsync()));
app.MapPost("/api/telegram/login/restart", async (TelegramService tg) => Results.Ok(await tg.RestartLoginAsync()));
app.MapPost("/api/telegram/login", async (TelegramLoginRequest request, TelegramService tg) => Results.Ok(await tg.ContinueLoginAsync(request.Value)));
app.MapPost("/api/telegram/search-phone", async (PhoneLookupRequest request, TelegramService tg) => Results.Ok(await tg.SearchUserByPhoneAsync(request.Phone)));
app.MapPost("/api/telegram/search-contact/{id:int}", async (int id, DatabaseService db, TelegramService tg) =>
{
    var contact = db.GetContactsByIds(new[] { id }).FirstOrDefault();
    if (contact is null) return Results.NotFound(new { error = "Контакт не знайдено." });
    var result = await tg.SearchUserByPhoneAsync(contact.Phone);
    return Results.Ok(new { contact.Id, contact.Name, result.State, result.Detail });
});
app.MapPost("/api/telegram/qr/start", async (TelegramService tg) => Results.Ok(await tg.StartQrLoginAsync()));
app.MapGet("/api/telegram/qr", (TelegramService tg) =>
{
    try { return Results.File(tg.GetQrPng(), "image/png"); }
    catch (Exception ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/whatsapp/start", async (WhatsAppService wa) =>
{
    await wa.StartAsync();
    return Results.Ok(await wa.GetStateAsync());
});
app.MapGet("/api/whatsapp/status", async (WhatsAppService wa) => Results.Ok(await wa.GetStateAsync()));
app.MapGet("/api/whatsapp/screenshot", async (WhatsAppService wa) => Results.File(await wa.ScreenshotAsync(), "image/png"));
app.MapPost("/api/messages/enqueue", (SendMessageRequest r, DatabaseService db) =>
    Results.Ok(new { id = db.Enqueue(r), status = "queued" }));
app.MapGet("/api/messages/recent", (int? limit, DatabaseService db) => Results.Ok(db.GetRecent(limit ?? 200)));
app.MapGet("/api/messages/summary", (DatabaseService db) => Results.Ok(db.GetSummary()));

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("MessengerCRM Server listening on http://127.0.0.1:18950"));

await app.RunAsync();

static bool EngineAuthorized(HttpContext http, string token)
{
    var supplied = http.Request.Headers["X-Engine-Token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(supplied) || supplied.Length != token.Length) return false;
    return CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(supplied),
        System.Text.Encoding.UTF8.GetBytes(token));
}

static string EnsureEngineToken()
{
    var path = @"C:\MessengerCRMServer\data\engine-token.txt";
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (File.Exists(path))
    {
        var existing = File.ReadAllText(path).Trim();
        if (existing.Length >= 32) return existing;
    }
    var bytes = RandomNumberGenerator.GetBytes(32);
    var token = Convert.ToHexString(bytes);
    File.WriteAllText(path, token);
    return token;
}

static string EnsureApiToken()
{
    var path = @"C:\MessengerCRMServer\data\api-token.txt";
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (File.Exists(path))
    {
        var existing = File.ReadAllText(path).Trim();
        if (existing.Length >= 32) return existing;
    }

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    File.WriteAllText(path, token);
    return token;
}

