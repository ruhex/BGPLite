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
        return LoadFromText(yaml);
    }

    public static AppConfig LoadFromText(string yaml) =>
        // YamlDotNet returns null for an empty/whitespace document ("", "   ", "---") — fail loud
        // with a clear message instead of an NRE at the first config use (#321 item 6). The
        // hot-reload path logs it and keeps the previous config, as with any other bad edit.
        Deserializer.Deserialize<AppConfig>(yaml)
            ?? throw new InvalidOperationException("The configuration is empty — nothing to load.");
}
