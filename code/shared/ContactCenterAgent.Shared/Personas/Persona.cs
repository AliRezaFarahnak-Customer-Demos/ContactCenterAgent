namespace ContactCenterAgent.Shared.Personas;

/// <summary>
/// One persona entry from the shared <c>personas.json</c> at the repo root.
/// Mirrors the JSON shape consumed by admin-chat (TypeScript) so the file is
/// the single source of truth across all services.
/// </summary>
public sealed record Persona(
    string Id,
    string Label,
    string? Emoji,
    string? Description,
    string Prompt,
    string? CountryCode,
    string? PhoneNumber,
    string? Language,
    string? LanguageCode);
