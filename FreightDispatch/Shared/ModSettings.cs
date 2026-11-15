using System.Text.Json;

namespace DW2Mod;

// The options text the launcher passes in, read by key. A key that is absent keeps the caller's current value (the mod's default); a key
// that is present with the wrong kind of value is reported as an error and also keeps the default.
internal sealed class ModSettings
{
    readonly JsonElement root;

    ModSettings(JsonElement root)
    {
        this.root = root;
    }

    // invalid options text is reported and every setting then keeps its default
    public static ModSettings Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return new ModSettings(doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            ModHost.Error($"reading the options failed, every setting keeps its default: {ex.Message}");
            using JsonDocument empty = JsonDocument.Parse("{}");
            return new ModSettings(empty.RootElement.Clone());
        }
    }

    public bool Bool(string key, bool current)
    {
        if (!root.TryGetProperty(key, out JsonElement v))
        {
            return current;
        }
        if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
        {
            return v.GetBoolean();
        }
        return Wrong(key, "true or false", v, current);
    }

    // min / max are the limits the mod can work with; a value outside them is reported as an error and the nearest limit is used
    public float Num(string key, float current, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        if (!root.TryGetProperty(key, out JsonElement v))
        {
            return current;
        }
        if (v.ValueKind == JsonValueKind.Number)
        {
            return InRange(key, v.GetSingle(), min, max);
        }
        return Wrong(key, "a number", v, current);
    }

    public int Int(string key, int current, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!root.TryGetProperty(key, out JsonElement v))
        {
            return current;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
        {
            return InRange(key, n, min, max);
        }
        return Wrong(key, "a whole number", v, current);
    }

    public string Text(string key, string current)
    {
        if (!root.TryGetProperty(key, out JsonElement v))
        {
            return current;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }
        return Wrong(key, "text", v, current);
    }

    // a list of text values (the launcher's TranslationFiles); absent is empty, anything but an array of text is reported as an error and gives none
    public List<string> Texts(string key)
    {
        List<string> texts = new();
        if (!root.TryGetProperty(key, out JsonElement v))
        {
            return texts;
        }
        if (v.ValueKind != JsonValueKind.Array)
        {
            Wrong(key, "a list of text", v, "");
            return texts;
        }
        foreach (JsonElement item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                Wrong(key, "a list of text", v, "");
                return new List<string>();
            }
            texts.Add(item.GetString());
        }
        return texts;
    }

    // also used for values that do not come from the options text (environment overrides)
    public static float InRange(string key, float value, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        float used = Math.Clamp(value, min, max);
        if (used != value)
        {
            ModHost.Error($"setting {key} failed: {value} is outside {Bounds(min, max)}; using {used}");
        }
        return used;
    }

    public static int InRange(string key, int value, int min = int.MinValue, int max = int.MaxValue)
    {
        int used = Math.Clamp(value, min, max);
        if (used != value)
        {
            ModHost.Error($"setting {key} failed: {value} is outside {Bounds(min, max)}; using {used}");
        }
        return used;
    }

    static string Bounds(float min, float max)
        => float.IsNegativeInfinity(min) ? $"at most {max}" : float.IsPositiveInfinity(max) ? $"at least {min}" : $"{min} to {max}";

    static string Bounds(int min, int max)
        => min == int.MinValue ? $"at most {max}" : max == int.MaxValue ? $"at least {min}" : $"{min} to {max}";

    static T Wrong<T>(string key, string expected, JsonElement v, T current)
    {
        ModHost.Error($"setting {key} failed: expected {expected}, got {v.ValueKind} {v.GetRawText()}; keeping {current}");
        return current;
    }
}
