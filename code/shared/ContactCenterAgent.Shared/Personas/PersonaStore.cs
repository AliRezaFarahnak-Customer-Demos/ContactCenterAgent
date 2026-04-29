using System.Text.Json;

namespace ContactCenterAgent.Shared.Personas;

/// <summary>
/// Loads personas from the shared <c>personas.json</c> that ships next to the
/// consuming app's binaries (copied automatically by ContactCenterAgent.Shared.csproj).
/// </summary>
public static class PersonaStore
{
    private static readonly JsonDocumentOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Default location: <c>{AppContext.BaseDirectory}/personas.json</c>.
    /// Override only for tests.
    /// </summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "personas.json");

    /// <summary>Load all personas. Throws if the file is missing.</summary>
    public static IReadOnlyList<Persona> LoadAll(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Shared personas.json not found at '{path}'. Ensure your project references ContactCenterAgent.Shared.",
                path);

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, s_jsonOptions);

        var list = new List<Persona>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new Persona(
                Id: GetString(el, "id") ?? throw new InvalidOperationException("Persona missing 'id'"),
                Label: GetString(el, "label") ?? "",
                Emoji: GetString(el, "emoji"),
                Description: GetString(el, "description"),
                Prompt: GetString(el, "prompt") ?? throw new InvalidOperationException("Persona missing 'prompt'"),
                CountryCode: GetString(el, "countryCode"),
                PhoneNumber: GetString(el, "phoneNumber"),
                Language: GetString(el, "language"),
                LanguageCode: GetString(el, "languageCode")));
        }
        return list;
    }

    /// <summary>Find one persona by id (case-insensitive). Throws if unknown.</summary>
    public static Persona GetById(string personaId, string? path = null)
    {
        var all = LoadAll(path);
        var match = all.FirstOrDefault(p => string.Equals(p.Id, personaId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var ids = string.Join(", ", all.Select(p => p.Id));
            throw new InvalidOperationException(
                $"Persona '{personaId}' not found in personas.json. Available: {ids}");
        }
        return match;
    }

    /// <summary>Convenience: just the prompt text for a given persona id.</summary>
    public static string GetPrompt(string personaId, string? path = null)
        => GetById(personaId, path).Prompt;

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
