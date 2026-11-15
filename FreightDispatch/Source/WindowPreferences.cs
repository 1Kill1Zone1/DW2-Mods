using System.Text.Json;
using Stride.Core.Mathematics;

namespace FreightDispatch;

// Window placement belongs to this machine, never to the campaign.
static class WindowPreferences
{
    sealed class StoredPosition
    {
        public int Version { get; set; } = 1;
        public float X { get; set; }
        public float Y { get; set; }
    }
    static string? checkPath;
    static bool loaded;
    static StoredPosition? position;
    static string FilePath => checkPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DW2FreightDispatch", "window-v1.json");

    internal static bool TryLoad(out Vector2 result)
    {
        if (!loaded)
        {
            loaded = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    var stored = JsonSerializer.Deserialize<StoredPosition>(File.ReadAllText(FilePath));
                    if (stored?.Version != 1 || !Valid(stored.X) || !Valid(stored.Y)) throw new InvalidDataException("Invalid window position");
                    position = stored;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { position = null; Console.Error.WriteLine("Freight Dispatch uses default window position: " + ex.Message); }
        }
        result = position == null ? default : new Vector2(position.X, position.Y);
        return position != null;
    }

    internal static void Save(Vector2 value)
    {
        if (!Valid(value.X) || !Valid(value.Y)) return;
        position = new StoredPosition { X = value.X, Y = value.Y }; loaded = true;
        try
        {
            var path = FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(position)); File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine("Freight Dispatch position remains session-only because it could not be saved: " + ex.Message); }
    }
    static bool Valid(float value) => float.IsFinite(value) && Math.Abs(value) < 100000;
    internal static void UsePathForCheck(string? path) { checkPath = path; loaded = false; position = null; }
}
