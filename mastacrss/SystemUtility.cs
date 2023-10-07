static class SystemUtility
{
    public const string Mastodon = nameof(Mastodon);
    public static async Task<T?> FallbackIfException<T>(Func<Task<T>> func, Func<Exception, Task>? fallback = null)
        where T : notnull
    {
        try
        {
            return await func().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (fallback is not null)
            {
                await fallback(ex).ConfigureAwait(false);
            }
            return default;
        }
    }
}