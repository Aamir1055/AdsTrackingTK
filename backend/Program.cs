using AdsTracking.Api.Data;
using AdsTracking.Api.Infrastructure;
using AdsTracking.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// MySQL connection factory (Dapper + MySqlConnector)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
builder.Services.AddSingleton(new DbConnectionFactory(connectionString));

// Register services
builder.Services.AddScoped<DownloadService>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddSingleton<TelegramMessagesService>();
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<NewsService>();

// Retry queue (singleton)
builder.Services.AddSingleton<RetryQueue>();
builder.Services.AddHostedService<RetryQueueService>();

// Controllers
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

// CORS — allow the frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

var frontendPath = ResolveFrontendPath(app.Environment.ContentRootPath);

// Create tables if they don't exist
var dbFactory = app.Services.GetRequiredService<DbConnectionFactory>();
await DatabaseInitializer.InitializeAsync(dbFactory);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// Serve static files from the frontend folder
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        frontendPath),
    RequestPath = ""
});

app.MapControllers();

// Fallback to index.html for the landing page
app.MapFallback(async context =>
{
    var filePath = Path.Combine(frontendPath, "index.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(filePath);
});

app.Run();

static string ResolveFrontendPath(string contentRootPath)
{
    var candidates = new[]
    {
        Path.Combine(contentRootPath, "frontend"),
        Path.Combine(contentRootPath, "..", "frontend")
    };

    foreach (var candidate in candidates)
    {
        var fullPath = Path.GetFullPath(candidate);
        if (Directory.Exists(fullPath))
            return fullPath;
    }

    throw new DirectoryNotFoundException(
        $"Unable to locate frontend directory. Checked: {string.Join(", ", candidates.Select(Path.GetFullPath))}");
}
