using AdsTracking.Api.Data;
using AdsTracking.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace AdsTracking.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly TelegramBotService _telegramBot;

    public DashboardController(DbConnectionFactory dbFactory, TelegramBotService telegramBot)
    {
        _dbFactory = dbFactory;
        _telegramBot = telegramBot;
    }

    [HttpGet("insights")]
    public async Task<IActionResult> GetInsights()
    {
        using var connection = _dbFactory.CreateConnection();
        await connection.OpenAsync();

        // KPIs
        var totalVisitors = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM visitors");
        var totalPageViews = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM page_views");
        var totalDownloads = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM download_events");
        var avgTimeOnPage = await connection.ExecuteScalarAsync<int?>(
            "SELECT ROUND(AVG(time_on_page_seconds)) FROM page_views WHERE time_on_page_seconds > 0 AND time_on_page_seconds <= 1800") ?? 0;

        var ctaClicks = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM events WHERE event_type = 'cta_click_telegram_page'");
        var telegramClicks = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT visitor_id) FROM events WHERE event_type = 'telegram_channel_click'");

        // Get real-time Telegram channel member count (show total count directly)
        var telegramMembersRaw = await _telegramBot.GetChannelMemberCountAsync();
        int telegramMembers = telegramMembersRaw ?? 0;

        var clickToJoinRate = (telegramClicks > 0 && telegramMembers > 0)
            ? Math.Round((double)telegramMembers / telegramClicks * 100, 1)
            : (double?)null;

        // Campaign performance
        var campaigns = await connection.QueryAsync<dynamic>(@"
            SELECT 
                v.utm_source AS utmSource,
                v.utm_medium AS utmMedium,
                v.utm_campaign AS utmCampaign,
                COUNT(DISTINCT v.visitor_id) AS visitors,
                COUNT(DISTINCT e.visitor_id) AS telegramClicks
            FROM visitors v
            LEFT JOIN events e ON v.visitor_id = e.visitor_id AND e.event_type = 'telegram_channel_click'
            GROUP BY v.utm_source, v.utm_medium, v.utm_campaign
            ORDER BY visitors DESC
            LIMIT 20");

        // Recent visitors with their journey data
        var recentVisitors = await connection.QueryAsync<dynamic>(@"
            SELECT 
                v.visitor_id AS visitorId,
                v.utm_source AS utmSource,
                v.utm_campaign AS utmCampaign,
                DATE_FORMAT(v.first_seen_utc, '%Y-%m-%dT%H:%i:%s.%fZ') AS firstSeen,
                v.ip_address AS ipAddress,
                COALESCE(pv.page_count, 0) AS pageViews,
                COALESCE(pv.total_time, 0) AS totalTime,
                CASE WHEN e.visitor_id IS NOT NULL THEN 1 ELSE 0 END AS clickedTelegram
            FROM visitors v
            LEFT JOIN (
                SELECT visitor_id, COUNT(*) AS page_count, SUM(time_on_page_seconds) AS total_time
                FROM page_views
                GROUP BY visitor_id
            ) pv ON v.visitor_id = pv.visitor_id
            LEFT JOIN (
                SELECT DISTINCT visitor_id FROM events WHERE event_type = 'telegram_channel_click'
            ) e ON v.visitor_id = e.visitor_id
            ORDER BY v.first_seen_utc DESC
            LIMIT 30");


        return Ok(new
        {
            totalVisitors,
            totalPageViews,
            totalDownloads,
            avgTimeOnPage,
            ctaClicks,
            telegramClicks,
            telegramMembers = telegramMembers,
            clickToJoinRate,
            campaigns,
            recentVisitors
        });
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        using var connection = _dbFactory.CreateConnection();
        await connection.OpenAsync();

        var visitors = await connection.QueryAsync(
            "SELECT visitor_id, utm_source, utm_medium, utm_campaign, fbclid, first_seen_utc FROM visitors ORDER BY first_seen_utc DESC LIMIT 50");
        var pageViews = await connection.QueryAsync(
            "SELECT visitor_id, page_url, page_title, entered_at_utc, time_on_page_seconds, ip_address FROM page_views ORDER BY entered_at_utc DESC LIMIT 50");
        var events = await connection.QueryAsync(
            "SELECT visitor_id, event_type, event_data, page_url, timestamp_utc FROM events ORDER BY timestamp_utc DESC LIMIT 50");
        var downloads = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM download_events");

        return Ok(new
        {
            totalVisitors = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM visitors"),
            totalPageViews = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM page_views"),
            totalEvents = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM events"),
            totalDownloads = downloads,
            visitors,
            pageViews,
            events
        });
    }
}
