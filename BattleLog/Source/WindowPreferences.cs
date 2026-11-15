using System.Text.Json;
using Stride.Core.Mathematics;

namespace BattleLog;

// This is deliberately local machine state rather than campaign data. A saved
// game must never change because a player moved a tool window on their screen.
static class WindowPreferences
{
    sealed class StoredGeometry
    {
        public int Version { get; set; } = 1;
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    static string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DW2BattleLog", "window-v1.json");
    static string? pathForCheck;
    static bool loaded;
    static StoredGeometry? geometry;
    static string PathForCurrentUser => pathForCheck ?? defaultPath;

    internal static bool TryLoad(out Vector2 position, out Vector2 size)
    {
        Load();
        if (geometry == null) { position = default; size = default; return false; }
        position = new Vector2(geometry.X, geometry.Y); size = new Vector2(geometry.Width, geometry.Height); return true;
    }

    internal static void Save(Vector2 position, Vector2 size)
    {
        var next = new StoredGeometry { X = position.X, Y = position.Y, Width = size.X, Height = size.Y };
        try
        {
            var path = PathForCurrentUser; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, path, overwrite: true);
            geometry = next; loaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A read-only or unavailable profile must not affect live combat telemetry.
            Console.Error.WriteLine("Battle Log window position was not saved: " + ex.Message);
        }
    }

    static void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            var path = PathForCurrentUser;
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredGeometry>(File.ReadAllText(path));
            if (stored?.Version != 1 || !Valid(stored.X) || !Valid(stored.Y) || !Valid(stored.Width) || !Valid(stored.Height) || stored.Width <= 0 || stored.Height <= 0)
                throw new InvalidDataException("invalid window geometry");
            geometry = stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            geometry = null;
            Console.Error.WriteLine("Battle Log is using the default window position: " + ex.Message);
        }
    }

    static bool Valid(float value) => float.IsFinite(value) && Math.Abs(value) < 100000;

    // The isolated native harness redirects this before constructing a panel.
    internal static void UsePathForCheck(string? path)
    {
        pathForCheck = path; loaded = false; geometry = null;
    }
}
