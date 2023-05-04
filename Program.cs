using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CodeHollow.FeedReader;
using HtmlAgilityPack;
using mastacrss;
using Mastonet;
using Mastonet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PasswordGenerator;

const string DescSuffix = """


このアカウントはRSSフィードの内容を投稿するbotアカウントです。
このアカウントの投稿に関するお問い合わせは @owner までお願いします。
""";

var app = ConsoleApp.CreateBuilder(args)
    .ConfigureServices((ctx, services) => services.Configure<ConsoleOptions>(ctx.Configuration))
    .Build();
app.AddRootCommand(Run);
await app.RunAsync();

static async Task Run(ILogger<Program> logger, IOptions<ConsoleOptions> options)
{
    var (mastodonUrl, tootAppToken, monitoringToken, configPath) = options.Value;
    var client = new MastodonClient(mastodonUrl.DnsSafeHost, monitoringToken);
    async void CheckRssUrl(Status? status)
    {
        if (status is null) return;
        var url = GetUrl(status.Content);
        if (url is null) return;
        var profileInfo = await FallbackIfException(
            () => FetchProfileInfoFromWebsite(url),
            async ex => logger.LogError(ex, $"Failed to fetch profile info from {url}"));
        if (profileInfo is null) return;
        var accounts = await client.SearchAccounts(profileInfo.Name);
        if (accounts.Any(a => a.AccountName == profileInfo.Name)) return;
        var bot = await CreateBot(mastodonUrl, tootAppToken, profileInfo, logger);
        await client.Follow(bot.Id, true);
        await client.Favourite(status.Id);
        await client.PublishStatus($"""
            @{status.Account.AccountName}
            @{profileInfo.Name} を作成しました。
            """, status.Visibility, status.Id);
        logger.LogInformation($"Created bot account @{profileInfo.Name}");
        var config = await TomatoShriekerConfig.Load(configPath);
        config.Sources.Add(new()
        {
            Id = profileInfo.Name,
            Source = new()
            {
                Feed = profileInfo.Rss,
                RemoteKeyword = new()
                {
                    Enable = true,
                    Ignore = null,
                    ReplaceRules = null,
                },
            },
            Dest = new()
            {
                Account = new() { Bot = true },
                Mastodon = new() { Url = mastodonUrl.AbsoluteUri, Token = bot.Token },
            }
        });
        await config.Save(configPath);
        logger.LogInformation($"Saved config to {configPath}");
    }
    var me = await client.GetCurrentUser();
    var convs = await client.GetConversations();
    var statuses = convs.Select(c => c.LastStatus)
        .OfType<Status>()
        .Where(s => s.Account.Id != me.Id && !(s.Favourited ?? false));
    foreach (var status in statuses)
    {
        CheckRssUrl(status);
    }

    var ust = client.GetUserStreaming();
    var dm = client.GetDirectMessagesStreaming();
    ust.OnConversation += (_, e) => CheckRssUrl(e.Conversation.LastStatus);
    dm.OnConversation += (_, e) => CheckRssUrl(e.Conversation.LastStatus);
    await Task.WhenAll(ust.Start(), dm.Start());
}

static Uri? GetUrl(string? content)
{
    if (content is null) return null;
    var document = new HtmlDocument();
    document.LoadHtml(content);
    var link = document.DocumentNode.SelectSingleNode("/p/a");
    return link is null ? null : new(link.Attributes["href"].Value);
}

static async Task<BotInfo> CreateBot(Uri mastodonUrl, string appAccessToken, ProfileInfo profileInfo, ILogger logger)
{
    using var mstdnClient = new HttpClient()
    {
        BaseAddress = mastodonUrl,
        DefaultRequestHeaders = { Authorization = new("Bearer", appAccessToken) }
    };
    // 1. アカウントの作成
    var pwd = new Password(32);
    // name = new Password(6).IncludeLowercase().Next();
    var createAccountData = new
    {
        username = profileInfo.Name,
        email = $"mastodon+{profileInfo.Name}@studiofreesia.com",
        password = pwd.Next(),
        agreement = true,
        locale = "ja"
    };
    var response = await mstdnClient.PostAsJsonAsync("/api/v1/accounts", createAccountData);
    response.EnsureSuccessStatusCode();
    var cred = await response.Content.ReadFromJsonAsync<AccountCredentials>() ?? throw new Exception("Failed to create account");
    mstdnClient.DefaultRequestHeaders.Authorization = new("Bearer", cred.access_token);
    logger.LogInformation($"Created account @{createAccountData.username}");
    logger.LogInformation($"email: {createAccountData.email}");
    logger.LogInformation($"password: {createAccountData.password}");
    logger.LogInformation($"token: {cred.access_token}");

    do
    {
        logger.LogInformation("Waiting for account creation...");
        await Task.Delay(10000);
        response = await mstdnClient.GetAsync("/api/v1/accounts/verify_credentials");
        // 403が返ってきたら、まだアカウントが作成されていないので、10秒待って再度試行する
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            response.EnsureSuccessStatusCode();
        }
    } while (!response.IsSuccessStatusCode);
    var account = await response.Content.ReadFromJsonAsync<AccountInfo>() ?? throw new Exception("Failed to get account info");

    // 3. プロフィール画像の設定
    var updateCredentialsUrl = "/api/v1/accounts/update_credentials";

    // apple-touch-icon.pngを取得して設定する
    if (File.Exists(profileInfo.IconPath))
    {
        using var avatarFile = File.OpenRead(profileInfo.IconPath);
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(avatarFile), "avatar", "avatar.png");
        response = await mstdnClient.PatchAsync(updateCredentialsUrl, content);
        response.EnsureSuccessStatusCode();
    }
    // og:imageを取得して設定する
    if (File.Exists(profileInfo.ThumbnailPath))
    {
        using var headerFile = File.OpenRead(profileInfo.ThumbnailPath);
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(headerFile), "header", "header.png");
        response = await mstdnClient.PatchAsync(updateCredentialsUrl, content);
        response.EnsureSuccessStatusCode();
    }

    // 4. プロフィール文の設定
    var updateProfileData = new
    {
        display_name = profileInfo.Title,
        note = profileInfo.Description + DescSuffix,
        fields_attributes = new Dictionary<int, object>()
        {
            [0] = new { name = "Website", value = profileInfo.Link, },
            [1] = new { name = "RSS", value = profileInfo.Rss, },
        },
        bot = true,
        discoverable = true,
    };
    response = await mstdnClient.PatchAsJsonAsync(updateCredentialsUrl, updateProfileData);
    response.EnsureSuccessStatusCode();

    // 5. タグの設定
    var safe = new Regex(@"[\s\*_\-\[\]\(\)]");
    foreach (var keyword in profileInfo.Keywords.Take(10))
    {
        response = await mstdnClient.PostAsJsonAsync("/api/v1/featured_tags", new { name = safe.Replace(keyword, "_") });
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning($"Failed to add tag: {keyword}");
        }
    }
    return new(account.id, cred.access_token);
}

