OutboxLoadTester

Usage:

- dotnet run --project Tools/OutboxLoadTester -- --count 10000 --batch 1000 --connection "<your-connection-string>"

The tool inserts `count` synthetic OutboxMessages in batches and polls the OutboxMessages table every 5 seconds to report processed and DLQ counts. It prints average latency (ms) after processing completes.

Environment variable alternative:
- Set OUTBOX_TEST_CONN environment variable instead of passing --connection.

Notes:
- Designed to be minimal and non-intrusive.
- Uses SYSUTCDATETIME() for CreatedAt and AvailableAt to match SQL Server server time.
- Ensure you run this against a non-production or staging database.
