using Checkpoint.Api;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Checkpoint.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string database = "checkpoint_test_" + Guid.NewGuid().ToString("N");
    private string adminConnection = "";
    public string ConnectionString { get; private set; } = "";
    public HttpClient Client { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Checkpoint"] = ConnectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Database:SeedDemo"] = "true",
            ["Images:Concurrency"] = "2"
        }));
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        adminConnection = Environment.GetEnvironmentVariable("CHECKPOINT_TEST_ADMIN")
            ?? throw new InvalidOperationException("Integration tests need CHECKPOINT_TEST_ADMIN; they create/drop only a new checkpoint_test_<random> database.");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database, IncludeErrorDetail = false }.ConnectionString;
        try { Client = CreateClient(); await Client.GetAsync("/health/live"); await Execute("UPDATE checkpoint.site_status SET status='live' WHERE id=1"); }
        catch { await Cleanup(); throw; }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        Client?.Dispose();
        await base.DisposeAsync();
        await Cleanup();
    }

    private async Task Cleanup()
    {
        if (!database.StartsWith("checkpoint_test_", StringComparison.Ordinal)) throw new InvalidOperationException();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
    }

    public async Task<T> Query<T>(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<T>(sql, args);
    }

    public async Task Execute(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(sql, args);
    }
}
