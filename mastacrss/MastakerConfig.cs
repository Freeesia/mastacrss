using VYaml.Annotations;
using VYaml.Serialization;

namespace mastacrss;

[YamlObject(NamingConvention.SnakeCase)]
partial record MastakerConfig(string BaseUrl, TagConfig? Tag, IList<FeedConfig> Feeds)
{
    public static async ValueTask<MastakerConfig> Load(string path)
    {
        using var stream = File.OpenRead(path);
        var options = YamlSerializerOptions.Standard;
        return await YamlSerializer.DeserializeAsync<MastakerConfig>(stream, options)
            .ConfigureAwait(false);
    }

    public async ValueTask Save(string path)
    {
        var yaml = YamlSerializer.SerializeToString(this);
        // mastakerがnullを受け付けないので削除
        yaml = yaml.Replace(": null", ":");
        await File.WriteAllTextAsync(path, yaml);
    }
}

[YamlObject(NamingConvention.SnakeCase)]
partial record FeedConfig(string Id, string Url, string Token, TagConfig? Tag = null);

[YamlObject(NamingConvention.SnakeCase)]
partial record TagConfig(IReadOnlyList<string>? Always, IReadOnlyList<string>? Ignore, IReadOnlyList<string>? Replace, string? Xpath);