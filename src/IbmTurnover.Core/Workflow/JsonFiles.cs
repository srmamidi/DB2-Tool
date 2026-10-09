using System.Text.Json;

namespace IbmTurnover.Core.Workflow;

/// <summary>Reads the tool's JSON stores. A damaged file stops the action with its name and the fix, instead of a raw
/// parser trace (or, for a file holding "null", silently starting empty and overwriting it on the next save).</summary>
internal static class JsonFiles
{
    public static T ReadOrNew<T>(string path, JsonSerializerOptions? options = null) where T : class, new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options)
                   ?? throw new JsonException("the file holds null");
        }
        catch (JsonException ex)
        {
            throw new TurnoverException($"{path} is damaged ({ex.Message}). Restore it from backup, or move it aside to start empty. The tool will not overwrite it.");
        }
    }
}
