using Dapper;

namespace AdsTracking.Api.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(DbConnectionFactory dbFactory)
    {
        using var connection = dbFactory.CreateConnection();
        await connection.OpenAsync();

        // Drop old unused tables if they exist
        await connection.ExecuteAsync("DROP TABLE IF EXISTS telegram_click_events");
        await connection.ExecuteAsync("DROP TABLE IF EXISTS visit_records");

        // Visitors table — one row per unique visitor
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS visitors (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                visitor_id CHAR(36) NOT NULL UNIQUE,
                first_seen_utc DATETIME(3) NOT NULL,
                utm_source VARCHAR(256) NOT NULL DEFAULT '',
                utm_medium VARCHAR(256) NOT NULL DEFAULT '',
                utm_campaign VARCHAR(256) NOT NULL DEFAULT '',
                utm_term VARCHAR(256) NOT NULL DEFAULT '',
                utm_content VARCHAR(256) NOT NULL DEFAULT '',
                fbclid VARCHAR(512) NOT NULL DEFAULT '',
                ip_address VARCHAR(45) NOT NULL DEFAULT '',
                created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                INDEX idx_utm (utm_source, utm_medium, utm_campaign),
                INDEX idx_first_seen (first_seen_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Add ip_address column if it doesn't exist (for existing installs)
        try {
            await connection.ExecuteAsync("ALTER TABLE visitors ADD COLUMN ip_address VARCHAR(45) NOT NULL DEFAULT '' AFTER fbclid");
        } catch { /* column already exists */ }

        // Page views — every page a visitor sees
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS page_views (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                visitor_id CHAR(36) NOT NULL,
                page_url VARCHAR(2048) NOT NULL,
                page_title VARCHAR(512) NOT NULL DEFAULT '',
                entered_at_utc DATETIME(3) NOT NULL,
                time_on_page_seconds INT NOT NULL DEFAULT 0,
                ip_address VARCHAR(45) NOT NULL DEFAULT '',
                user_agent VARCHAR(1024) NOT NULL DEFAULT '',
                created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                INDEX idx_pv_visitor (visitor_id),
                INDEX idx_pv_entered (entered_at_utc),
                INDEX idx_pv_page (page_url(255))
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Events — clicks, navigations, telegram join, etc.
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS events (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                visitor_id CHAR(36) NOT NULL,
                event_type VARCHAR(50) NOT NULL,
                event_data TEXT,
                page_url VARCHAR(2048) NOT NULL DEFAULT '',
                timestamp_utc DATETIME(3) NOT NULL,
                created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                INDEX idx_ev_visitor (visitor_id),
                INDEX idx_ev_type (event_type),
                INDEX idx_ev_timestamp (timestamp_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Download events (aggregate — no visitor attribution)
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS download_events (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                timestamp_utc DATETIME(3) NOT NULL,
                ip_address VARCHAR(45) NOT NULL,
                user_agent VARCHAR(1024) NOT NULL,
                created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                INDEX idx_dl_timestamp_utc (timestamp_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Telegram channel messages — stores message IDs for preview
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS channel_messages (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                message_id INT NOT NULL UNIQUE,
                message_text VARCHAR(1024) NOT NULL DEFAULT '',
                photo_url VARCHAR(512) NOT NULL DEFAULT '',
                timestamp_utc DATETIME(3) NOT NULL,
                created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                INDEX idx_cm_timestamp (timestamp_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Add photo_url column if missing (for existing installs)
        try {
            await connection.ExecuteAsync("ALTER TABLE channel_messages ADD COLUMN photo_url VARCHAR(512) NOT NULL DEFAULT '' AFTER message_text");
        } catch { /* column already exists */ }

        // Dashboard login users
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS dashboard_users (
                id INT AUTO_INCREMENT PRIMARY KEY,
                username VARCHAR(100) NOT NULL UNIQUE,
                password_hash VARCHAR(255) NOT NULL,
                created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Seed default admin user if table is empty
        var userCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dashboard_users");
        if (userCount == 0)
        {
            var defaultHash = BCrypt.Net.BCrypt.HashPassword("*9AJcdasq+LC(!kT4ziX+");
            await connection.ExecuteAsync(
                "INSERT INTO dashboard_users (username, password_hash) VALUES (@Username, @Hash)",
                new { Username = "JpTradeBazaar", Hash = defaultHash });
        }
    }
}
