using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AdsPortalV2.Data;

namespace AdsPortalV2.IntegrationTests;

public static class TestHelpers
{
    public static (SqliteConnection, DbContextOptions<AppDbContext>) CreateInMemorySqliteContextOptions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite(connection);

        return (connection, builder.Options);
    }
}
