using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DistantWorlds.Types;

namespace FreightDispatch;

// Matrix values cap at 8,000 bytes. A header switches banks only after every
// chunk is read back, so a partial write leaves the prior complete history.
static class SavedData
{
    const string Owner = "freight-dispatch";
    const int ChunkSize = 7000, MaxChunks = 256, MaxBytes = 4 * 1024 * 1024;
    static Func<object, string, string, string>? getter;
    static Action<object, string, string, string>? setter;
    static readonly object channelGate = new();
    sealed record Header(int Version, string Bank, int Chunks, string Hash);

    internal static void RequireChannels()
    {
        lock (channelGate)
        {
            if (getter != null && setter != null) return;
            var live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")?.GetType("DW2ModLauncher.Loader.LiveChannels");
            var get = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string), typeof(int), typeof(Type)], null);
            getter = (Func<object, string, string, string>?)get?.Invoke(null, ["FreightDispatch", "saved-data.get", 1, typeof(Func<object, string, string, string>)]);
            setter = (Action<object, string, string, string>?)get?.Invoke(null, ["FreightDispatch", "saved-data.set", 1, typeof(Action<object, string, string, string>)]);
            if (getter == null || setter == null) throw new InvalidOperationException("Freight Dispatch requires Positronic Matrix saved-data.get/set; history is never session-only.");
        }
    }
    static string? Get(Galaxy g, string key) { RequireChannels(); return getter!(g, Owner, key); }
    static void Set(Galaxy g, string key, string? value)
    {
        RequireChannels(); setter!(g, Owner, key, value!);
        if (Get(g, key) != value) throw new IOException("Positronic Matrix did not store Freight Dispatch key " + key);
    }
    internal static string ObserverPrefix(short empireId) => "state.empire." + empireId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static bool HasLegacyHistory(Galaxy g) => g.GlobalVariables.ContainsKey("mod." + Owner + ".state.head");
    static Header? HeaderFor(Galaxy g, string prefix)
    {
        var text = Get(g, prefix + ".head");
        if (text == null)
        {
            if (g.GlobalVariables.ContainsKey("mod." + Owner + "." + prefix + ".head")) throw new InvalidDataException("Freight Dispatch header exists but cannot be read; it will not be overwritten.");
            return null;
        }
        var h = System.Text.Json.JsonSerializer.Deserialize<Header>(text);
        if (h == null || h.Version != 1 || (h.Bank != "a" && h.Bank != "b") || h.Chunks is < 1 or > MaxChunks || h.Hash?.Length != 64) throw new InvalidDataException("Invalid Freight Dispatch save header; it will not be overwritten.");
        return h;
    }
    public static string? Read(Galaxy g, string prefix = "state")
    {
        var h = HeaderFor(g, prefix); if (h == null) return null;
        var encoded = new StringBuilder();
        for (var i = 0; i < h.Chunks; i++)
        {
            var chunk = Get(g, $"{prefix}.{h.Bank}.{i}") ?? throw new InvalidDataException("Missing Freight Dispatch save chunk " + i);
            if (Encoding.UTF8.GetByteCount(chunk) > ChunkSize) throw new InvalidDataException("Oversized Freight Dispatch save chunk");
            encoded.Append(chunk);
        }
        byte[] compressed;
        try { compressed = Convert.FromBase64String(encoded.ToString()); }
        catch (FormatException ex) { throw new InvalidDataException("Freight Dispatch save encoding is corrupt.", ex); }
        if (Convert.ToHexString(SHA256.HashData(compressed)) != h.Hash) throw new InvalidDataException("Freight Dispatch save checksum mismatch.");
        using var input = new MemoryStream(compressed); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > MaxBytes) throw new InvalidDataException("Freight Dispatch save too large");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public static void Write(Galaxy g, string json, string prefix = "state")
    {
        var bytes = Encoding.UTF8.GetBytes(json); if (bytes.Length > MaxBytes) throw new InvalidDataException("Freight Dispatch state too large; nothing was truncated.");
        using var raw = new MemoryStream(bytes); using var stream = new MemoryStream(); using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, true)) raw.CopyTo(gzip);
        var compressed = stream.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(compressed)); var current = HeaderFor(g, prefix);
        if (current?.Hash == hash) { _ = Read(g, prefix); return; }
        var bank = current?.Bank == "a" ? "b" : "a"; var encoded = Convert.ToBase64String(compressed); var count = (encoded.Length + ChunkSize - 1) / ChunkSize;
        if (count is < 1 or > MaxChunks) throw new InvalidDataException("Freight Dispatch state needs too many chunks; nothing was truncated.");
        var countKey = $"{prefix}.{bank}.count"; _ = int.TryParse(Get(g, countKey), out var prior);
        if (prior is < 0 or > MaxChunks) throw new InvalidDataException("Invalid Freight Dispatch bank size");
        Set(g, countKey, Math.Max(prior, count).ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 0; i < count; i++) Set(g, $"{prefix}.{bank}.{i}", encoded.Substring(i * ChunkSize, Math.Min(ChunkSize, encoded.Length - i * ChunkSize)));
        for (var i = count; i < prior; i++) Set(g, $"{prefix}.{bank}.{i}", null);
        Set(g, countKey, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(g, prefix + ".head", System.Text.Json.JsonSerializer.Serialize(new Header(1, bank, count, hash)));
        _ = Read(g, prefix);
    }
}

