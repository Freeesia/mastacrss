using Microsoft.EntityFrameworkCore;

static class EFCoreExtensions
{
    public static async Task<T> UpdateAsNoTracking<T>(this DbContext context, T entity, CancellationToken cancellationToken = default)
        where T : class
    {
        var entry = context.Update(entity);
        entry.State = EntityState.Modified;
        await context.SaveChangesAsync(cancellationToken);
        entry.State = EntityState.Detached;
        return entity;
    }

    public static async Task AddAsNoTracking<T>(this DbContext context, T entity, CancellationToken cancellationToken = default)
        where T : class
    {
        var entry = await context.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        entry.State = EntityState.Detached;
    }
}