static async Task<ProfileInfo> FetchProfileInfoFromWebsite(Uri url)
{
    var name = url.Host.Split('.').OrderByDescending(x => x.Length)
        .Where(s => s is not "www")
        .First()
        .Replace('-', '_');
    using var httpClient = new HttpClient();
    // ルートHTMLを取得
    var response = await httpClient.GetAsync(url.GetLeftPart(UriPartial.Authority));
    var document = new HtmlDocument();
    document.Load(await response.Content.ReadAsStreamAsync());

    // apple-touch-iconの画像をダウンロードしてパスを取得する
    var iconPath = Path.GetTempFileName();
    var appleTouchIconLink = document.DocumentNode.SelectSingleNode("//link[@rel='apple-touch-icon']")?.GetAttributeValue("href", string.Empty);
    if (!string.IsNullOrEmpty(appleTouchIconLink))
    {
        if (!Uri.IsWellFormedUriString(appleTouchIconLink, UriKind.Absolute))
        {
            var builder = new UriBuilder(url) { Path = appleTouchIconLink };
            appleTouchIconLink = builder.Uri.AbsoluteUri;
        }
        using var stram = await httpClient.GetStreamAsync(appleTouchIconLink);
        using var fileStream = File.Open(iconPath, FileMode.Create);
        await stram.CopyToAsync(fileStream);
    }
    else
    {
        File.Delete(iconPath);
    }

    // keywordsを取得して、それをタグに設定する
    var keywords = document.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", string.Empty)
        .Split(',')
        .Select(x => x.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .ToArray() ?? Array.Empty<string>();

    // og:imageの画像をダウンロードしてパスを取得する
    var thumbnailPath = Path.GetTempFileName();
    var imageLink = document.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", string.Empty);
    imageLink ??= document.DocumentNode.SelectSingleNode("//meta[@name='twitter:image:src']")?.GetAttributeValue("content", string.Empty);
    if (!string.IsNullOrEmpty(imageLink))
    {
        if (!Uri.IsWellFormedUriString(imageLink, UriKind.Absolute))
        {
            var builder = new UriBuilder(url) { Path = imageLink };
            imageLink = builder.Uri.AbsoluteUri;
        }
        using var stram = await httpClient.GetStreamAsync(imageLink);
        using var fileStream = File.Open(thumbnailPath, FileMode.Create);
        await stram.CopyToAsync(fileStream);
    }
    else
    {
        File.Delete(thumbnailPath);
    }
    if (!File.Exists(iconPath) && File.Exists(thumbnailPath))
    {
        iconPath = thumbnailPath;
    }

    // urlがルートだったら、RSSフィードのURLを取得して、ちがったらそのまま使う
    var rssUrl = url.AbsolutePath == "/" ? document.DocumentNode.SelectSingleNode("//link[@type='application/rss+xml']")?.GetAttributeValue("href", string.Empty) : url.AbsoluteUri;
    rssUrl ??= url.AbsoluteUri;
    // rssからtitle, description,link,languageを取得して設定する
    var feed = await FeedReader.ReadAsync(rssUrl);
    return new ProfileInfo(name, iconPath, thumbnailPath, feed.Title, feed.Description, feed.Link, rssUrl, keywords);
}

static async Task<T?> FallbackIfException<T>(Func<Task<T>> func, Func<Exception, Task> fallback)
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
record AccountCredentials(string access_token, string token_type, string scope, long created_at);
record AccountInfo(string id);
record AppCredentials(string client_id, string client_secret, string vapid_key);
record ProfileInfo(string Name, string IconPath, string ThumbnailPath, string Title, string Description, string Link, string Rss, string[] Keywords);
record BotInfo(string Id, string Token);
record ConsoleOptions
{
    public required Uri MastodonUrl { get; init; }
    public required string TootAppToken { get; init; }
    public required string MonitoringToken { get; init; }
    public required string ConfigPath { get; init; }

    public void Deconstruct(out Uri mastodonUrl, out string tootAppToken, out string monitoringToken, out string configPath)
        => (mastodonUrl, tootAppToken, monitoringToken, configPath)
        = (this.MastodonUrl, this.TootAppToken, this.MonitoringToken, this.ConfigPath);
}

static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PatchAsync(this HttpClient client, string requestUri, HttpContent content)
    {
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), requestUri)
        {
            Content = content
        };

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(this HttpClient client, string requestUri, T value)
        => client.PatchAsync(requestUri, JsonContent.Create(value));
}
