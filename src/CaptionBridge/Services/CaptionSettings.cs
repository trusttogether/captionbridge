using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptionBridge.Services;

internal sealed class CaptionSettings
{
    public const int MinFontSize = 10;
    public const int MaxFontSize = 48;
    public const int MinOpacityPercent = 20;
    public const int MaxOpacityPercent = 100;
    public const int MinPollIntervalMs = 10;
    public const int MaxPollIntervalMs = 250;

    public int FontSize { get; set; } = 18;

    public int OpacityPercent { get; set; } = 100;

    /// <summary>How often the Live Captions window is re-read, in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 25;

    public bool AlwaysOnTop { get; set; } = true;

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public int WindowLeft { get; set; } = int.MinValue;

    public int WindowTop { get; set; } = int.MinValue;

    [JsonIgnore]
    public bool HasWindowPlacement =>
        WindowLeft != int.MinValue && WindowTop != int.MinValue && WindowWidth > 0 && WindowHeight > 0;

    public void Clamp()
    {
        FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize);
        OpacityPercent = Math.Clamp(OpacityPercent, MinOpacityPercent, MaxOpacityPercent);
        PollIntervalMs = Math.Clamp(PollIntervalMs, MinPollIntervalMs, MaxPollIntervalMs);
    }
}

internal static class CaptionSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaptionBridge",
        "settings.json");

    public static CaptionSettings Load()
    {
        CaptionSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<CaptionSettings>(File.ReadAllText(FilePath)) ?? new CaptionSettings()
                : new CaptionSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new CaptionSettings();
        }

        settings.Clamp();
        return settings;
    }

    public static void Save(CaptionSettings settings)
    {
        try
        {
            settings.Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; losing them must never take the window down.
        }
    }
}
