using CodeHollow.FeedReader;
using HtmlAgilityPack;
using static SystemUtility;

record ProfileInfo(string Name, string? IconPath, string? ThumbnailPath, string Title, string Description, string Lang, string Link, string Rss, string[] Keywords)
{
    public static async Task<ProfileInfo> FetchFromWebsite(Uri url)
    {
        var name = url.Host.Split('.').OrderByDescending(x => x.Length)
            .Where(s => s is not "www")
            .First()
            .Replace('-', '_');
        using var httpClient = new HttpClient();
        httpClient.BaseAddress = url;
        // ルートHTMLを取得
        var response = await httpClient.GetAsync("/");
        var document = new HtmlDocument();
        document.Load(await response.Content.ReadAsStreamAsync());

        // apple-touch-iconの画像をダウンロードしてパスを取得する
        string? iconPath = null;
        var appleTouchIconLink = document.DocumentNode.SelectSingleNode("//link[@rel='apple-touch-icon']")?.GetAttributeValue("href", string.Empty);
        if (!string.IsNullOrEmpty(appleTouchIconLink))
        {
            iconPath = await httpClient.DownloadAsync(appleTouchIconLink);
        }

        // keywordsを取得して、それをタグに設定する
        var keywords = document.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", string.Empty)
            .Split(',')
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray() ?? Array.Empty<string>();

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

        // urlがルートだったら、RSSフィードのURLを取得して、ちがったらそのまま使う
        var rssUrl = url.AbsoluteUri;
        var feed = await FallbackIfException(() => FeedReader.ReadAsync(rssUrl), _ => Task.CompletedTask);
        if (feed is null)
        {
            rssUrl = document.DocumentNode.SelectSingleNode("//link[@type='application/rss+xml']")
                ?.GetAttributeValue("href", string.Empty)
                ?? throw new InvalidOperationException("'application/rss+xml' not found");
            var rssUri = new Uri(rssUrl, UriKind.RelativeOrAbsolute);
            if (!rssUri.IsAbsoluteUri)
            {
                rssUri = new Uri(url, rssUri);
            }
            rssUrl = rssUri.AbsoluteUri;
            feed = await FeedReader.ReadAsync(rssUri.AbsoluteUri);
        }
        var description = feed.Description;
        if (string.IsNullOrEmpty(description))
        {
            description = (document.DocumentNode.SelectSingleNode("//meta[@name='description']")
                ?? document.DocumentNode.SelectSingleNode("//meta[@name='og:description']")
                ?? document.DocumentNode.SelectSingleNode("//meta[@name='twitter:description']"))
                ?.GetAttributeValue("content", string.Empty) ?? string.Empty;
        }
        var lang = feed.Language;
        if (string.IsNullOrEmpty(lang))
        {
            lang = "ja";
        }
        else if (lang.Length > 2)
        {
            lang = lang[..2];
        }
        // rssからtitle, description,link,languageを取得して設定する
        return new ProfileInfo(name, iconPath, thumbnailPath, feed.Title, description, lang, feed.Link, rssUrl, keywords);
    }
}
