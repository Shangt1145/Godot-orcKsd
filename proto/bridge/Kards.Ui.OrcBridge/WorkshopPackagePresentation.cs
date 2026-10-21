using System.Text.Json;

namespace Kards.Ui.OrcBridge;

/// <summary>Display grouping is separate from manifest.requires, which describes expected references.</summary>
internal static class WorkshopPackagePresentation
{
    private static readonly HashSet<string> ShippedExpansions = new(StringComparer.Ordinal)
    {
        "exp-worldatwar", "exp-legions", "exp-winterwar", "exp-homefront", "exp-loyalty", "exp-warzone",
        "exp-comrades", "exp-navalwarfare", "exp-oceaniastorm", "exp-covertops", "exp-breakthrough", "exp-bloodandiron"
    };
    internal static string? Parent(string root, string id)
    {
        var path = Path.Combine(root, "workshop.presentation.json");
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("basePackId", out var field))
                return field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 } parent && parent != id ? parent : null;
        }
        // Explicit compatibility map for the shipped catalog; never infer third-party families from references or naming.
        return ShippedExpansions.Contains(id) ? "builtin" : null;
    }
}
