using System.Net.Http.Json;

static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(this HttpClient client, string requestUri, T value)
        => client.PatchAsync(requestUri, JsonContent.Create(value));

    public static async Task<string?> DownloadAsync(this HttpClient client, string requestUri)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            using var stram = await client.GetStreamAsync(requestUri);
            using var fileStream = File.Open(path, FileMode.Create);
            await stram.CopyToAsync(fileStream);
        }
        catch (Exception)
        {
            File.Delete(path);
            path = null;
        }
        return path;
    }
}
