using System.Data;
using Microsoft.EntityFrameworkCore;

class AccountContext : DbContext
{
    public DbSet<AccountInfo> AccountInfos { get; init; } = null!;

    public AccountContext(DbContextOptions options)
        : base(options)
    {
        this.Database.GetDbConnection().StateChange += (_, args) =>
        {
            if (args.CurrentState != ConnectionState.Open)
            {
                return;
            }

            this.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
        };
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);
}

[PrimaryKey(nameof(Url), nameof(RequestId))]
record AccountInfo(
    Uri Url,
    string RequestId,
    string? Name = null,
    string? BotId = null,
    string? AccessToken = null,
    bool Setuped = false,
    bool Notified = false,
    bool Replied = false,
    bool Finished = false);