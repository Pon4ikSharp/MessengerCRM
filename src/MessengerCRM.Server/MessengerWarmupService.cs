namespace MessengerCRM.Server;

public sealed class MessengerWarmupService : BackgroundService
{
    private readonly TelegramService _telegram;
    private readonly WhatsAppService _whatsapp;
    private readonly ILogger<MessengerWarmupService> _log;

    public MessengerWarmupService(TelegramService telegram, WhatsAppService whatsapp, ILogger<MessengerWarmupService> log)
    {
        _telegram = telegram;
        _whatsapp = whatsapp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            try
            {
                ChannelState? tgState = null;
                for (var i = 0; i < 10 && !stoppingToken.IsCancellationRequested; i++)
                {
                    tgState = await _telegram.GetStateAsync();
                    if (tgState.Connected) break;
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                if (tgState?.Connected == true) await _telegram.RefreshKnownContactNamesAsync();
            }
            catch (Exception ex) { _log.LogWarning(ex, "Telegram TDLib warm-up failed"); }

            try { await _whatsapp.GetStateAsync(startIfNeeded: true); }
            catch (Exception ex) { _log.LogWarning(ex, "WhatsApp engine warm-up failed"); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
