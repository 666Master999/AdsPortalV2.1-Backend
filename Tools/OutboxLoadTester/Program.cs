using System.CommandLine;
using System.CommandLine.Invocation;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Text.Json;

var root = new RootCommand("Outbox load tester")
{
    new Option<int>("--count", () => 10000, "Number of messages to insert and process"),
    new Option<int>("--batch", () => 1000, "Insert batch size"),
    new Option<string?>("--connection", "Database connection string (overrides appsettings)")
};

root.SetHandler<int,int,string?>(async (count, batch, connection) =>
{
    var connStr = connection ?? Environment.GetEnvironmentVariable("OUTBOX_TEST_CONN") ?? throw new InvalidOperationException("Provide connection string via --connection or OUTBOX_TEST_CONN env var");

    Console.WriteLine($"Starting Outbox load test: count={count}, batch={batch}");

    using var conn = new SqlConnection(connStr);
    await conn.OpenAsync();

    var totalInserted = 0;
    var sw = Stopwatch.StartNew();

    // prepare insert command
    while (totalInserted < count)
    {
        var toInsert = Math.Min(batch, count - totalInserted);

        using var tran = conn.BeginTransaction();
        for (int i = 0; i < toInsert; i++)
        {
            var eventId = Guid.NewGuid();
            var eventType = "AdsPortalV2.Services.AdImageDeletedDomainEvent, AdsPortalV2";
            var payload = new { EventId = eventId, AdId = 123, ImageId = i, OccurredAt = DateTime.UtcNow };
            var payloadJson = JsonSerializer.Serialize(payload);

            var cmd = new SqlCommand(@"INSERT INTO OutboxMessages (EventId, EventType, PayloadJson, CreatedAt, Status, AttemptCount, AvailableAt) VALUES (@EventId, @EventType, @PayloadJson, SYSUTCDATETIME(), @Status, 0, SYSUTCDATETIME())", conn, tran);
            cmd.Parameters.Add(new SqlParameter("@EventId", SqlDbType.UniqueIdentifier) { Value = eventId });
            cmd.Parameters.Add(new SqlParameter("@EventType", SqlDbType.NVarChar) { Value = eventType });
            cmd.Parameters.Add(new SqlParameter("@PayloadJson", SqlDbType.NVarChar) { Value = payloadJson });
            cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.Int) { Value = 0 });
            await cmd.ExecuteNonQueryAsync();
        }
        await tran.CommitAsync();
        totalInserted += toInsert;
        Console.WriteLine($"Inserted {totalInserted}/{count}");
    }

    sw.Stop();
    Console.WriteLine($"Insertion finished in {sw.Elapsed}");

    Console.WriteLine("Waiting for processing... polling every 5 seconds");

    var processed = 0;
    var failed = 0;
    var start = DateTime.UtcNow;
    while (processed + failed < count)
    {
        await Task.Delay(5000);
        using var checkCmd = new SqlCommand(@"SELECT SUM(CASE WHEN Status = 2 THEN 1 ELSE 0 END) as DoneCount, SUM(CASE WHEN Status = 4 THEN 1 ELSE 0 END) as DLQCount FROM OutboxMessages", conn);
        using var reader = await checkCmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            processed = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            failed = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }

        var now = DateTime.UtcNow;
        Console.WriteLine($"Processed={processed}, DLQ={failed}, elapsed={now - start}");
    }

    var end = DateTime.UtcNow;

    // compute average latency (ProcessedAt - CreatedAt)
    using var latencyCmd = new SqlCommand(@"SELECT AVG(DATEDIFF(ms, CreatedAt, ProcessedAt)) FROM OutboxMessages WHERE Status = 2", conn);
    var avgLatencyObj = await latencyCmd.ExecuteScalarAsync();
    var avgLatency = avgLatencyObj == DBNull.Value ? 0 : Convert.ToDouble(avgLatencyObj);

    Console.WriteLine($"Total processed: {processed}, DLQ: {failed}");
    Console.WriteLine($"Total time: {end - start}");
    Console.WriteLine($"Avg latency (ms): {avgLatency}");

}, root.Options.ToArray());

return await root.InvokeAsync(args);
