using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeHollow.FeedReader;
using CodeHollow.FeedReader.Feeds;
using HtmlAgilityPack;
using static SystemUtility;

partial record ProfileInfo(string Name, string? IconPath, string? ThumbnailPath, string Title, string Description, string Lang, string Link, string Rss, string[] Keywords)
{
    const string DescSuffix = """


        このアカウントはRSSフィードの内容を投稿するbotアカウントです。
        このアカウントの投稿に関するお問い合わせは @owner までお願いします。
        """;

    [GeneratedRegex("(?<!非)(公式|オフィシャル|\\sofficial)", RegexOptions.IgnoreCase)]
    private static partial Regex OfficialRegex();

    public static async Task<ProfileInfo> FetchFromWebsite(Uri url)
    {
        using var httpClient = new HttpClient();
        httpClient.BaseAddress = url;
        var document = new HtmlDocument();

        // urlがルートだったら、RSSフィードのURLを取得して、ちがったらそのまま使う
        var rssUrl = url.AbsoluteUri;
        var feed = await FallbackIfException(() => FeedReader.ReadAsync(rssUrl), _ => Task.CompletedTask);
        if (feed is null)
        {
            try
            {
                document.Load(await httpClient.GetStreamAsync(url));
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ArgumentException("404 Not Found", nameof(url), e);
            }
            rssUrl = document.DocumentNode.SelectSingleNode("//link[@type='application/rss+xml']")
                ?.GetAttributeValue("href", string.Empty)
                ?? throw new ArgumentException("'application/rss+xml' not found");
            var rssUri = new Uri(rssUrl, UriKind.RelativeOrAbsolute);
            if (!rssUri.IsAbsoluteUri)
            {
                rssUri = new Uri(url, rssUri);
            }
            rssUrl = rssUri.AbsoluteUri;
            feed = await FeedReader.ReadAsync(rssUri.AbsoluteUri);
        }
        else
        {
            document.Load(await httpClient.GetStreamAsync(feed.Link));
        }

        // プロフィールに記載するWebサイトのURL
        var siteUrl = new Uri(feed.Link, UriKind.RelativeOrAbsolute);
        if (!siteUrl.IsAbsoluteUri)
        {
            siteUrl = new Uri(url, siteUrl);
        }

        // YouTubeのチャンネルの場合、特殊処理する
        if (url.Host.EndsWith("youtube.com") && feed.SpecificFeed is AtomFeed atom)
        {
            var channelUrl = atom.Links.FirstOrDefault(x => x.Relation != "self")?.Href ?? throw new InvalidOperationException("channel url not found");
            document.Load(await httpClient.GetStreamAsync(channelUrl));
            var script = document.DocumentNode.SelectSingleNode("//script[contains(text(), 'ytInitialData')]")?.InnerText ?? throw new InvalidOperationException("ytInitialData not found");
            var json = Regex.Match(script, @"(?<=ytInitialData\s*=\s*)(?<json>{.*})(?=;)", RegexOptions.Singleline).Groups["json"].Value;
            var handleUrl = JsonDocument.Parse(json).SelectElement("$.metadata.channelMetadataRenderer.vanityChannelUrl")?.GetString() ?? throw new InvalidOperationException("vanityChannelUrl not found");
            siteUrl = new Uri(handleUrl, UriKind.Absolute);
            rssUrl = feed.Link;
        }

        // 安全な名前生成
        var nameSegs = siteUrl.Host.Split('.')
            // www: 汎用的なサブドメイン
            // m: モバイルサイト用サブドメイン(YouTubeとか)
            // com, jp, net, org, site, info: 汎用的なトップレベルドメイン
            .Where(s => s is not "www" and not "com" and not "jp" and not "net" and not "org" and not "co" and not "site" and not "info" and not "m")
            .Concat(siteUrl.Segments.Select(s => s.Trim('/')))
            .Where(s => !string.IsNullOrEmpty(s));
        var name = string.Join('_', nameSegs);
        name = Regex.Replace(name, "[^a-zA-Z0-9_]", string.Empty);
        name = name[..int.Min(30, name.Length)].ToString();

        // apple-touch-iconの画像をダウンロードしてパスを取得する
        string? iconPath = null;
        var appleTouchIconLink = document.DocumentNode.SelectSingleNode("//link[@rel='apple-touch-icon']")?.GetAttributeValue("href", string.Empty);
        if (!string.IsNullOrEmpty(appleTouchIconLink))
        {
            iconPath = await httpClient.DownloadAsync(appleTouchIconLink);
        }

        // ogpのタグを取得する
        var tags = document.DocumentNode
            // dotnetのXPathはends-withが使えないので、containsで代用する
            .SelectNodes("//meta[starts-with(@property, 'og:') and contains(@property, ':tag')]")?
            .Select(x => x.GetAttributeValue("content", string.Empty))
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray() ?? Array.Empty<string>();

        // og:*:tagがなかったら、keywordsを取得する
        // keywordsは、カンマ区切りの文字列か、スペース区切りの文字列か、だったり最後に...がついてたりするので低優先
        if (!tags.Any())
        {
            // keywordsを取得して、それをタグに設定する
            tags = document.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?
                .GetAttributeValue("content", string.Empty)
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray() ?? Array.Empty<string>();
            if (tags is [{ Length: > 0 } k])
            {
                tags = k.Split(' ')
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToArray();
            }
        }

        // og:imageの画像をダウンロードしてパスを取得する
        string? thumbnailPath = null;
        var imageLink = document.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", string.Empty);
        imageLink ??= document.DocumentNode.SelectSingleNode("//meta[@name='twitter:image:src']")?.GetAttributeValue("content", string.Empty);
        if (!string.IsNullOrEmpty(imageLink))
        {
            thumbnailPath = await httpClient.DownloadAsync(imageLink);
        }
        if (!File.Exists(iconPath) && File.Exists(thumbnailPath))
        {
            iconPath = thumbnailPath;
        }

        var description = feed.Description;
        if (string.IsNullOrEmpty(description))
        {
            var desc = document.DocumentNode.SelectSingleNode("//meta[@name='description']")
                ?? document.DocumentNode.SelectSingleNode("//meta[@name='og:description']")
                ?? document.DocumentNode.SelectSingleNode("//meta[@name='twitter:description']");
            description = desc?.GetAttributeValue("content", string.Empty) ?? string.Empty;
            // HTMLのエスケープを除去する
            description = HtmlEntity.DeEntitize(description);
        }
        // 公式って入ると勘違いするので抜く。けど「非公式」は残す
        description = OfficialRegex().Replace(description, string.Empty);
        description = description[..Math.Min(500 - DescSuffix.Length, description.Length)];
        description += DescSuffix;

        var lang = feed.Language;
        if (string.IsNullOrEmpty(lang))
        {
            lang = "ja";
        }
        else if (lang.Length > 2)
        {
            lang = lang[..2];
        }

        var title = feed.Title;
        // 公式って入ると勘違いするので抜く。けど「非公式」は残す
        title = OfficialRegex().Replace(title, string.Empty);
        title = title[..Math.Min(30, title.Length)];

        // rssからtitle, description,link,languageを取得して設定する
        return new ProfileInfo(name, iconPath, thumbnailPath, title, description, lang, siteUrl.AbsoluteUri, rssUrl, tags);
    }
}
