using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using ConsoleAppFramework;
using HtmlAgilityPack;
using mastacrss;
using Mastonet;
using Mastonet.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static SystemUtility;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.Configure<ConsoleOptions>(configuration)
    .AddLogging(b => b
        .AddConfiguration(configuration)
        .AddSentry(op =>
        {
            // Sentryを無効化するために空のDSNを設定する必要があるけど、環境変数からは空文字を設定できないので、ここで設定する
            // 環境変数でDNSが設定されているときはそちらが優先されるはず
            op.Dsn = "";
            op.SampleRate = 0.25f;
            op.CaptureFailedRequests = true;
            op.SetBeforeSend(BeforeSend);
        })
        .AddConsole())
    .AddDbContextFactory<AccountContext>(op => op.UseSqlite(configuration.GetConnectionString("DefaultConnection"), b => b.CommandTimeout(60)))
    .AddSingleton<AccountRegisterer>()
    .AddHttpClient(Mastodon, (s, c) =>
    {
        var op = s.GetRequiredService<IOptions<ConsoleOptions>>();
        c.BaseAddress = op.Value.MastodonUrl;
    });

static SentryEvent? BeforeSend(SentryEvent ev, SentryHint hint)
{
    // ConnectionRefusedの場合はサーバー起動してないので無視
    if (ev.Exception is HttpRequestException &&
        ev.Exception.InnerException is SocketException se &&
        se.SocketErrorCode == SocketError.ConnectionRefused)
    {
        return null;
    }
    // ConnectionClosedPrematurelyの場合はたぶんサーバーが再起動したので、再接続する
    else if (ev.Exception is WebSocketException wse &&
        wse.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
    {
        return null;
    }
    return ev;
}

using var serviceProvider = services.BuildServiceProvider();
ConsoleApp.ServiceProvider = serviceProvider;

var app = ConsoleApp.Create();
app.Add("", Run);
app.Add("test", Test);
app.Add("config-test", ConfigTest);
app.Add("setup", Setup);
app.Add("setup-all", SetupAll);

using (var sp = serviceProvider.CreateScope())
{
    var logger = sp.ServiceProvider.GetRequiredService<ILogger<Program>>();
    using var scope = logger.BeginScope("startup");
    var assembly = Assembly.GetExecutingAssembly();
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString();
    logger.LogInformation($"Ver: {version}");
}
await app.RunAsync(args);

static async Task Run(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory, AccountRegisterer registerer)
{
    var (mastodonUrl, _, monitoringToken, _, reactiveTag, _) = options.Value;
    var client = new MastodonClient(mastodonUrl.DnsSafeHost, monitoringToken, factory.CreateClient());
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
        var bRef = false;
        foreach (var url in GetUrls(status.Content))
        {
            bRef |= await registerer.QueueRequest(url, status.Id);
        }
        await client.Favourite(status.Id);
        if (bRef)
        {
            await client.PublishStatus($"""
                @{status.Account.AccountName}
                bot作成依頼を受け付けました。
                順次作成しますので、しばらくお待ちください。
                """, status.Visibility, status.Id);
        }
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

    var dm = client.GetDirectMessagesStreaming();
    dm.OnConversation += (_, e) => CheckRssUrl(e.Conversation.LastStatus, me.Id);
    await dm.Start();
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

static async Task ConfigTest(IOptions<ConsoleOptions> options, IHttpClientFactory factory)
{
    // #pragma warning disable CS0612
    //     var oldConfig = await TomatoShriekerConfig.Load(options.Value.ConfigPath);
    // #pragma warning restore CS0612
    //     var newConfig = new MastakerConfig(oldConfig.Sources[0].Dest.Mastodon.Url, null, []);
    //     foreach (var feed in oldConfig.Sources)
    //     {
    //         var xpath = feed.Source.RemoteXpathTags;
    //         var ignores = feed.Source.RemoteKeyword.Ignore ?? [];
    //         var tag = !string.IsNullOrEmpty(xpath) || ignores.Any() ? new TagConfig(null, ignores, null, xpath) : null;
    //         newConfig.Feeds.Add(new(feed.Id, feed.Source.Feed, feed.Dest.Mastodon.Token, tag));
    //     }
    var newConfig = await MastakerConfig.Load(options.Value.ConfigPath);
    await newConfig.Save(options.Value.ConfigPath + "_new");
}

static async Task Setup(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory, Uri uri, string accessToken)
{
    var info = await ProfileInfo.FetchFromWebsite(factory, uri);
    await AccountRegisterer.SetupAccount(factory, accessToken, info, options.Value.DispNamePrefix, logger);
    var config = await MastakerConfig.Load(options.Value.ConfigPath);
    config.Feeds.Add(new(info.Name, info.Rss, accessToken));
    await config.Save(options.Value.ConfigPath);
}

static async Task SetupAll(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory)
{
    var config = await MastakerConfig.Load(options.Value.ConfigPath);
    foreach (var source in config.Feeds)
    {
        try
        {
            var profile = await ProfileInfo.FetchFromWebsite(factory, new Uri(source.Url));
            profile = profile with { Name = source.Id };
            logger.LogInformation(profile.ToString());
            await AccountRegisterer.SetupAccount(factory, source.Token, profile, options.Value.DispNamePrefix, logger);
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            logger.LogWarning($"Failed to setup {source.Id}");
        }
    }
}

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
