using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Tiler.NativeMethods;

namespace Tiler;

internal enum ModifierKey
{
    Shift,
    Ctrl,
    Alt,
}

internal sealed class Settings
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Pixels between tiles and around the edge of the screen.</summary>
    public int Gap { get; set; } = 8;

    /// <summary>How long windows glide to their tiles; 0 moves them instantly.</summary>
    public int AnimationMs { get; set; } = 220;

    /// <summary>Windows can be resized smaller than their tiles, leaving the rest of the tile empty.</summary>
    public bool FreeSize { get; set; }

    /// <summary>Held while dropping a window, takes it out of the layout.</summary>
    public ModifierKey FloatModifier { get; set; } = ModifierKey.Ctrl;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tiler");
    static string FilePath => Path.Combine(Folder, "settings.json");

    [JsonIgnore]
    public int FloatVirtualKey => FloatModifier switch
    {
        ModifierKey.Shift => VK_SHIFT,
        ModifierKey.Alt => VK_MENU,
        _ => VK_CONTROL,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Error("Could not read settings, using defaults", e);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Log.Error("Could not save settings", e);
        }
    }
}
