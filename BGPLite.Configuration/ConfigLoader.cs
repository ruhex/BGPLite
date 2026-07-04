using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public static class ConfigLoader
{
    // Strict deserialization: unknown/typo'd YAML keys throw at load time (fail-loud) rather than
    // being silently swallowed. Operators get a clear "(Lin: N): Property 'X' not found" error at
    // startup pointing to the typo (#102).
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .Build();

    public static AppConfig Load(string path)
    {
        var yaml = File.ReadAllText(path);
        return Deserializer.Deserialize<AppConfig>(yaml);
    }

    public static AppConfig LoadFromText(string yaml) =>
        Deserializer.Deserialize<AppConfig>(yaml);

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .Build();

    public static string Save(AppConfig config) =>
        Serializer.Serialize(config);
}
