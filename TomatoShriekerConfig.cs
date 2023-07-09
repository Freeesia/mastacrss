using System.Buffers;
using VYaml.Annotations;
using VYaml.Serialization;

namespace mastacrss;

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
}
[YamlObject]
partial record SourceInfo
{
    public required string Id { get; init; }
    public required Dest Dest { get; init; }
    public required Source Source { get; init; }
}
[YamlObject]
partial record Source
{
    public required string Feed { get; init; }
    [YamlMember("remote_keyword")]
    public required RemoteKeyword RemoteKeyword { get; init; }
    [YamlMember("remote_xpath_tags")]
    public string? RemoteXpathTags { get; init; }
}
[YamlObject]
partial record RemoteKeyword
{
    public required bool Enable { get; init; }
    public required IReadOnlyList<string>? Ignore { get; init; }
    [YamlMember("replace_rules")]
    public required IReadOnlyList<ReplaceRule>? ReplaceRules { get; init; }
}
[YamlObject]
partial record ReplaceRule
{
    public required string Pattern { get; init; }
    public required string Replace { get; init; }
}
[YamlObject]
partial record Dest
{
    public required AccountDest Account { get; init; }
    public required MastodonDest Mastodon { get; init; }
}
[YamlObject]
partial record AccountDest
{
    public required bool Bot { get; init; }
}
[YamlObject]
partial record MastodonDest
{
    public required string Url { get; init; }
    public required string Token { get; init; }
}
[YamlObject]
partial record CryptInfo
{
    public required string Password { get; init; }
}
