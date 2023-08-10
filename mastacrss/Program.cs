using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using mastacrss;
using Mastonet;
using Mastonet.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PasswordGenerator;
using static SystemUtility;

const string Mastodon = nameof(Mastodon);

var app = ConsoleApp.CreateBuilder(args)
    .ConfigureServices(
        (c, s) => s.Configure<ConsoleOptions>(c.Configuration)
            .AddDbContext<AccountContext>(op => op.UseSqlite(c.Configuration.GetConnectionString("DefaultConnection")))
            .AddHttpClient(Mastodon, (s, c) =>
            {
                var op = s.GetRequiredService<IOptions<ConsoleOptions>>();
                c.BaseAddress = op.Value.MastodonUrl;
            }))
    .ConfigureLogging((c, l) => l.AddConfiguration(c.Configuration).AddSentry())
    .Build();
app.AddRootCommand(Run);
app.AddCommand("test", Test);
app.AddCommand("setup", Setup);
app.AddCommand("setup-all", SetupAll);
await app.RunAsync();

static async Task Run(ILogger<Program> logger, IOptions<ConsoleOptions> options, AccountContext accountContext, IHttpClientFactory factory)
{
    var (mastodonUrl, tootAppToken, monitoringToken, configPath, reactiveTag, dispNamePrefix) = options.Value;
    var client = new MastodonClient(mastodonUrl.DnsSafeHost, monitoringToken, factory.CreateClient());
    await accountContext.Database.EnsureCreatedAsync();
    async void CheckRssUrl(Status? status, string myId)
    {
        // 投稿なければ無視
        if (status is null) return;
        // 自分の投稿は無視
        if (status.Account.Id == myId) return;
        // 反応するハッシュタグがなければ無視
        if (!status.Tags.OfType<Tag>().Select(t => t.Name).Contains(reactiveTag))
        {
            logger.LogInformation($"No reactive tag: {status.Id}");
            return;
        }
        foreach (var url in GetUrls(status.Content))
        {
            var profileInfo = await FallbackIfException(
                () => ProfileInfo.FetchFromWebsite(factory, url),
                async ex =>
                {
                    logger.LogError(ex, $"Failed to fetch profile info from {url}");
                    await client.PublishStatus($"""
                        @{status.Account.AccountName}
                        以下のURLのフィード情報の取得に失敗しました。別のURLをお試しください。
                        {url}
                        """, status.Visibility, status.Id);
                });
            if (profileInfo is null) continue;

            if (await accountContext.AccountInfos.FindAsync(profileInfo.Name) is not { } accountInfo)
            {
                // 作成中じゃないけどアカウントが存在する場合は作成済みなので抜ける
                var accounts = await client.SearchAccounts(profileInfo.Name);
                if (accounts.Any(a => a.AccountName == profileInfo.Name))
                {
                    logger.LogInformation($"Account @{profileInfo.Name} already exists. RSS: {profileInfo.Rss}");
                    await client.PublishStatus($"""
                        @{status.Account.AccountName}
                        依頼されたアカウントは @{profileInfo.Name} として作成済みです。
                        同一サイトで複数のRSSが存在する場合は個別に対応するので、しばらくお待ちください。
                        """, status.Visibility, status.Id);
                    continue;
                }

                var token = await CreateBot(factory, tootAppToken, profileInfo, logger);
                accountInfo = new(profileInfo.Name, token, status.Id);
                await accountContext.AddAsNoTracking(accountInfo);
                await accountContext.SaveChangesAsync();
            }
            var accessToken = accountInfo.AccessToken;
            if (accountInfo is not { Id: { } botId })
            {
                botId = await WaitVerifiy(factory, accessToken, logger);
                accountContext.UpdateAsNoTracking(accountInfo with { Id = botId });
                await accountContext.SaveChangesAsync();
            }
            if (!accountInfo.Setuped)
            {
                await SetupAccount(factory, accessToken, profileInfo, dispNamePrefix, logger);
                accountContext.UpdateAsNoTracking(accountInfo with { Setuped = true });
                await accountContext.SaveChangesAsync();
            }

            var config = await TomatoShriekerConfig.Load(configPath);
            if (!config.Sources.Any(s => s.Id == profileInfo.Name))
            {
                config.AddSource(profileInfo.Name, profileInfo.Rss, mastodonUrl.AbsoluteUri, accessToken);
                await config.Save(configPath);
                logger.LogInformation($"Saved config to {configPath}");
            }


            await client.Follow(botId, true);
            if (!accountInfo.Notified)
            {
                var mediaIds = new List<string>(1);
                if (profileInfo.ThumbnailPath is { } path)
                {
                    using var stream = File.OpenRead(path);
                    var m = await client.UploadMedia(stream);
                    mediaIds.Add(m.Id);
                }
                await client.PublishStatus($"""
                    新しいbotアカウント {profileInfo.Title}(@{profileInfo.Name}) を作成しました。
                    """, mediaIds: mediaIds);
                accountContext.UpdateAsNoTracking(accountInfo with { Notified = true });
                await accountContext.SaveChangesAsync();
            }
            // await client.PublishBotListStatus(botId, profileInfo);
            logger.LogInformation($"rep: @{status.Account.AccountName}, bot: @{profileInfo.Name}, repId: {status.Id}");
            if (!accountInfo.Replied)
            {
                await client.PublishStatus($"""
                    @{status.Account.AccountName}
                    @{profileInfo.Name} を作成しました。
                    """, status.Visibility, status.Id);
                accountContext.UpdateAsNoTracking(accountInfo with { Replied = true });
                await accountContext.SaveChangesAsync();
            }
            logger.LogInformation($"Created bot account @{profileInfo.Name}");
            var entity = accountContext.Remove(accountInfo);
            await entity.ReloadAsync();
            await accountContext.SaveChangesAsync();
        }
        await client.Favourite(status.Id);
    }
    var me = await client.GetCurrentUser();
    var convs = await client.GetConversations();
    var statuses = convs.Select(c => c.LastStatus)
        .OfType<Status>()
        .Where(s => s.Account.Id != me.Id && !(s.Favourited ?? false));
    foreach (var status in statuses)
    {
        CheckRssUrl(status, me.Id);
    }

    var ust = client.GetUserStreaming();
    var dm = client.GetDirectMessagesStreaming();
    ust.OnConversation += (_, e) => CheckRssUrl(e.Conversation.LastStatus, me.Id);
    dm.OnConversation += (_, e) => CheckRssUrl(e.Conversation.LastStatus, me.Id);
    await Task.WhenAll(ust.Start(), dm.Start());
}

