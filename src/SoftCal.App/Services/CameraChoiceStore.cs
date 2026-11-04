using System.IO;
using System.Text.Json;

namespace SoftCal.App.Services;

/// <summary>The camera used last time: a phone or a webcam, and where the phone was reached (its address, its name).</summary>
internal sealed record CameraChoice(bool Phone, string? PhoneAddress = null, string? PhoneName = null);

/// <summary>Remembers the camera chosen in step 2, so the step opens as it was left (and finds the same phone again).</summary>
internal static class CameraChoiceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string FilePath => Path.Combine(AppPaths.Root, "camera.json");

    public static CameraChoice Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<CameraChoice>(File.ReadAllText(FilePath), Json) ?? new CameraChoice(false) : new CameraChoice(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CameraChoice(false);
        }
    }

    /// <summary>Saves the choice; a choice that cannot be saved is simply not remembered.</summary>
    public static void Save(CameraChoice choice)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(choice, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered.
        }
    }
}
