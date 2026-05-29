using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Tests;

public sealed class SqliteUserAccessTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"telegrambot-access-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Access_request_creates_pending_user_and_approval_grants_access()
    {
        var service = CreateService();
        await service.InitializeDatabaseAsync();

        await service.CreateAccessRequestAsync(
            userId: 123,
            username: "alice",
            firstName: "Alice",
            lastName: "User");

        var pending = await service.GetBotUserAsync(123);
        Assert.NotNull(pending);
        Assert.Equal(UserAccessStatus.Pending, pending.Status);
        Assert.False(await service.IsUserApprovedAsync(123));

        Assert.True(await service.ApproveUserAsync(123, approvedBy: 777));

        var approved = await service.GetBotUserAsync(123);
        Assert.NotNull(approved);
        Assert.Equal(UserAccessStatus.Approved, approved.Status);
        Assert.Equal(UserRole.User, approved.Role);
        Assert.True(await service.IsUserApprovedAsync(123));
    }

    [Fact]
    public async Task Admin_configured_in_database_has_admin_role_and_access()
    {
        var service = CreateService();
        await service.InitializeDatabaseAsync();

        await service.EnsureAdminUserAsync(777, username: "admin");

        var admin = await service.GetBotUserAsync(777);
        Assert.NotNull(admin);
        Assert.Equal(UserAccessStatus.Approved, admin.Status);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.True(await service.IsUserApprovedAsync(777));
    }

    private SqliteDataService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = _dbPath
            })
            .Build();

        return new SqliteDataService(config, NullLogger<SqliteDataService>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
