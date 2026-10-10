using System.Text.Json;

namespace Momentum4.CoreHost;

public sealed class PrivateProtocolException : Exception;

public sealed record PrivateRequest(long Id, string Operation, JsonElement Arguments);

public static class PrivateProtocol
{
    public const int RequestLimit = 65536;
    public const int ReplyLimit = 262144;
    public const int DepthLimit = 16;

    public static Task<byte[]?> ReadLineAsync(Stream input, CancellationToken cancellationToken) => new PrivateLineReader(input).ReadAsync(cancellationToken);

    public static JsonDocument Parse(ReadOnlyMemory<byte> input)
    {
        if (input.Length == 0 || input.Length > RequestLimit) throw new PrivateProtocolException();
        JsonDocument document;
        try { document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = DepthLimit }); }
        catch (JsonException) { throw new PrivateProtocolException(); }
        try { Unique(document.RootElement); }
        catch { document.Dispose(); throw; }
        return document;
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PrivateProtocolException();
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }

    public static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new PrivateProtocolException();
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || names.Any(name => !actual.Contains(name, StringComparer.Ordinal))) throw new PrivateProtocolException();
    }

    public static long Integer(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result)) throw new PrivateProtocolException();
        return result;
    }

    public static string Text(JsonElement value, string name, int maximum = 128)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) throw new PrivateProtocolException();
        var result = property.GetString()!;
        if (result.Length > maximum) throw new PrivateProtocolException();
        return result;
    }

    public static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new PrivateProtocolException();
        return property.GetBoolean();
    }

    public static PrivateRequest Request(JsonElement value)
    {
        Fields(value, "v", "kind", "id", "op", "args");
        if (Integer(value, "v") != 1 || Text(value, "kind") != "request") throw new PrivateProtocolException();
        var id = Integer(value, "id");
        if (id <= 0 || value.GetProperty("args").ValueKind != JsonValueKind.Object) throw new PrivateProtocolException();
        return new(id, Text(value, "op", 32), value.GetProperty("args").Clone());
    }
}
