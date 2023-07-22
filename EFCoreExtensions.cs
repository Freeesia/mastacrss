using Microsoft.EntityFrameworkCore;

static class EFCoreExtensions
{
    public static void UpdateAsNoTracking<T>(this DbContext context, T entity)
        where T : class
    {
        var entry = context.Update(entity);
        entry.State = EntityState.Detached;
    }
}