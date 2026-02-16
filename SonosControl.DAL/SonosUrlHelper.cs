namespace SonosControl.DAL;

/// <summary>Shared URL normalization for Sonos station/stream URIs.</summary>
public static class SonosUrlHelper
{
    /// <summary>Strips x-rincon-mp3radio prefix and trims. Use for display or matching.</summary>
    public static string NormalizeStationUrl(string? rawStationUrl) =>
        rawStationUrl?.Replace("x-rincon-mp3radio://", "", StringComparison.OrdinalIgnoreCase).Trim() ?? string.Empty;
}
