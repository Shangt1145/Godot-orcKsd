using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed record DiyTextResult(DiyCardDraft? Draft, IReadOnlyList<string> Problems)
{
    public bool Success => Draft is not null;
}

/// <summary>One parser/admission path for the card editor and the full Inspector.</summary>
public static class DiyTextAuthoring
{
    public static DiyTextResult Parse(DiyCardDraft draft, string text, IReadOnlyList<UiCardDefinition> catalog, bool append = false)
    {
        try
        {
            var compiled = OrcInspector.CompileText(draft.Id.Replace('/', '.'), text, draft.CardType, catalog);
            if (!compiled.ParseComplete || !compiled.AllCompiled) return new(null, compiled.Problems);
            using var parsed = JsonDocument.Parse(compiled.Parse.DesktopResultJson!);
            var graph = parsed.RootElement; var record = InspectorCardRecord.FromDraft(draft);
            var incoming = JsonNode.Parse(graph.GetProperty("effects").GetRawText())!.AsArray();
            if (append)
            {
                var existing = record["effects"]!.AsArray();
                // Each trigger owns its own target declarations. Do not discard graphs by merging empty Ops arrays.
                foreach (var effect in incoming) existing.Add(effect?.DeepClone());
            }
            else record["effects"] = incoming;
            foreach (var field in graph.GetProperty("cardFields").EnumerateObject()) record[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            foreach (var map in new[] { "kwMap", "kwValues" })
            {
                record[map] ??= new JsonObject();
                foreach (var word in graph.GetProperty(map).EnumerateObject()) record[map]![word.Name] = JsonNode.Parse(word.Value.GetRawText());
            }
            record["text"] = text; record[InspectorCardRecord.TextModeKey] = "detached";
            var updated = InspectorCardRecord.ApplyRecord(draft, JsonSerializer.SerializeToElement(record));
            var validation = DiyCardCompiler.Validate(updated, catalog);
            return validation.Success ? new(updated, []) : new(null, validation.Errors);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or JsonException)
        { return new(null, [error.Message]); }
    }
}
