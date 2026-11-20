using System.Collections.Concurrent;
using QRCoder;
using TdLib;

namespace MessengerCRM.Server;

public sealed class TelegramService : IAsyncDisposable
{
    private const string DatabaseDirectory = @"C:\MessengerCRMServer\data\tdlib";
    private readonly SecretStore _secrets;
    private readonly DatabaseService _db;
    private readonly ILogger<TelegramService> _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private TdClient? _client;
    private TdApi.AuthorizationState? _authState;
    private string? _qrUrl;
    private string? _qrError;
    private string? _pendingPassword;
    private string? _account;
    private TaskCompletionSource<bool> _stateChanged = NewStateTcs();
    private readonly ConcurrentDictionary<(long chatId, long oldId), long> _finalMessageIds = new();
    private IReadOnlyDictionary<int, PresenceResult>? _presenceCache;
    private DateTime _presenceCacheUtc = DateTime.MinValue;
    private DateTime? _contactImportBlockedUntilUtc;

    public TelegramService(SecretStore secrets, DatabaseService db, ILogger<TelegramService> log)
    {
        _secrets = secrets;
        _db = db;
        _log = log;
    }

    public bool IsConfigured => _secrets.Has("telegram.api_id") && _secrets.Has("telegram.api_hash");

    public TelegramPublicConfig GetPublicConfig()
    {
        _ = int.TryParse(_secrets.Get("telegram.api_id"), out var apiId);
        return new TelegramPublicConfig(apiId, _secrets.Get("telegram.phone") ?? "", _secrets.Has("telegram.api_hash"));
    }

    public async Task ConfigureAsync(TelegramConfigRequest request)
    {
        if (request.ApiId <= 0) throw new ArgumentException("API ID is required");
        var oldHash = _secrets.Get("telegram.api_hash");
        var hash = string.IsNullOrWhiteSpace(request.ApiHash) ? oldHash : request.ApiHash.Trim();
        if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("API Hash is required");

        _secrets.Set("telegram.api_id", request.ApiId.ToString());
        _secrets.Set("telegram.api_hash", hash);
        if (!string.IsNullOrWhiteSpace(request.Phone))
            _secrets.Set("telegram.phone", DatabaseService.NormalizePhone(request.Phone));

        await ResetClientAsync(deleteDatabase: false);
    }

