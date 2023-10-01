using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Mastonet;
using Mastonet.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PasswordGenerator;
using Sentry;
using static SystemUtility;

namespace mastacrss;

/// <summary>
/// キューに積まれたアカウントを順次登録する
/// </summary>
class AccountRegisterer
{
    private readonly Channel<AccountInfo> requestQueue = Channel.CreateUnbounded<AccountInfo>(new() { SingleReader = true, SingleWriter = false });
    private readonly Channel<AccountInfo> createQueue = Channel.CreateUnbounded<AccountInfo>(new() { SingleReader = true, SingleWriter = true });
    private readonly ILogger<AccountRegisterer> logger;
    private readonly IHttpClientFactory factory;
    private readonly AccountContext accountContext;
    private readonly Uri mastodonUrl;
    private readonly string configPath;
    private readonly string tootAppToken;
    private readonly string dispNamePrefix;
    private readonly MastodonClient client;

    public AccountRegisterer(ILogger<AccountRegisterer> logger, AccountContext accountContext, IOptions<ConsoleOptions> options, IHttpClientFactory factory)
    {
        this.logger = logger;
        this.factory = factory;
        this.accountContext = accountContext;
        var (mastodonUrl, tootAppToken, monitoringToken, configPath, _, dispNamePrefix) = options.Value;
        this.mastodonUrl = mastodonUrl;
        this.tootAppToken = tootAppToken;
        this.configPath = configPath;
        this.dispNamePrefix = dispNamePrefix;
        this.client = new MastodonClient(mastodonUrl.DnsSafeHost, monitoringToken, factory.CreateClient());
        _ = Task.WhenAll(
            Task.Run(async () => await QueueCreate()),
            Task.Run(async () => await StartRegistarAccountAsync())
        );
    }

    public async ValueTask QueueRequest(Uri url, string requestId)
    {
        this.logger.LogInformation($"request: url {url}, requestId {requestId}");
        await this.requestQueue.Writer.WriteAsync(new(url, requestId));
    }

    private async Task QueueCreate(CancellationToken cancellationToken = default)
    {
        await this.accountContext.Database.EnsureCreatedAsync(cancellationToken);
        foreach (var info in this.accountContext.AccountInfos.Where(a => !a.Finished))
        {
            await this.createQueue.Writer.WriteAsync(info, cancellationToken);
        }
        await foreach (var info in this.requestQueue.Reader.ReadAllAsync(cancellationToken))
        {
            await this.accountContext.AddAsNoTracking(info, cancellationToken);
            await this.accountContext.SaveChangesAsync(cancellationToken);
            await this.createQueue.Writer.WriteAsync(info, cancellationToken);
        }
    }

    private async Task StartRegistarAccountAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var info in this.createQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await RegistarAccountAsync(info, cancellationToken);
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e);
            }
        }
    }

    private async Task RegistarAccountAsync(AccountInfo request, CancellationToken cancellationToken = default)
    {
        var status = await this.client.GetStatus(request.RequestId);
        var profileInfo = await FallbackIfException(
            () => ProfileInfo.FetchFromWebsite(factory, request.Url),
            async ex =>
            {
                logger.LogError(ex, $"Failed to fetch profile info from {request.Url}");
                await client.PublishStatus($"""
                        @{status.Account.AccountName}
                        以下のURLのフィード情報の取得に失敗しました。別のURLをお試しください。
                        {request.Url}
                        """, status.Visibility, status.Id);
            });

        // RSS情報取れなければ終了
        if (profileInfo is null)
        {
            await accountContext.UpdateAsNoTracking(request with { Finished = true }, cancellationToken);
            return;
        }
        request = request with { Name = profileInfo.Name };
        await accountContext.UpdateAsNoTracking(request, cancellationToken);

        // トークンがなければこのリクエストでアカウント作ってない
        if (request is not { AccessToken: { } token })
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
                await accountContext.UpdateAsNoTracking(request with { Finished = true }, cancellationToken);
                return;
            }

            token = await CreateBot(factory, tootAppToken, profileInfo, logger);
            request = request with { AccessToken = token };
            await accountContext.UpdateAsNoTracking(request, cancellationToken);
        }
        if (request is not { BotId: { } botId })
        {
            botId = await WaitVerifiy(factory, token, logger);
            await accountContext.UpdateAsNoTracking(request with { BotId = botId }, cancellationToken);
        }
        if (!request.Setuped)
        {
            await SetupAccount(factory, token, profileInfo, dispNamePrefix, logger);
            await accountContext.UpdateAsNoTracking(request with { Setuped = true }, cancellationToken);
        }

        var config = await TomatoShriekerConfig.Load(configPath);
        if (!config.Sources.Any(s => s.Id == profileInfo.Name))
        {
            config.AddSource(profileInfo.Name, profileInfo.Rss, mastodonUrl.AbsoluteUri, token, profileInfo.Interval);
            await config.Save(configPath);
            logger.LogInformation($"Saved config to {configPath}");
        }

        await client.Follow(botId, true);
        if (!request.Notified)
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
            await accountContext.UpdateAsNoTracking(request with { Notified = true }, cancellationToken);
        }
        // await client.PublishBotListStatus(botId, profileInfo);
        logger.LogInformation($"rep: @{status.Account.AccountName}, bot: @{profileInfo.Name}, repId: {status.Id}");
        if (!request.Replied)
        {
            await client.PublishStatus($"""
                    @{status.Account.AccountName}
                    @{profileInfo.Name} を作成しました。
                    """, status.Visibility, status.Id);
            await accountContext.UpdateAsNoTracking(request with { Replied = true }, cancellationToken);
        }
        logger.LogInformation($"Created bot account @{profileInfo.Name}");
        await accountContext.UpdateAsNoTracking(request with { Finished = true }, cancellationToken);
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

    public static async Task SetupAccount(IHttpClientFactory factory, string accessToken, ProfileInfo profileInfo, string dispNamePrefix, ILogger logger, SetupTarget? target = null)
    {
        var (_, iconPath, thumbnailPath, title, note, language, link, rss, keywords, _) = profileInfo;
        var (setAvatar, setHeader, setBio, setTags, setFixedInfo) = target ?? new();
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

        if (setFixedInfo)
        {
            var response = await client.PatchAsJsonAsync(updateCredentialsUrl, new
            {
                discoverable = true,
                indexable = true,
                bot = true,
            });
            response.EnsureSuccessStatusCode();
        }
    }
}

record RegsterRequest(Status Status, Uri Url);
record Token(string access_token, string token_type, string scope, long created_at);
record CredentialAccount(string id);
record AppCredentials(string client_id, string client_secret, string vapid_key);
record BotInfo(string Id, string Token);
record SetupTarget(bool Avatar = true, bool Header = true, bool Bio = true, bool Tags = true, bool FixedInfo = true);
