using System.Text.Json;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
// Polly is optional; use built-in simple retry if Polly not available.

namespace AdsPortalV2.Services.Outbox;

public class OutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 10;

    public OutboxProcessor(IServiceProvider provider, ILogger<OutboxProcessor> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatch(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxProcessor failure");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task ProcessBatch(CancellationToken ct)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Claim a batch of pending messages using SQL Server row locking hints (ROWLOCK, UPDLOCK, READPAST)
        // This prevents multiple workers from processing the same rows concurrently.
        var batchSize = 50;
        List<OutboxMessage> msgs;
        // Use explicit transaction to hold UPDLOCK until we update the rows
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct))
        {
            // Select with locking hints
            var sql = $@"SELECT TOP ({batchSize}) * FROM OutboxMessages WITH (ROWLOCK, UPDLOCK, READPAST)
WHERE Status = {(int)OutboxStatus.Pending} AND (AvailableAt IS NULL OR AvailableAt <= SYSUTCDATETIME())
ORDER BY CreatedAt";

            msgs = await db.OutboxMessages.FromSqlRaw(sql).ToListAsync(ct);

            _logger.LogDebug("OutboxProcessor claimed {Count} messages", msgs.Count);

            if (msgs.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            // Mark claimed messages as Processing and increment AttemptCount
            foreach (var msg in msgs.Where(msg => msg.Status == OutboxStatus.Pending && msg.ProcessedAt == null))
            {
                msg.Status = OutboxStatus.Processing;
                msg.AttemptCount += 1;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        foreach (var msg in msgs)
        {
            if (ct.IsCancellationRequested) break;

            if (msg.Status != OutboxStatus.Pending && msg.Status != OutboxStatus.Processing)
                continue;

            if (msg.ProcessedAt != null)
                continue;

            try
            {
                _logger.LogInformation("Processing outbox message {Id} Event={EventType} Attempt={Attempt}", msg.Id, msg.EventType, msg.AttemptCount);
                var success = await DispatchMessage(msg, ct);
                if (success)
                {
                    msg.Status = OutboxStatus.Done;
                    msg.ProcessedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Outbox message {Id} processed successfully", msg.Id);
                }
                else
                {
                    // schedule retry
                    msg.LastError = "Dispatch failed";
                    if (msg.AttemptCount > MaxAttempts)
                    {
                        msg.Status = OutboxStatus.DeadLetter;
                        msg.AvailableAt = null;
                    }
                    else
                    {
                        msg.AvailableAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, msg.AttemptCount));
                        msg.Status = OutboxStatus.Pending;
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing outbox message {Id}", msg.Id);
                msg.LastError = ex.Message;
                msg.AttemptCount++;
                if (msg.AttemptCount > MaxAttempts)
                {
                    msg.Status = OutboxStatus.DeadLetter;
                    msg.AvailableAt = null;
                }
                else
                {
                    msg.AvailableAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, msg.AttemptCount));
                    msg.Status = OutboxStatus.Pending;
                }
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task<bool> DispatchMessage(OutboxMessage msg, CancellationToken ct)
    {
        try
        {
            // resolve handlers
            var eventTypeName = msg.EventType;
            var type = Type.GetType(eventTypeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(eventTypeName))
                .FirstOrDefault(t => t != null);
            if (type == null)
            {
                _logger.LogWarning("Unknown event type {Type} for Outbox.Id={Id}", eventTypeName, msg.Id);
                return true; // discard
            }

            var payload = JsonSerializer.Deserialize(msg.PayloadJson, type, DialogHelpers.s_jsonl);
            if (payload == null)
            {
                _logger.LogWarning("Empty payload for outbox {Id}", msg.Id);
                return true;
            }

            var handlerInterface = typeof(IDomainEventHandler<>).MakeGenericType(type);
            using var scope = _provider.CreateScope();
            var handlers = (IEnumerable<object>?)scope.ServiceProvider.GetService(typeof(IEnumerable<>).MakeGenericType(handlerInterface)) ?? Enumerable.Empty<object>();

            if (!handlers.Any())
            {
                _logger.LogWarning("No handlers for event {Type} Outbox.Id={Id}", eventTypeName, msg.Id);
                return true; // nothing to do
            }

            // run handlers with simple retry loop
            var maxAttempts = 3;
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    foreach (var handler in handlers)
                    {
                        var method = handlerInterface.GetMethod("Handle");
                        if (method == null) continue;
                        _logger.LogDebug("Invoking handler {Handler} for Outbox.Id={Id}", handler.GetType().FullName, msg.Id);

                        // Build arguments dynamically to remain backward-compatible with handlers
                        // that expect either (TEvent, long outboxMessageId) or (TEvent, Guid eventId)
                        var parameters = method.GetParameters();
                        object[] args;
                        if (parameters.Length == 2)
                        {
                            var secondType = parameters[1].ParameterType;
                            if (secondType == typeof(Guid))
                            {
                                args = new object[] { payload, msg.EventId };
                            }
                            else if (secondType == typeof(long) || secondType == typeof(Int64))
                            {
                                args = new object[] { payload, msg.Id };
                            }
                            else
                            {
                                // unknown second parameter type - try to pass outbox id as fallback
                                args = new object[] { payload, msg.Id };
                            }
                        }
                        else
                        {
                            // single-argument handler
                            args = new object[] { payload };
                        }

                        var task = (Task)method.Invoke(handler, args)!;
                        await task;
                    }
                    break; // success
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Handler attempt {Attempt} failed for outbox {Id}", attempt, msg.Id);
                    if (attempt >= maxAttempts) throw;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch error for outbox {Id}", msg.Id);
            return false;
        }
    }
}