static IEnumerable<Uri> GetUrls(string? content)
{
    if (content is null) return Enumerable.Empty<Uri>();
    var document = new HtmlDocument();
    document.LoadHtml(content);
    return document.DocumentNode
        .SelectNodes("//p/a[not(contains(@class,'hashtag')) or not(contains(@class,'mention'))]")?
        .OfType<HtmlNode>()
        .Where(n => !n.InnerText.StartsWith('#') && !n.InnerText.StartsWith('@'))
        .Select(link => new Uri(link.Attributes["href"].Value))
        .ToArray()
        ?? Enumerable.Empty<Uri>();
}

static async Task<string> CreateBot(IHttpClientFactory factory, string appAccessToken, ProfileInfo profileInfo, ILogger logger)
{
    using var mstdnClient = factory.CreateClient(Mastodon);
    mstdnClient.DefaultRequestHeaders.Authorization = new("Bearer", appAccessToken);
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
    HttpResponseMessage response;
    do
    {
        response = await mstdnClient.PostAsJsonAsync("/api/v1/accounts", createAccountData);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            logger.LogInformation("Too many create requests. Waiting for 30 minutes...");
            await Task.Delay(TimeSpan.FromMinutes(30));
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }
    } while (!response.IsSuccessStatusCode);
    var cred = await response.Content.ReadFromJsonAsync<Token>() ?? throw new Exception("Failed to create account");
    mstdnClient.DefaultRequestHeaders.Authorization = new("Bearer", cred.access_token);
    logger.LogInformation($"Created account @{createAccountData.username}");
    logger.LogInformation($"email: {createAccountData.email}");
    logger.LogInformation($"password: {createAccountData.password}");
    logger.LogInformation($"token: {cred.access_token}");
    return cred.access_token;
}

static async Task<string> WaitVerifiy(IHttpClientFactory factory, string accessToken, ILogger logger)
{
    using var client = factory.CreateClient(Mastodon);
    client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
    var response = await client.GetAsync("/api/v1/accounts/verify_credentials");
    while (!response.IsSuccessStatusCode)
    {
        // 403,422が返ってきたら、まだアカウントが作成されていないので、1分待って再度試行する
        if (response.StatusCode is not HttpStatusCode.Forbidden and not HttpStatusCode.UnprocessableEntity)
        {
            response.EnsureSuccessStatusCode();
        }
        logger.LogInformation("Waiting for account creation...");
        await Task.Delay(TimeSpan.FromMinutes(1));
        response = await client.GetAsync("/api/v1/accounts/verify_credentials");
    }
    var account = await response.Content.ReadFromJsonAsync<CredentialAccount>() ?? throw new Exception("Failed to get account info");
    return account.id;
}

