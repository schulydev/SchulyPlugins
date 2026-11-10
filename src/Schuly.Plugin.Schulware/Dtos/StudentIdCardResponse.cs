namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>The sanitized Schulnetz card. <c>Source</c> is <c>mobile</c> or <c>web</c>.</summary>
    public record StudentIdCardResponse(string Html, DateTimeOffset FetchedAt, string Source);
}
