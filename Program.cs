using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Engine;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "RamStockAlerts API", Version = "v1" });
});

// Add SQLite database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Data Source=ramstockalerts.db"));

// Caching for universe and circuit breaker
builder.Services.AddMemoryCache();

// Add HttpClient for Discord and Polygon
builder.Services.AddHttpClient<DiscordNotificationService>();
builder.Services.AddHttpClient("Polygon"); // Named client for Polygon API
builder.Services.AddHttpClient();

// Register services
builder.Services.AddScoped<SignalService>();
builder.Services.AddScoped<AlertThrottlingService>();
builder.Services.AddScoped<DiscordNotificationService>();
builder.Services.AddSingleton<SignalValidator>();
builder.Services.AddSingleton<TradeBlueprint>();
builder.Services.AddSingleton<CircuitBreakerService>();
builder.Services.AddSingleton<UniverseBuilder>();
builder.Services.AddScoped<PerformanceTracker>();
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();

// Register Polygon background service (fallback for daily aggregates)
builder.Services.AddHostedService<PolygonRestClient>();

// Register Alpaca real-time streaming service
builder.Services.AddSingleton<AlpacaStreamClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlpacaStreamClient>());

// Health checks
builder.Services.AddHealthChecks();

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => 
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "RamStockAlerts API v1");
        c.RoutePrefix = string.Empty; // Swagger at root URL
    });
    app.UseCors("DevCors");
}

// Only use HTTPS redirection in production or when HTTPS is available
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