static async Task SetupAccount(IHttpClientFactory factory, string accessToken, ProfileInfo profileInfo, string dispNamePrefix, ILogger logger, bool setAvatar = true, bool setHeader = true, bool setBio = true, bool setTags = true)
{
    var (_, iconPath, thumbnailPath, title, note, language, link, rss, keywords) = profileInfo;
    using var client = factory.CreateClient(Mastodon);
    client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
    // 3. プロフィール画像の設定
    var updateCredentialsUrl = "/api/v1/accounts/update_credentials";

    // apple-touch-icon.pngを取得して設定する
    if (!string.IsNullOrEmpty(iconPath) && setAvatar)
    {
        using var avatarFile = File.OpenRead(iconPath);
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(avatarFile), "avatar", "avatar.png");
        var response = await client.PatchAsync(updateCredentialsUrl, content);
        response.EnsureSuccessStatusCode();
    }
    // og:imageを取得して設定する
    if (!string.IsNullOrEmpty(thumbnailPath) && setHeader)
    {
        using var headerFile = File.OpenRead(thumbnailPath);
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(headerFile), "header", "header.png");
        var response = await client.PatchAsync(updateCredentialsUrl, content);
        response.EnsureSuccessStatusCode();
    }

    // 4. プロフィール文の設定
    if (setBio)
    {
        var dispName = dispNamePrefix + title;
        var response = await client.PatchAsJsonAsync(updateCredentialsUrl, new
        {
            display_name = dispName[..Math.Min(30, dispName.Length)],
            note,
            fields_attributes = new Dictionary<int, object>()
            {
                [0] = new { name = "Website", value = link, },
                [1] = new { name = "RSS", value = rss, },
            },
            bot = true,
            discoverable = true,
            source = new { language },
        });
        response.EnsureSuccessStatusCode();
    }

    // 5. タグの設定
    if (setTags)
    {
        var safe = new Regex(@"[\s\*_\-\[\]\(\)]");
        foreach (var keyword in keywords.Take(10))
        {
            var response = await client.PostAsJsonAsync("/api/v1/featured_tags", new { name = safe.Replace(keyword, "_") });
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning($"Failed to add tag: {keyword}");
            }
        }
    }
}

static async Task Test(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory, AccountContext accountContext, Uri uri)
{
    var info = await ProfileInfo.FetchFromWebsite(factory, uri);
    logger.LogInformation(info.ToString());
    // await accountContext.Database.EnsureCreatedAsync();
    // await accountContext.AccountInfos.AddAsync(new("hoge", "fuga", "piyo"));
    // await accountContext.SaveChangesAsync();
    // var client = new MastodonClient(options.Value.MastodonUrl.DnsSafeHost, options.Value.MonitoringToken);
    // var me = await client.GetCurrentUser();
    // var convs = await client.GetConversations();
    // var urls = convs.Select(c => c.LastStatus)
    //     .OfType<Status>()
    //     .Where(s => s.Account.Id != me.Id)
    //     .SelectMany(s => GetUrls(s.Content));
    // foreach (var url in urls)
    // {
    //     logger.LogInformation(url.ToString());
    // }
    // var config = await TomatoShriekerConfig.Load(options.Value.ConfigPath);
    // await config.Save(options.Value.ConfigPath);
}

static async Task Setup(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory, Uri uri, string accessToken)
{
    var info = await ProfileInfo.FetchFromWebsite(factory, uri);
    await SetupAccount(factory, accessToken, info, options.Value.DispNamePrefix, logger);
    var config = await TomatoShriekerConfig.Load(options.Value.ConfigPath);
    config.AddSource(info.Name, info.Rss, options.Value.MastodonUrl.AbsoluteUri, accessToken);
    await config.Save(options.Value.ConfigPath);
}

static async Task SetupAll(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory)
{
    var config = await TomatoShriekerConfig.Load(options.Value.ConfigPath);
    foreach (var source in config.Sources)
    {
        try
        {
            var profile = await ProfileInfo.FetchFromWebsite(factory, new Uri(source.Source.Feed));
            profile = profile with { Name = source.Id };
            logger.LogInformation(profile.ToString());
            await SetupAccount(factory, source.Dest.Mastodon.Token, profile, options.Value.DispNamePrefix, logger, false, false, true, false);
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        catch (System.Exception)
        {
            logger.LogWarning($"Failed to setup {source.Id}");
        }
    }
}

record Token(string access_token, string token_type, string scope, long created_at);
record CredentialAccount(string id);
record AppCredentials(string client_id, string client_secret, string vapid_key);
record BotInfo(string Id, string Token);
record ConsoleOptions
{
    public required Uri MastodonUrl { get; init; }
    public required string TootAppToken { get; init; }
    public required string MonitoringToken { get; init; }
    public required string ConfigPath { get; init; }
    public required string ReactiveTag { get; init; }
    public required string DispNamePrefix { get; init; } = string.Empty;

    public void Deconstruct(out Uri mastodonUrl, out string tootAppToken, out string monitoringToken, out string configPath, out string reactiveTag, out string dispNamePrefix)
        => (mastodonUrl, tootAppToken, monitoringToken, configPath, reactiveTag, dispNamePrefix)
        = (this.MastodonUrl, this.TootAppToken, this.MonitoringToken, this.ConfigPath, this.ReactiveTag, this.DispNamePrefix);
}
