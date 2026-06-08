using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>
/// Background service that runs once every 24 hours to purge old diagnostic records.
/// Prevents InvoiceScanLogs, ChatbotLogs, and ChatMessages from growing unboundedly,
/// which was causing the application to slow down and eventually crash.
/// Retention: 90 days for all log tables.
/// </summary>
public class DbCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    public DbCleanupService(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app to finish starting before first run
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCleanupAsync();
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCleanupAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TripExDbContext>();

            var cutoffDto = DateTimeOffset.UtcNow.AddDays(-90);
            var cutoffDt  = DateTime.UtcNow.AddDays(-90);

            // Delete old scan logs (nvarchar(max) RawAiResponse — most space-heavy)
            var deletedScanLogs = await db.InvoiceScanLogs
                .Where(l => l.CreatedAt < cutoffDto)
                .ExecuteDeleteAsync();

            // Delete old chatbot logs
            var deletedChatLogs = await db.ChatbotLogs
                .Where(l => l.CreatedAt < cutoffDt)
                .ExecuteDeleteAsync();

            // Delete old chat messages first (FK dependency)
            var deletedMessages = await db.ChatMessages
                .Where(m => m.CreatedAt < cutoffDt)
                .ExecuteDeleteAsync();

            // Delete sessions that have no remaining messages and are old
            var sessionsWithMessages = db.ChatMessages.Select(m => m.SessionId).Distinct();
            var deletedSessions = await db.ChatSessions
                .Where(s => s.CreatedAt < cutoffDt && !sessionsWithMessages.Contains(s.Id))
                .ExecuteDeleteAsync();

            var total = deletedScanLogs + deletedChatLogs + deletedMessages + deletedSessions;
            if (total > 0)
                Console.WriteLine(
                    $"[DB-CLEANUP] Purged {deletedScanLogs} scan logs, " +
                    $"{deletedChatLogs} chatbot logs, " +
                    $"{deletedMessages} chat messages, " +
                    $"{deletedSessions} chat sessions (>90 days old)");
            else
                Console.WriteLine("[DB-CLEANUP] No old records to purge.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DB-CLEANUP] Error during cleanup: {ex.Message}");
        }
    }
}
