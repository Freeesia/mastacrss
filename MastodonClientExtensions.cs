using HtmlAgilityPack;
using Mastonet;

static class MastodonClientExtensions
{
    public static async Task PublishBotListStatus(this MastodonClient client, string id, ProfileInfo newBot)
    {
        var statuses = await client.GetAccountStatuses(id, new() { Limit = 1 }, pinned: true);
        if (statuses is [{ } status])
        {
            var document = new HtmlDocument();
            document.LoadHtml(status.Content);
            // brタグ毎に分割して、改行する
            var nodes = document.DocumentNode.SelectNodes("//br") ?? Enumerable.Empty<HtmlNode>();
            foreach (var node in nodes)
            {
                node.ParentNode.ReplaceChild(document.CreateTextNode(Environment.NewLine), node);
            }
            var content = string.Join(
                Environment.NewLine + Environment.NewLine,
                document.DocumentNode.SelectNodes("//p").Select(p => p.InnerText));
            content = $"""
                {content}
                ・ {newBot.Title} ( @{newBot.Name} )
                """;
            if (content.Length < 500)
            {
                await client.EditStatus(status.Id, content);
                return;
            }
        }
        var res = await client.PublishStatus($"""
        botアカウント一覧

        ・ {newBot.Title} ( @{newBot.Name} )
        """);
        await client.Pin(res.Id);
    }
}