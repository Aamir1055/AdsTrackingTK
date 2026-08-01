using System.Text.Json;

namespace AdsTracking.Api.Services;

public class TelegramBotService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _channelUsername;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(IConfiguration config, ILogger<TelegramBotService> logger)
    {
        _httpClient = new HttpClient();
        _botToken = config["Telegram:BotToken"] ?? "";
        _channelUsername = config["Telegram:ChannelId"] ?? config["Telegram:ChannelUsername"] ?? "";
        _logger = logger;
    }

    /// <summary>
    /// Gets the current member count of the Telegram channel.
    /// </summary>
    public async Task<int?> GetChannelMemberCountAsync()
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_channelUsername))
        {
            _logger.LogWarning("Telegram bot token or channel ID not configured.");
            return null;
        }

        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/getChatMemberCount?chat_id={_channelUsername}";
            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("ok").GetBoolean())
            {
                return root.GetProperty("result").GetInt32();
            }

            _logger.LogWarning("Telegram API returned not-ok: {Response}", json);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Telegram channel member count.");
            return null;
        }
    }
}
