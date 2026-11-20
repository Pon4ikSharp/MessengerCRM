namespace MessengerCRM.Server;

public sealed class MessageWorker : BackgroundService
{
    private readonly DatabaseService _db;
    private readonly TelegramService _telegram;
    private readonly WhatsAppService _whatsapp;
    private readonly ILogger<MessageWorker> _log;

    public MessageWorker(DatabaseService db, TelegramService telegram, WhatsAppService whatsapp, ILogger<MessageWorker> log)
    {
        _db = db;
        _telegram = telegram;
        _whatsapp = whatsapp;
        _log = log;
        _db.ResetInterrupted();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var item = _db.GetNextQueued();
            if (item is null)
            {
                await Task.Delay(1200, stoppingToken);
                continue;
            }

            _db.MarkProcessing(item.Id);
            try
            {
                switch (item.Channel)
                {
                    case "telegram":
                    {
                        var sent = await _telegram.SendAsync(item.Phone, item.Text);
                        _db.MarkSent(item.Id, sent.peerId, sent.messageId);
                        _db.MarkContactChannelConfirmed(item.Phone, "telegram");
                        break;
                    }
                    case "whatsapp":
                    {
                        var contact = _db.GetContactByPhone(item.Phone);
                        var knownJid = contact?.WhatsAppJid ?? _db.GetLatestPeerKeyForPhone("whatsapp", item.Phone);
                        var sent = await _whatsapp.SendAsync(item.Phone, item.Text, knownJid, item.ContactName);
                        _db.MarkSentByKey(item.Id, sent.Jid, sent.MessageId);
                        _db.MarkContactChannelConfirmed(item.Phone, "whatsapp");
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unsupported channel: {item.Channel}");
                }

                await Task.Delay(4000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send queued message {MessageId} through {Channel}", item.Id, item.Channel);
                _db.MarkFailed(item.Id, ex.Message);
                await Task.Delay(4000, stoppingToken);
            }
        }
    }
}
