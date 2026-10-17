using System.Net;
using System.Text.Json;

namespace ShopEasy.EndToEndTests.Support;

// One answer of the system, with its body read once: the status, the headers, the text and, when
// the body is JSON, the parsed document. ToString is what a failed assertion prints.
public sealed class Reply
{
    private readonly JsonDocument? _json;

    public HttpStatusCode Status { get; }
    public string Body { get; }
    public HttpResponseMessage Message { get; }

    private Reply(HttpResponseMessage message, string body)
    {
        Message = message;
        Status = message.StatusCode;
        Body = body;
        if (body.Length > 0 && message.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            try { _json = JsonDocument.Parse(body); }
            catch (JsonException) { /* reported through ToString */ }
        }
    }

    public static async Task<Reply> ReadAsync(HttpResponseMessage message) =>
        new(message, await message.Content.ReadAsStringAsync());

    public JsonElement Json => _json?.RootElement
        ?? throw new InvalidOperationException($"The answer has no JSON body: {this}");

    public string? Header(string name) =>
        Message.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()
        : Message.Content.Headers.TryGetValues(name, out var contentValues) ? contentValues.FirstOrDefault()
        : null;

    public override string ToString()
    {
        var body = Body.Length > 600 ? Body[..600] + "…" : Body;
        return $"{(int)Status} {Status} {body}";
    }
}

public static class JsonReading
{
    // The property of a JSON object, or a failure that says what was there instead
    public static JsonElement Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOperationException($"Expected a property '{name}' in {Trim(element)}");

    public static string Text(this JsonElement element, string name) => element.Prop(name).GetString()!;

    public static decimal Number(this JsonElement element, string name) => element.Prop(name).GetDecimal();

    private static string Trim(JsonElement element)
    {
        var text = element.ToString();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
