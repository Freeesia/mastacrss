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
            var beforeContent = string.Join(
                Environment.NewLine,
                document.DocumentNode.SelectNodes("//br")?.Select(x => x.InnerText) ?? Array.Empty<string>());
            var newContent = $"""
            {beforeContent}
            ・ {newBot.Title} ( @{newBot.Name} )
            """;
            if (newContent.Length < 500)
            {
                await client.EditStatus(status.Id, newContent);
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