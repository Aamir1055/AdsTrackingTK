using System.Text.Json;
using AdsTracking.Api.Data;
using Dapper;
using System.Text.RegularExpressions;

namespace AdsTracking.Api.Services;

public class TelegramMessage
{
    public int MessageId { get; set; }
    public string Text { get; set; } = "";
    public string PhotoUrl { get; set; } = "";
    public DateTime Date { get; set; }
}

public class TelegramMessagesService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _channelId;
    private readonly string _channelUsername;
    private readonly DbConnectionFactory _dbFactory;
    private readonly ILogger<TelegramMessagesService> _logger;
    private int _lastUpdateId = 0;
    private DateTime _lastReconcileUtc = DateTime.MinValue;

    public TelegramMessagesService(IConfiguration config, DbConnectionFactory dbFactory, ILogger<TelegramMessagesService> logger)
    {
        _httpClient = new HttpClient();
        _botToken = config["Telegram:BotToken"] ?? "";
        _channelId = config["Telegram:ChannelId"] ?? "";
        _channelUsername = ResolveChannelUsername(config);
        _dbFactory = dbFactory;
        _logger = logger;

        // Seed known IDs on startup if table is empty, then start polling
        _ = InitAndPollAsync();
    }

    /// <summary>
    /// Returns the latest N message IDs from the database.
    /// </summary>
    public async Task<List<int>> GetRecentMessageIdsAsync(int count = 10)
    {
        using var conn = _dbFactory.CreateConnection();
        var ids = await conn.QueryAsync<int>(
            "SELECT message_id FROM channel_messages ORDER BY message_id DESC LIMIT @Count",
            new { Count = count });
        return ids.ToList();
    }

    public async Task<List<TelegramMessage>> GetRecentMessagesAsync(int count = 10)
    {
        using var conn = _dbFactory.CreateConnection();
        var msgs = await conn.QueryAsync<TelegramMessage>(
            "SELECT message_id AS MessageId, message_text AS Text, photo_url AS PhotoUrl, timestamp_utc AS Date FROM channel_messages ORDER BY message_id DESC LIMIT @Count",
            new { Count = count });
        return msgs.ToList();
    }

    private async Task InitAndPollAsync()
    {
        await Task.Delay(3000); // Wait for DB init

        // Keep existing messages in DB, only add new ones from the configured channel
        _logger.LogInformation("Starting Telegram message polling for channel {ChannelId}.", _channelId);

        // Start continuous polling — real messages will be stored as they arrive
        while (true)
        {
            try
            {
                await PollNewMessagesAsync();

                // Reconcile every 5 minutes so deleted Telegram posts do not linger in local DB.
                if ((DateTime.UtcNow - _lastReconcileUtc).TotalMinutes >= 5)
                {
                    await ReconcileWithTelegramPublicFeedAsync();
                    _lastReconcileUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling Telegram");
            }
            await Task.Delay(5000); // Poll every 5 seconds
        }
    }

    private async Task PollNewMessagesAsync()
    {
        var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?offset={_lastUpdateId + 1}&limit=100&allowed_updates=[\"channel_post\",\"edited_channel_post\"]";
        var response = await _httpClient.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean()) return;

        var results = root.GetProperty("result");

        foreach (var update in results.EnumerateArray())
        {
            var updateId = update.GetProperty("update_id").GetInt32();
            if (updateId > _lastUpdateId)
                _lastUpdateId = updateId;

            var hasChannelPost = update.TryGetProperty("channel_post", out var post);
            var hasEditedChannelPost = update.TryGetProperty("edited_channel_post", out var editedPost);

            if (!hasChannelPost && !hasEditedChannelPost)
                continue;

            if (!hasChannelPost)
                post = editedPost;

            // Only process messages from our configured channel
            var chatId = post.GetProperty("chat").GetProperty("id").GetInt64().ToString();
            if (chatId != _channelId)
                continue;

            // Skip videos, animations/GIFs, and documents — we only show text and photos
            if (post.TryGetProperty("video", out _) || post.TryGetProperty("animation", out _) || post.TryGetProperty("document", out _))
                continue;

            var messageId = post.GetProperty("message_id").GetInt32();
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(post.GetProperty("date").GetInt64()).UtcDateTime;
            var text = post.TryGetProperty("text", out var t) ? t.GetString() ?? "" :
                       post.TryGetProperty("caption", out var c) ? c.GetString() ?? "" : "";

            // Get photo URL if present
            var photoUrl = "";
            if (post.TryGetProperty("photo", out var photos))
            {
                // Get the largest photo (last in array)
                var photoArray = photos.EnumerateArray().ToList();
                if (photoArray.Count > 0)
                {
                    var fileId = photoArray[photoArray.Count - 1].GetProperty("file_id").GetString();
                    photoUrl = await GetFileUrlAsync(fileId!);
                }
            }

            // Persist to DB
            try
            {
                using var conn = _dbFactory.CreateConnection();
                await conn.ExecuteAsync(
                                        @"INSERT INTO channel_messages (message_id, message_text, photo_url, timestamp_utc)
                                            VALUES (@MessageId, @Text, @PhotoUrl, @Timestamp)
                                            ON DUPLICATE KEY UPDATE
                                                message_text = VALUES(message_text),
                                                photo_url = VALUES(photo_url),
                                                timestamp_utc = VALUES(timestamp_utc)",
                    new { MessageId = messageId, Text = text.Length > 1024 ? text[..1024] : text, PhotoUrl = photoUrl, Timestamp = timestamp });

                // Keep only latest 20 messages — delete older ones
                await conn.ExecuteAsync(
                    @"DELETE FROM channel_messages 
                      WHERE id NOT IN (SELECT id FROM (SELECT id FROM channel_messages ORDER BY message_id DESC LIMIT 20) AS keep)");

                _logger.LogInformation("Upserted channel message ID {Id} (photo: {HasPhoto}, edited: {IsEdited})", messageId, !string.IsNullOrEmpty(photoUrl), hasEditedChannelPost && !hasChannelPost);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist message {Id}", messageId);
            }
        }
    }

    private async Task ReconcileWithTelegramPublicFeedAsync()
    {
        if (string.IsNullOrWhiteSpace(_channelUsername))
            return;

        try
        {
            var channelUrl = $"https://t.me/s/{_channelUsername}";
            var html = await _httpClient.GetStringAsync(channelUrl);

            var matches = Regex.Matches(html, "data-post=\"[^\"/]+/(\\d+)\"");
            var visibleIds = matches
                .Select(m => int.TryParse(m.Groups[1].Value, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (visibleIds.Count == 0)
                return;

            var minVisibleId = visibleIds.First();
            var maxVisibleId = visibleIds.Last();

            using var conn = _dbFactory.CreateConnection();
            var removed = await conn.ExecuteAsync(
                @"DELETE FROM channel_messages
                  WHERE message_id BETWEEN @MinVisibleId AND @MaxVisibleId
                    AND message_id NOT IN @VisibleIds",
                new { MinVisibleId = minVisibleId, MaxVisibleId = maxVisibleId, VisibleIds = visibleIds });

            if (removed > 0)
            {
                _logger.LogInformation("Reconciled channel messages with Telegram feed. Removed {RemovedCount} stale local rows.", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram public-feed reconciliation skipped/failed.");
        }
    }

    private static string ResolveChannelUsername(IConfiguration config)
    {
        var fromConfig = (config["Telegram:ChannelUsername"] ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(fromConfig) && !fromConfig.Equals("YOUR_CHANNEL_USERNAME", StringComparison.OrdinalIgnoreCase))
            return fromConfig.TrimStart('@');

        var fromUrl = (config["TelegramChannelUrl"] ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(fromUrl) && Uri.TryCreate(fromUrl, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(path) && !path.StartsWith('+'))
            {
                return path.Split('/')[0].TrimStart('@');
            }
        }

        return string.Empty;
    }

    private async Task<string> GetFileUrlAsync(string fileId)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/getFile?file_id={fileId}";
            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("ok").GetBoolean())
            {
                var filePath = root.GetProperty("result").GetProperty("file_path").GetString();
                return $"https://api.telegram.org/file/bot{_botToken}/{filePath}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get file URL for {FileId}", fileId);
        }
        return "";
    }
}
