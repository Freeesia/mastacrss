using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EnsureThat;
using Mastonet;
using Mastonet.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PasswordGenerator;
using static SystemUtility;

namespace mastacrss;

/// <summary>
/// キューに積まれたアカウントを順次登録する
/// </summary>
class AccountRegisterer
{
    private readonly Channel<(AccountInfo request, ProfileInfo info)> createQueue = Channel.CreateUnbounded<(AccountInfo request, ProfileInfo info)>(new() { SingleReader = true, SingleWriter = true });
    private readonly Channel<(AccountInfo request, ProfileInfo info)> verifyQueue = Channel.CreateUnbounded<(AccountInfo request, ProfileInfo info)>(new() { SingleReader = true, SingleWriter = false });
    private readonly ILogger<AccountRegisterer> logger;
    private readonly IDbContextFactory<AccountContext> contextFactory;
    private readonly IHttpClientFactory factory;
    private readonly Uri mastodonUrl;
    private readonly string configPath;
    private readonly string tootAppToken;
    private readonly string monitoringToken;
    private readonly string dispNamePrefix;
    private readonly MastodonClient client;

    public AccountRegisterer(ILogger<AccountRegisterer> logger, IDbContextFactory<AccountContext> contextFactory, IOptions<ConsoleOptions> options, IHttpClientFactory factory)
    {
        this.logger = logger;
        this.contextFactory = contextFactory;
        this.factory = factory;
        var (mastodonUrl, tootAppToken, monitoringToken, configPath, _, dispNamePrefix) = options.Value;
        this.mastodonUrl = mastodonUrl;
        this.tootAppToken = tootAppToken;
        this.monitoringToken = monitoringToken;
        this.configPath = configPath;
        this.dispNamePrefix = dispNamePrefix;
        this.client = new MastodonClient(mastodonUrl.DnsSafeHost, monitoringToken, factory.CreateClient());
        _ = Task.WhenAll(
            Task.Run(StartCreateLoop),
            Task.Run(StartVerifyLoop)
        );
    }

    public async ValueTask<bool> QueueRequest(Uri url, string requestId)
    {
        this.logger.LogInformation($"request: url {url}, requestId {requestId}");
        using var context = await this.contextFactory.CreateDbContextAsync();
        if (await context.FindAsync<AccountInfo>(url, requestId) is null)
        {
            var info = new AccountInfo(url, requestId);
            await context.AddAsNoTracking(info);
            await context.SaveChangesAsync();
            return await RegistarAccountAsync(context, info);
        }
        return false;
    }