    public async Task<ChannelState> GetStateAsync()
    {
        if (!IsConfigured) return new("telegram", false, false, "not_configured");
        try
        {
            await EnsureClientAsync();
            var state = _authState;
            if (state is TdApi.AuthorizationState.AuthorizationStateReady)
                return new("telegram", true, true, "connected", _account);
            if (!string.IsNullOrWhiteSpace(_qrError))
                return new("telegram", true, false, "qr_error: " + _qrError);
            return state switch
            {
                TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation => new("telegram", true, false, "scan_qr_required"),
                TdApi.AuthorizationState.AuthorizationStateWaitPassword => new("telegram", true, false, "password_required"),
                TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber => new("telegram", true, false, "authorization_required"),
                TdApi.AuthorizationState.AuthorizationStateWaitCode => new("telegram", true, false, "code_required"),
                TdApi.AuthorizationState.AuthorizationStateWaitEmailCode => new("telegram", true, false, "email_code_required"),
                TdApi.AuthorizationState.AuthorizationStateWaitRegistration => new("telegram", true, false, "registration_required"),
                _ => new("telegram", true, false, state is null ? "starting" : state.GetType().Name)
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TDLib state check failed");
            return new("telegram", true, false, "error: " + ex.Message);
        }
    }

    public async Task<object> StartQrLoginAsync(string? password = null)
    {
        await EnsureClientAsync();
        _pendingPassword = string.IsNullOrWhiteSpace(password) ? null : password;
        _qrError = null;
        if (_authState is TdApi.AuthorizationState.AuthorizationStateReady)
            return new { ready = true, qrReady = false, error = (string?)null };

        if (_authState is TdApi.AuthorizationState.AuthorizationStateWaitPassword)
        {
            if (string.IsNullOrWhiteSpace(_pendingPassword))
                return new { ready = false, qrReady = false, error = "2FA password required" };
            await SubmitPasswordAsync(_pendingPassword);
        }
        else if (_authState is TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation)
        {
            // A QR request is already pending; keep the current frequently-updated link.
        }
        else
        {
            await _client!.ExecuteAsync(new TdApi.RequestQrCodeAuthentication { OtherUserIds = Array.Empty<long>() });
        }

        for (var i = 0; i < 40; i++)
        {
            if (_authState is TdApi.AuthorizationState.AuthorizationStateReady) break;
            if (!string.IsNullOrWhiteSpace(_qrUrl) || !string.IsNullOrWhiteSpace(_qrError)) break;
            await WaitForStateChangeAsync(TimeSpan.FromMilliseconds(150));
        }

        return new
        {
            ready = _authState is TdApi.AuthorizationState.AuthorizationStateReady,
            qrReady = !string.IsNullOrWhiteSpace(_qrUrl),
            error = _qrError
        };
    }

    public byte[] GetQrPng()
    {
        if (_authState is TdApi.AuthorizationState.AuthorizationStateReady)
            throw new InvalidOperationException("Telegram is already connected");
        if (string.IsNullOrWhiteSpace(_qrUrl))
            throw new InvalidOperationException(_qrError ?? "Telegram QR is not ready yet");
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(_qrUrl, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(18, drawQuietZones: true);
    }

    // Legacy code-login endpoints are kept for backward compatibility only.
    public async Task<object> StartLoginAsync()
    {
        await EnsureClientAsync();
        if (_authState is TdApi.AuthorizationState.AuthorizationStateReady)
            return new { ready = true, prompt = (string?)null, account = _account };
        var phone = _secrets.Get("telegram.phone");
        if (string.IsNullOrWhiteSpace(phone)) return new { ready = false, prompt = "phone_required", account = (string?)null };
        await _client!.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber { PhoneNumber = phone });
        return new { ready = false, prompt = "verification_code", account = (string?)null };
    }

    public Task<object> RestartLoginAsync() => StartLoginAsync();

    public async Task<object> ContinueLoginAsync(string value)
    {
        await EnsureClientAsync();
        if (_authState is TdApi.AuthorizationState.AuthorizationStateWaitPassword)
            await _client!.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = value ?? "" });
        else if (_authState is TdApi.AuthorizationState.AuthorizationStateWaitCode)
            await _client!.ExecuteAsync(new TdApi.CheckAuthenticationCode { Code = value ?? "" });
        await WaitForStateChangeAsync(TimeSpan.FromSeconds(1));
        return new { ready = _authState is TdApi.AuthorizationState.AuthorizationStateReady, prompt = AuthPrompt(), account = _account };
    }

    public async Task<IReadOnlyDictionary<int, PresenceResult>> CheckContactsAsync(IReadOnlyList<ContactRow> contacts)
    {
        await EnsureReadyAsync();
        await _operationGate.WaitAsync();
        try
        {
            var result = new Dictionary<int, PresenceResult>();
            var toImport = new List<ContactRow>();

            // Never re-query contacts that are already confirmed. Recover TDLib user_id
            // from an existing private chat when possible.
            foreach (var c in contacts)
            {
                if (c.TelegramUserId is long knownUserId && knownUserId > 0)
                {
                    result[c.Id] = new PresenceResult("yes", knownUserId.ToString());
                    continue;
                }

                var peerId = _db.GetLatestPeerIdForPhone("telegram", c.Phone);
                if (peerId is long chatId && chatId > 0)
                {
                    long? recoveredUserId = null;
                    try
                    {
                        var chat = await _client!.ExecuteAsync(new TdApi.GetChat { ChatId = chatId });
                        if (chat.Type is TdApi.ChatType.ChatTypePrivate privateChat)
                            recoveredUserId = privateChat.UserId;
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Could not recover Telegram user id from chat {ChatId}", chatId);
                    }
                    result[c.Id] = new PresenceResult("yes", recoveredUserId?.ToString());
                    continue;
                }

                if (c.TelegramState == "limited" && c.ChannelsCheckedAtUtc is DateTime checkedAt)
                {
                    var checkedAtUtc = checkedAt.ToUniversalTime();
                    if (checkedAtUtc > DateTime.UtcNow.AddHours(-1))
                    {
                        result[c.Id] = new PresenceResult("limited", $"Telegram rate-limit cooldown until {checkedAtUtc.AddHours(1):O}");
                        continue;
                    }
                }

                toImport.Add(c);
            }

            if (toImport.Count == 0) return result;

            if (_contactImportBlockedUntilUtc is DateTime blockedUntil && blockedUntil > DateTime.UtcNow)
            {
                foreach (var c in toImport)
                    result[c.Id] = new PresenceResult("limited", $"Telegram contact check temporarily limited until {blockedUntil:O}");
                return result;
            }

            var importedContacts = toImport.Select(c =>
            {
                var (first, last) = SplitContactName(c.Name);
                return new TdApi.ImportedContact
                {
                    PhoneNumber = DatabaseService.NormalizePhone(c.Phone),
                    FirstName = first,
                    LastName = last,
                    Note = null
                };
            }).ToArray();

            var imported = await _client!.ExecuteAsync(new TdApi.ImportContacts { Contacts = importedContacts });
            for (var i = 0; i < toImport.Count; i++)
            {
                var userId = i < imported.UserIds.Length ? imported.UserIds[i] : 0;
                result[toImport[i].Id] = userId == 0
                    ? new PresenceResult("hidden", "Telegram did not return a user for this phone")
                    : new PresenceResult("yes", userId.ToString());
            }
            return result;
        }
        catch (TdException ex)
        {
            var seconds = ParseRetryAfterSeconds(ex.Message);
            if (seconds > 0) _contactImportBlockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            _log.LogWarning(ex, "TDLib batch contact check failed");
            var fallback = new Dictionary<int, PresenceResult>();
            foreach (var c in contacts)
            {
                if (c.TelegramState == "yes")
                    fallback[c.Id] = new PresenceResult("yes", c.TelegramUserId?.ToString());
                else if (seconds > 0)
                    fallback[c.Id] = new PresenceResult("limited", $"Telegram rate limit; retry after {seconds} seconds");
                else
                    fallback[c.Id] = new PresenceResult("error", ex.Message);
            }
            return fallback;
        }
        finally { _operationGate.Release(); }
    }

    public async Task<PresenceResult> CheckContactAsync(string phone)
    {
        var c = _db.GetContactByPhone(phone);
        if (c is null) return new("unknown", "Contact not found in CRM");
        var one = await CheckContactsAsync(new[] { c });
        return one.TryGetValue(c.Id, out var result) ? result : new("unknown");
    }

    public async Task<PresenceResult> SearchUserByPhoneAsync(string phone)
    {
        await EnsureReadyAsync();
        await _operationGate.WaitAsync();
        try
        {
            var normalized = DatabaseService.NormalizePhone(phone);
            if (!DatabaseService.IsPlausiblePhone(normalized))
                return new("error", "Некоректний номер телефону.");
            return await SearchUserByPhoneUnlockedAsync(normalized);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<PresenceResult> SearchUserByPhoneUnlockedAsync(string normalized)
    {
        try
        {
            var user = await _client!.ExecuteAsync(new TdApi.SearchUserByPhoneNumber
            {
                PhoneNumber = normalized,
                OnlyLocal = false
            });
            return user.Id > 0
                ? new PresenceResult("yes", user.Id.ToString())
                : new PresenceResult("no", "Telegram user not found");
        }
        catch (TdException ex)
        {
            var seconds = ParseRetryAfterSeconds(ex.Message);
            if (seconds > 0)
                return new PresenceResult("limited", $"Telegram rate limit; retry after {seconds} seconds");

            var msg = ex.Message ?? "";
            if (msg.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return new PresenceResult("no", msg);

            return new PresenceResult("error", msg);
        }
    }

    public async Task<(long peerId, long messageId)> SendAsync(string phone, string text)
    {
        await EnsureReadyAsync();
        await _operationGate.WaitAsync();
        try
        {
            var normalized = DatabaseService.NormalizePhone(phone);
            var contact = _db.GetContactByPhone(normalized);
            var userId = contact?.TelegramUserId ?? 0;
            long chatId;
            if (userId != 0)
            {
                var chat = await _client!.ExecuteAsync(new TdApi.CreatePrivateChat { UserId = userId, Force = false });
                chatId = chat.Id;
            }
            else
            {
                chatId = _db.GetLatestPeerIdForPhone("telegram", normalized) ?? 0;
                if (chatId == 0)
                {
                    if (contact is null)
                        throw new InvalidOperationException("Контакт не знайдено в CRM.");

                    var lookup = await SearchUserByPhoneUnlockedAsync(normalized);
                    if (lookup.State == "limited")
                        throw new InvalidOperationException(lookup.Detail ?? "Telegram тимчасово обмежив пошук за номером.");
                    if (lookup.State != "yes" || !long.TryParse(lookup.Detail, out userId) || userId <= 0)
                        throw new InvalidOperationException(lookup.State == "no"
                            ? "Telegram не знайшов користувача за цим номером."
                            : $"Не вдалося знайти Telegram-користувача: {lookup.Detail}");

                    _db.UpdateTelegramChannelState(contact.Id, "yes", userId);
                    var chat = await _client!.ExecuteAsync(new TdApi.CreatePrivateChat { UserId = userId, Force = false });
                    chatId = chat.Id;
                }
            }

            var sent = await _client!.ExecuteAsync(new TdApi.SendMessage
            {
                ChatId = chatId,
                InputMessageContent = new TdApi.InputMessageContent.InputMessageText
                {
                    Text = new TdApi.FormattedText { Text = text, Entities = Array.Empty<TdApi.TextEntity>() },
                    ClearDraft = true
                }
            });

            var finalId = sent.Id;
            for (var i = 0; i < 30; i++)
            {
                if (_finalMessageIds.TryRemove((chatId, sent.Id), out var mapped))
                {
                    finalId = mapped;
                    break;
                }
                await Task.Delay(100);
            }
            return (chatId, finalId);
        }
        finally { _operationGate.Release(); }
    }

    private static int ParseRetryAfterSeconds(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return 0;
        var lower = message.ToLowerInvariant();
        var marker = "retry after ";
        var at = lower.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return 0;
        at += marker.Length;
        var digits = new string(lower[at..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var seconds) ? Math.Clamp(seconds, 1, 86400) : 0;
    }

    private static (string first, string last) SplitContactName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.Length == 0) return ("Контакт", "");
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = parts[0];
        var last = parts.Length > 1 ? parts[1] : "";
        if (first.Length > 64) first = first[..64];
        if (last.Length > 64) last = last[..64];
        return (first, last);
    }

    public async Task RefreshKnownContactNamesAsync()
    {
        await EnsureReadyAsync();
        await _operationGate.WaitAsync();
        try
        {
            foreach (var contact in _db.GetContacts().Where(c => c.TelegramState == "yes"))
            {
                try
                {
                    long userId = contact.TelegramUserId ?? 0;
                    if (userId == 0)
                    {
                        var chatId = _db.GetLatestPeerIdForPhone("telegram", contact.Phone);
                        if (chatId is null) continue;
                        var chat = await _client!.ExecuteAsync(new TdApi.GetChat { ChatId = chatId.Value });
                        if (chat.Type is not TdApi.ChatType.ChatTypePrivate privateChat) continue;
                        userId = privateChat.UserId;
                    }
                    var (first, last) = SplitContactName(contact.Name);
                    await _client.ExecuteAsync(new TdApi.AddContact
                    {
                        UserId = userId,
                        Contact = new TdApi.ImportedContact
                        {
                            PhoneNumber = DatabaseService.NormalizePhone(contact.Phone),
                            FirstName = first,
                            LastName = last,
                            Note = null
                        },
                        SharePhoneNumber = false
                    });
                    _db.UpdateTelegramChannelState(contact.Id, "yes", userId);
                    await Task.Delay(250);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Could not refresh Telegram contact name for {Phone}", contact.Phone);
                }
            }
        }
        finally { _operationGate.Release(); }
    }

    private async Task EnsureReadyAsync()
    {
        await EnsureClientAsync();
        if (_authState is not TdApi.AuthorizationState.AuthorizationStateReady)
            throw new InvalidOperationException("Telegram TDLib is not authorized");
    }

    private async Task EnsureClientAsync()
    {
        if (_client is not null) return;
        await _startGate.WaitAsync();
        try
        {
            if (_client is not null) return;
            if (!IsConfigured) throw new InvalidOperationException("Telegram API ID/API Hash are not configured");
            Directory.CreateDirectory(DatabaseDirectory);
            var client = new TdClient();
            client.UpdateReceived += async (_, update) => await ProcessUpdateAsync(update);
            _client = client;

            // TDLib immediately emits the authorization state. Give it a short window
            // so status/QR calls don't race the first update.
            for (var i = 0; i < 30 && _authState is null; i++)
                await WaitForStateChangeAsync(TimeSpan.FromMilliseconds(100));
        }
        finally { _startGate.Release(); }
    }

    private async Task ProcessUpdateAsync(TdApi.Update update)
    {
        try
        {
            switch (update)
            {
                case TdApi.Update.UpdateAuthorizationState a:
                    _authState = a.AuthorizationState;
                    SignalStateChanged();
                    await ProcessAuthorizationStateAsync(a.AuthorizationState);
                    break;

                case TdApi.Update.UpdateNewMessage n when !n.Message.IsOutgoing:
                    if (n.Message.Content is TdApi.MessageContent.MessageText mt)
                        _db.AddIncoming("telegram", n.Message.ChatId, n.Message.Id, mt.Text.Text ?? "");
                    break;

                case TdApi.Update.UpdateChatReadOutbox r:
                    _db.MarkReadThrough("telegram", r.ChatId, r.LastReadOutboxMessageId);
                    break;

                case TdApi.Update.UpdateMessageSendSucceeded s:
                    _finalMessageIds[(s.Message.ChatId, s.OldMessageId)] = s.Message.Id;
                    _db.ReplaceExternalMessageId("telegram", s.Message.ChatId, s.OldMessageId, s.Message.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TDLib update processing failed: {UpdateType}", update.GetType().Name);
        }
    }

    private async Task ProcessAuthorizationStateAsync(TdApi.AuthorizationState state)
    {
        if (_client is null) return;
        switch (state)
        {
            case TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters:
            {
                var apiId = int.Parse(_secrets.Get("telegram.api_id")!);
                var apiHash = _secrets.Get("telegram.api_hash")!;
                await _client.ExecuteAsync(new TdApi.SetTdlibParameters
                {
                    UseTestDc = false,
                    DatabaseDirectory = DatabaseDirectory,
                    FilesDirectory = Path.Combine(DatabaseDirectory, "files"),
                    DatabaseEncryptionKey = Array.Empty<byte>(),
                    UseFileDatabase = true,
                    UseChatInfoDatabase = true,
                    UseMessageDatabase = true,
                    UseSecretChats = false,
                    ApiId = apiId,
                    ApiHash = apiHash,
                    SystemLanguageCode = "uk",
                    DeviceModel = "MessengerCRM Server",
                    SystemVersion = Environment.OSVersion.VersionString,
                    ApplicationVersion = "2.0"
                });
                break;
            }
            case TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation q:
                _qrUrl = q.Link;
                _qrError = null;
                break;
            case TdApi.AuthorizationState.AuthorizationStateWaitPassword:
                _qrUrl = null;
                _qrError = null;
                if (!string.IsNullOrWhiteSpace(_pendingPassword))
                    await SubmitPasswordAsync(_pendingPassword);
                break;
            case TdApi.AuthorizationState.AuthorizationStateReady:
                _qrUrl = null;
                _qrError = null;
                _pendingPassword = null;
                try
                {
                    var me = await _client.ExecuteAsync(new TdApi.GetMe());
                    _account = string.Join(" ", new[] { me.FirstName, me.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
                    if (string.IsNullOrWhiteSpace(_account)) _account = me.PhoneNumber;
                }
                catch { _account = "Telegram"; }
                break;
        }
        SignalStateChanged();
    }

    public async Task<object> CompleteTwoFactorAsync(string password)
    {
        await EnsureClientAsync();
        if (_authState is TdApi.AuthorizationState.AuthorizationStateReady)
            return new { ready = true, error = (string?)null };
        if (_authState is not TdApi.AuthorizationState.AuthorizationStateWaitPassword)
            return new { ready = false, error = "Telegram is not waiting for a 2FA password" };
        if (string.IsNullOrWhiteSpace(password))
            return new { ready = false, error = "Введи пароль двофакторної автентифікації Telegram" };

        _pendingPassword = password;
        await SubmitPasswordAsync(password);
        for (var i = 0; i < 30 && _authState is TdApi.AuthorizationState.AuthorizationStateWaitPassword && string.IsNullOrWhiteSpace(_qrError); i++)
            await WaitForStateChangeAsync(TimeSpan.FromMilliseconds(150));

        return new {
            ready = _authState is TdApi.AuthorizationState.AuthorizationStateReady,
            error = _qrError
        };
    }

    private async Task SubmitPasswordAsync(string password)
    {
        if (_client is null) return;
        try
        {
            await _client.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = password });
            _qrError = null;
        }
        catch (Exception ex)
        {
            _qrError = "2FA: " + ex.Message;
            _log.LogWarning(ex, "Telegram TDLib 2FA failed");
        }
        SignalStateChanged();
    }

    private string? AuthPrompt() => _authState switch
    {
        TdApi.AuthorizationState.AuthorizationStateWaitCode => "verification_code",
        TdApi.AuthorizationState.AuthorizationStateWaitPassword => "password",
        TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber => "phone_number",
        _ => null
    };

    private void SignalStateChanged()
    {
        var old = Interlocked.Exchange(ref _stateChanged, NewStateTcs());
        old.TrySetResult(true);
    }

    private async Task WaitForStateChangeAsync(TimeSpan timeout)
    {
        var task = _stateChanged.Task;
        await Task.WhenAny(task, Task.Delay(timeout));
    }

    private static TaskCompletionSource<bool> NewStateTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ResetClientAsync(bool deleteDatabase)
    {
        await _startGate.WaitAsync();
        try
        {
            if (_client is not null)
            {
                try { _client.Dispose(); } catch { }
                _client = null;
            }
            _authState = null;
            _qrUrl = null;
            _qrError = null;
            _account = null;
            if (deleteDatabase && Directory.Exists(DatabaseDirectory))
            {
                try { Directory.Delete(DatabaseDirectory, true); } catch { }
            }
            await Task.CompletedTask;
        }
        finally { _startGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetClientAsync(deleteDatabase: false);
        _operationGate.Dispose();
        _startGate.Dispose();
    }
}
