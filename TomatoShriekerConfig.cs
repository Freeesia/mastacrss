using System.Buffers;
using System.Linq.Expressions;
using VYaml.Annotations;
using VYaml.Emitter;
using VYaml.Parser;
using VYaml.Serialization;

namespace mastacrss;

[YamlObject]
partial record TomatoShriekerConfig
{
    static TomatoShriekerConfig()
    {
        BuiltinResolver.KnownGenericTypes[typeof(IReadOnlyList<>)] = typeof(IReadOnlyListFormatter<>);
    }
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

class IReadOnlyListFormatter<T> : IYamlFormatter<IReadOnlyList<T>?>
{
    private static readonly InterfaceReadOnlyListFormatter<T> innerFormatter = new();
    public void Serialize(ref Utf8YamlEmitter emitter, IReadOnlyList<T>? value, YamlSerializationContext context)
    {
        if (value is null)
        {
            emitter.WriteNull();
            return;
        }
        var indentGetter = CreateGetter<Utf8YamlEmitterGetter<int>>("currentIndentLevel");
        var indentSetter = CreateSetter<Utf8YamlEmitterSetter<int>>("currentIndentLevel");
        var indent = indentGetter(ref emitter);
        innerFormatter.Serialize(ref emitter, value, context);
        indentSetter(ref emitter, indent);
    }

    public IReadOnlyList<T>? Deserialize(ref YamlParser parser, YamlDeserializationContext context)
        => innerFormatter.Deserialize(ref parser, context);

    delegate T1 Utf8YamlEmitterGetter<T1>(ref Utf8YamlEmitter foo);
    delegate void Utf8YamlEmitterSetter<T1>(ref Utf8YamlEmitter foo, T1 value);
    private static TDelegate CreateGetter<TDelegate>(string memberName) where TDelegate : Delegate
    {
        var invokeMethod = typeof(TDelegate).GetMethod("Invoke");
        var delegateParameters = invokeMethod!.GetParameters();
        var paramType = delegateParameters[0].ParameterType;

        var objParam = Expression.Parameter(paramType, "obj");
        var memberExpr = Expression.PropertyOrField(objParam, memberName);
        Expression returnExpr = memberExpr;
        if (invokeMethod.ReturnType != memberExpr.Type)
            returnExpr = Expression.ConvertChecked(memberExpr, invokeMethod.ReturnType);

        var lambda = Expression.Lambda<TDelegate>(returnExpr, $"Getter{paramType.Name}_{memberName}", new[] { objParam });
        return lambda.Compile();
    }
    private static TDelegate CreateSetter<TDelegate>(string memberName) where TDelegate : Delegate
    {
        var invokeMethod = typeof(TDelegate).GetMethod("Invoke");
        var delegateParameters = invokeMethod!.GetParameters();
        var paramType = delegateParameters[0].ParameterType;
        var valueType = delegateParameters[1].ParameterType;

        var objParam = Expression.Parameter(paramType, "obj");
        var valueParam = Expression.Parameter(valueType, "value");
        var memberExpr = Expression.PropertyOrField(objParam, memberName);
        var setExpr = Expression.Assign(memberExpr, valueParam);
        var lambda = Expression.Lambda<TDelegate>(setExpr, $"Setter{paramType.Name}_{memberName}", new[] { objParam, valueParam });
        return lambda.Compile();
    }
}