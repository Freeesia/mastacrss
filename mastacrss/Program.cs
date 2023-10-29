using System.Reflection;
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

var app = ConsoleApp.CreateBuilder(args)
    .ConfigureServices(
        (c, s) => s.Configure<ConsoleOptions>(c.Configuration)
            .AddDbContextFactory<AccountContext>(op => op.UseSqlite(c.Configuration.GetConnectionString("DefaultConnection"), b => b.CommandTimeout(60)))
            .AddSingleton<AccountRegisterer>()
            .AddHttpClient(Mastodon, (s, c) =>
            {
                var op = s.GetRequiredService<IOptions<ConsoleOptions>>();
                c.BaseAddress = op.Value.MastodonUrl;
            }))
    .ConfigureLogging((c, l) => l.AddConfiguration(c.Configuration).AddSentry())
    .Build();
app.AddRootCommand(Run);
app.AddCommand("test", Test);
app.AddCommand("config-test", ConfigTest);
app.AddCommand("setup", Setup);
app.AddCommand("setup-all", SetupAll);

using (app.Logger.BeginScope("startup"))
{
    app.Logger.LogInformation($"App: {app.Environment.ApplicationName}");
    app.Logger.LogInformation($"Env: {app.Environment.EnvironmentName}");
    var assembly = Assembly.GetExecutingAssembly();
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString();
    app.Logger.LogInformation($"Ver: {version}");
}

await app.RunAsync();

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
    var config = await TomatoShriekerConfig.Load(options.Value.ConfigPath);
    for (int i = 0; i < config.Sources.Count; i++)
    {
        var source = config.Sources[i];
        try
        {
            var info = await ProfileInfo.FetchFromWebsite(factory, new(source.Source.Feed));
            config.Sources[i] = source with { Schedule = new($"{(int)info.Interval.TotalMinutes}m") };
        }
        catch { }
    }
    await config.Save(options.Value.ConfigPath);
}

static async Task Setup(ILogger<Program> logger, IOptions<ConsoleOptions> options, IHttpClientFactory factory, Uri uri, string accessToken)
{
    var info = await ProfileInfo.FetchFromWebsite(factory, uri);
    await AccountRegisterer.SetupAccount(factory, accessToken, info, options.Value.DispNamePrefix, logger);
    var config = await TomatoShriekerConfig.Load(options.Value.ConfigPath);
    config.AddSource(info.Name, info.Rss, options.Value.MastodonUrl.AbsoluteUri, accessToken, info.Interval);
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
            await AccountRegisterer.SetupAccount(factory, source.Dest.Mastodon.Token, profile, options.Value.DispNamePrefix, logger);
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        catch (System.Exception)
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
