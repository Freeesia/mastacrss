using System.Buffers;
using VYaml.Annotations;
using VYaml.Serialization;

namespace mastacrss;

[Obsolete]
[YamlObject]
partial record TomatoShriekerConfig
{
    public required string Environment { get; init; }
    public required IList<SourceInfo> Sources { get; init; }
    public required CryptInfo Crypt { get; init; }
    public static async ValueTask<TomatoShriekerConfig> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return await YamlSerializer.DeserializeAsync<TomatoShriekerConfig>(stream).ConfigureAwait(false);
    }

    public async ValueTask Save(string path)
    {
        var writer = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(writer, this);
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(writer.WrittenMemory);
    }

    public void AddSource(string name, string feed, string mastodonUrl, string mastodonToken, TimeSpan interval)
        => this.Sources.Add(new(name, new(new(), new(mastodonUrl, mastodonToken), new[] { name }), new($"{(int)interval.TotalMinutes}m"), new(feed, new(true, null, null))));
}

[Obsolete]
[YamlObject]
partial record SourceInfo(string Id, Dest Dest, Schedule Schedule, Source Source);
[Obsolete]
[YamlObject]
partial record Source(string Feed, [property: YamlMember("remote_keyword")] RemoteKeyword RemoteKeyword, [property: YamlMember("remote_xpath_tags")] string? RemoteXpathTags = null);
[Obsolete]
[YamlObject]
partial record RemoteKeyword(bool Enable, IReadOnlyList<string>? Ignore, [property: YamlMember("replace_rules")] IReadOnlyList<ReplaceRule>? ReplaceRules);
[Obsolete]
[YamlObject]
partial record ReplaceRule(string Pattern, string Replace);
[Obsolete]
[YamlObject]
partial record Dest(AccountDest Account, MastodonDest Mastodon, IReadOnlyList<string>? Tags);
[Obsolete]
[YamlObject]
partial record AccountDest(bool Bot = true);
[Obsolete]
[YamlObject]
partial record MastodonDest(string Url, string Token);
[Obsolete]
[YamlObject]
partial record CryptInfo(string Password);
[Obsolete]
[YamlObject]
partial record Schedule(string? Every = "20m");