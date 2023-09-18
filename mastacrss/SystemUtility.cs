static class SystemUtility
{
    public const string Mastodon = nameof(Mastodon);
    public static async Task<T?> FallbackIfException<T>(Func<Task<T>> func, Func<Exception, Task> fallback)
        where T : notnull
    {
        try
        {
            return await func().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await fallback(ex).ConfigureAwait(false);
            return default;
        }
    }
}