    private async Task StartCreateLoop()
    {
        using (var context = await this.contextFactory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
            var infos = await context.AccountInfos.Where(a => !a.Finished).ToArrayAsync();
            foreach (var info in infos)
            {
                try
                {
                    await RegistarAccountAsync(context, info);
                }
                catch (Exception e)
                {
                    SentrySdk.CaptureException(e);
                }
            }
        }
        await foreach (var (request, info) in this.createQueue.Reader.ReadAllAsync())
        {
            try
            {
                var req = request;
                var accounts = await client.GetAdminAccounts(new() { Limit = 1 }, AdminAccountOrigin.Local, username: info.Name);
                if (accounts is [])
                {
                    using var context = await this.contextFactory.CreateDbContextAsync();
                    var token = await CreateBot(info);
                    req = await context.UpdateAsNoTracking(req with { AccessToken = token });
                    accounts = await client.GetAdminAccounts(new() { Limit = 1 }, AdminAccountOrigin.Local, username: info.Name);
                    var account = accounts.Single();
                    await this.client.PublishStatus($"""
                        依頼サイト: {info.Link}
                        {new Uri(this.mastodonUrl, $"/admin/accounts/{account.Id}")}
                        """, Visibility.Direct);
                }
                await this.verifyQueue.Writer.WriteAsync((req, info));
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e);
            }
        }
    }

    private async Task StartVerifyLoop()
    {
        await foreach (var (request, info) in this.verifyQueue.Reader.ReadAllAsync())
        {
            try
            {
                var accounts = await client.GetAdminAccounts(new() { Limit = 1 }, AdminAccountOrigin.Local, username: info.Name);
                switch (accounts.SingleOrDefault())
                {
                    case null:
                        this.logger.LogInformation($"{info.Name}: アカウントがないので作成キューに積む");
                        await this.createQueue.Writer.WriteAsync((request, info));
                        break;
                    case { Disabled: true }:
                        using (var context = await this.contextFactory.CreateDbContextAsync())
                        {
                            await context.UpdateAsNoTracking(request with { Finished = true });
                        }
                        var status = await FallbackIfException(() => this.client.GetStatus(request.RequestId));
                        if (status is not null)
                        {
                            await this.client.PublishStatus($"""
                                @{status.Account.AccountName}
                                依頼されたサイト {request.Url} のアカウント作成は却下されました。
                                """, status.Visibility, status.Id);
                        }
                        break;
                    case { Confirmed: true } when request.BotId is not null:
                        await PostVerify(request, info);
                        break;
                    case { Confirmed: true, Id: var id }:
                        await PostVerify(request with { BotId = id }, info);
                        break;
                    default:
                        this.logger.LogInformation($"承認待ち: {info.Name}");
                        await this.verifyQueue.Writer.WriteAsync((request, info));
                        await Task.Delay(TimeSpan.FromMinutes(1));
                        break;
                }
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e);
            }
        }
    }

    private async Task<bool> RegistarAccountAsync(AccountContext context, AccountInfo request, CancellationToken cancellationToken = default)
    {
        Ensure.That(request.Finished).IsFalse();
        var status = await FallbackIfException(() => this.client.GetStatus(request.RequestId));
        if (status is null)
        {
            request = await context.UpdateAsNoTracking(request with { Finished = true }, cancellationToken);
            return false;
        }
        var profileInfo = await FallbackIfException(
            () => ProfileInfo.FetchFromWebsite(factory, request.Url),
            async ex =>
            {
                logger.LogWarning(ex, $"Failed to fetch profile info from {request.Url}");
                var text = $"""
                    @{status.Account.AccountName}
                    以下のURLのフィード情報の取得に失敗しました。別のURLをお試しください。
                    {request.Url}
                    """;
                await client.PublishStatus(text, status.Visibility, status.Id);
            });

        // RSS情報取れなければ終了
        if (profileInfo is null)
        {
            await context.UpdateAsNoTracking(request with { Finished = true }, cancellationToken);
            return false;
        }
        request = await context.UpdateAsNoTracking(request with { Name = profileInfo.Name }, cancellationToken);

        var accounts = await client.GetAdminAccounts(new() { Limit = 1 }, AdminAccountOrigin.Local, username: profileInfo.Name);
        // アカウントがなければ作成キューに積む
        // (一度作られても確認遅れると消えるので、アクセストークンあってもアカウントがないときがある)
        if (accounts is [])
        {
            logger.LogInformation($"{profileInfo.Name}: アカウントがないので作成キューに積む");
            await this.createQueue.Writer.WriteAsync((request, profileInfo), cancellationToken);
            return true;
        }
        var account = accounts.Single();
        // アクセストークンがないってことは新規リクエストなので、既存の結果を返す
        if (request.AccessToken is null)
        {
            logger.LogInformation($"Account @{profileInfo.Name} already exists. RSS: {profileInfo.Rss}");
            var text = account switch
            {
                { Confirmed: false } => $"""
                    @{status.Account.AccountName}
                    依頼されたサイトのアカウントは現在承認待ちです。
                    以下のアカウントになる予定です。
                    {account.Account!.ProfileUrl}
                    """,
                { Disabled: true } => $"""
                    @{status.Account.AccountName}
                    依頼されたサイト {request.Url} のアカウント作成は却下されました。
                    """,
                _ => $"""
                    @{status.Account.AccountName}
                    依頼されたサイトは @{profileInfo.Name} として作成済みです。
                    """,
            };
            await client.PublishStatus(text, status.Visibility, status.Id);
            await context.UpdateAsNoTracking(request with { Finished = true }, cancellationToken);
            return false;
        }
        // アクセストークンがある場合は作成後キューに積む
        else
        {
            logger.LogInformation($"{profileInfo.Name}: 全部終わってないので作成後キューに積む");
            await this.verifyQueue.Writer.WriteAsync((request, profileInfo), cancellationToken);
            return true;
        }
    }

    private async Task PostVerify(AccountInfo request, ProfileInfo info)
    {
        using var context = await this.contextFactory.CreateDbContextAsync();
        // botIdが追加されているのでいったん保存
        await context.UpdateAsNoTracking(request);
        var (_, _, _, botId, token, setuped, notified, replied, finished) = request;
        if (finished)
        {
            return;
        }
        _ = botId ?? throw new InvalidOperationException("botId is null");
        _ = token ?? throw new InvalidOperationException("token is null");
        if (!setuped)
        {
            await SetupAccount(factory, token, info, dispNamePrefix, logger);
            request = await context.UpdateAsNoTracking(request with { Setuped = true });
            this.logger.LogInformation($"アカウントセットアップ完了: @{info.Name}");
        }

        var config = await MastakerConfig.Load(configPath);
        if (!config.Feeds.Any(s => s.Id == info.Name))
        {
            config.Feeds.Add(new(info.Name, info.Rss, token));
            await config.Save(configPath);
            logger.LogInformation($"Saved config to {configPath}");
        }

        await client.Follow(botId, true);
        if (!notified)
        {
            var mediaIds = new List<string>(1);
            if (info.ThumbnailPath is { } path)
            {
                using var stream = File.OpenRead(path);
                var m = await client.UploadMedia(stream);
                mediaIds.Add(m.Id);
            }
            await client.PublishStatus($"""
                    新しいbotアカウント {info.Title}(@{info.Name}) を作成しました。
                    """, mediaIds: mediaIds);
            request = await context.UpdateAsNoTracking(request with { Notified = true });
        }
        var status = await this.client.GetStatus(request.RequestId);
        logger.LogInformation($"rep: @{status.Account.AccountName}, bot: @{info.Name}, repId: {status.Id}");
        if (!replied)
        {
            await client.PublishStatus($"""
                    @{status.Account.AccountName}
                    @{info.Name} を作成しました。
                    """, status.Visibility, status.Id);
            request = await context.UpdateAsNoTracking(request with { Replied = true });
        }
        logger.LogInformation($"Created bot account @{info.Name}");
        await context.UpdateAsNoTracking(request with { Finished = true });
    }

    async Task<string> CreateBot(ProfileInfo profileInfo)
    {
        using var mstdnClient = this.factory.CreateClient(Mastodon);
        mstdnClient.DefaultRequestHeaders.Authorization = new("Bearer", this.tootAppToken);
        // 1. アカウントの作成
        var pwd = new Password(32);
        // name = new Password(6).IncludeLowercase().Next();
        var createAccountData = new
        {
            username = profileInfo.Name,
            email = $"mastodon+{profileInfo.Name}@studiofreesia.com",
            password = pwd.Next(),
            agreement = true,
            locale = profileInfo.Lang,
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
            else if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                logger.LogInformation("アカウント作れなかったので、メールアドレスのブロック確認する");
                using var adminClient = factory.CreateClient(Mastodon);
                adminClient.DefaultRequestHeaders.Authorization = new("Bearer", this.monitoringToken);
                var testRes = await adminClient.PostAsJsonAsync("/api/v1/admin/canonical_email_blocks/test", new { createAccountData.email });
                testRes.EnsureSuccessStatusCode();
                var list = await testRes.Content.ReadFromJsonAsync<CanonicalEmail[]>();
                if (list is [var can])
                {
                    var res = await adminClient.DeleteAsync($"/api/v1/admin/canonical_email_blocks/{can.id}");
                    res.EnsureSuccessStatusCode();
                    logger.LogInformation("メールアドレスのブロック解除成功");
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                }
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }
        } while (!response.IsSuccessStatusCode);
        var cred = await response.Content.ReadFromJsonAsync<Token>() ?? throw new Exception("Failed to create account");
        logger.LogInformation($"Created account @{createAccountData.username}, email: {createAccountData.email}, pass: {createAccountData.password}, token: {cred.access_token}");
        return cred.access_token;
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
record CanonicalEmail(int id, string canonical_email_hash);