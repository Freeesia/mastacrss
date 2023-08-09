using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

class AccountContext : DbContext
{
    public DbSet<AccountInfo> AccountInfos { get; init; } = null!;

    public AccountContext(DbContextOptions options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);
}

record AccountInfo(
    [property: Key] string Name,
    string AccessToken,
    string RequestId,
    string? Id = null,
    bool Setuped = false,
    bool Notified = false,
    bool Replied = false);