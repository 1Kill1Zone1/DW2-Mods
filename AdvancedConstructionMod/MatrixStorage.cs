using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DistantWorlds.Types;

namespace AdvancedConstructionMod;

// A header switches between two complete banks only after every staged chunk has read back.
internal sealed class MatrixStorage(Galaxy galaxy, short empireId)
{
    private const string Owner = "AdvancedConstructionMod";
    private const int ChunkSize = 7000, MaxChunks = 4096, MaxJsonBytes = 8 * 1024 * 1024;
    private readonly string prefix = "construction." + empireId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly object gate = new();
    private static readonly object channelGate = new();
    private static Func<object, string, string, string>? getter;
    private static Action<object, string, string, string>? setter;

    private sealed record Header(int Version, string Bank, int Chunks, string Hash);

    private static void RequireChannels()
    {
        lock (channelGate)
        {
            if (getter != null && setter != null) return;
            Type? live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")
                ?.GetType("DW2ModLauncher.Loader.LiveChannels");
            MethodInfo? get = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null,
                [typeof(string), typeof(string), typeof(int), typeof(Type)], null);
            getter = (Func<object, string, string, string>?)get?.Invoke(null,
                [Owner, "saved-data.get", 1, typeof(Func<object, string, string, string>)]);
            setter = (Action<object, string, string, string>?)get?.Invoke(null,
                [Owner, "saved-data.set", 1, typeof(Action<object, string, string, string>)]);
            if (getter == null || setter == null)
                throw new InvalidOperationException("Advanced Construction Mod requires Positronic Matrix. Enable it and restart through DW2 Mod Launcher.");
        }
    }

    private string? Get(string key) { RequireChannels(); return getter!(galaxy, Owner, key); }
    private void Set(string key, string? value)
    {
        RequireChannels();
        setter!(galaxy, Owner, key, value!);
        if (Get(key) != value) throw new IOException("Positronic Matrix rejected " + key + ".");
    }

    private Header? ReadHeader()
    {
        string? text = Get(prefix + ".head");
        if (text == null)
        {
            if(galaxy.GlobalVariables.ContainsKey("mod."+Owner+"."+prefix+".head"))
                throw new InvalidDataException("Advanced Construction metadata exists but cannot be read; it was not replaced.");
            return null;
        }
        Header? header = JsonSerializer.Deserialize<Header>(text);
        if (header == null || header.Version != 1 || (header.Bank != "a" && header.Bank != "b") ||
            header.Chunks is < 1 or > MaxChunks || header.Hash?.Length != 64)
            throw new InvalidDataException("Advanced Construction metadata header is corrupt or unsupported.");
        return header;
    }

    public ConstructionSnapshot? Read()
    {
        lock (gate)
        {
            Header? header = ReadHeader();
            if (header == null) return null;
            var encoded = new StringBuilder();
            for (int i = 0; i < header.Chunks; i++)
            {
                string part = Get($"{prefix}.{header.Bank}.{i}") ?? throw new InvalidDataException("Advanced Construction metadata chunk is missing.");
                if (part.Length > ChunkSize) throw new InvalidDataException("Advanced Construction metadata chunk is oversized.");
                encoded.Append(part);
            }
            byte[] compressed = Convert.FromBase64String(encoded.ToString());
            if (Convert.ToHexString(SHA256.HashData(compressed)) != header.Hash)
                throw new InvalidDataException("Advanced Construction metadata checksum mismatch.");
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer)) != 0)
            {
                if (output.Length + read > MaxJsonBytes) throw new InvalidDataException("Advanced Construction metadata exceeds the supported size.");
                output.Write(buffer, 0, read);
            }
            using var document=JsonDocument.Parse(output.ToArray());
            if(document.RootElement.TryGetProperty("Version",out var version))
            {
                if(version.GetInt32()>=2&&!document.RootElement.TryGetProperty("Targets",out _))throw new InvalidDataException("Series targets are missing from metadata.");
                if(version.GetInt32()>=3&&!document.RootElement.TryGetProperty("AutoUpgrade",out _))throw new InvalidDataException("Series automatic-upgrade settings are missing from version 3 metadata.");
                if(version.GetInt32()>=4&&!document.RootElement.TryGetProperty("ManualRetrofits",out _))throw new InvalidDataException("Individual retrofit orders are missing from version 4 metadata.");
            }
            var state = JsonSerializer.Deserialize<ConstructionSnapshot>(output.ToArray())
                ?? throw new InvalidDataException("Advanced Construction metadata is empty.");
            state.Validate();
            return state;
        }
    }

    public void Write(ConstructionSnapshot state)
    {
        state.Validate();
        lock (gate)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(state);
            if (json.Length > MaxJsonBytes) throw new InvalidDataException("Advanced Construction metadata is too large; nothing was truncated.");
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(json);
            byte[] bytes = compressed.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            string? previousHead = Get(prefix + ".head");
            Header? current = ReadHeader();
            if (current?.Hash == hash) { _ = Read(); return; }
            string bank = current?.Bank == "a" ? "b" : "a";
            string encoded = Convert.ToBase64String(bytes);
            int count = (encoded.Length + ChunkSize - 1) / ChunkSize;
            if (count is < 1 or > MaxChunks) throw new InvalidDataException("Advanced Construction metadata needs too many chunks.");
            string countKey = $"{prefix}.{bank}.count";
            _ = int.TryParse(Get(countKey), out int oldCount);
            if (oldCount is < 0 or > MaxChunks) throw new InvalidDataException("Advanced Construction staged bank size is invalid.");
            Set(countKey, Math.Max(oldCount, count).ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < count; i++) Set($"{prefix}.{bank}.{i}", encoded.Substring(i * ChunkSize, Math.Min(ChunkSize, encoded.Length - i * ChunkSize)));
            for (int i = count; i < oldCount; i++) Set($"{prefix}.{bank}.{i}", null);
            Set(countKey, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                Set(prefix + ".head", JsonSerializer.Serialize(new Header(1, bank, count, hash)));
                var reread = Read() ?? throw new IOException("Advanced Construction metadata was not readable after commit.");
                if (!Equivalent(state, reread)) throw new IOException("Advanced Construction metadata read-back differs from staged state.");
            }
            catch
            {
                // The old header still addresses a complete bank. Restore it if a header switch partially reached Matrix.
                try { Set(prefix + ".head", previousHead); } catch { }
                throw;
            }
        }
    }

    private static bool Equivalent(ConstructionSnapshot left, ConstructionSnapshot right) =>
        left.Version == right.Version && left.Revision == right.Revision && left.Classes.OrderBy(pair => pair.Key).SequenceEqual(right.Classes.OrderBy(pair => pair.Key)) &&
        left.DesignClasses.OrderBy(pair => pair.Key).SequenceEqual(right.DesignClasses.OrderBy(pair => pair.Key))&&left.Targets.OrderBy(p=>p.Key).SequenceEqual(right.Targets.OrderBy(p=>p.Key))&&left.AutoUpgrade.OrderBy(p=>p.Key).SequenceEqual(right.AutoUpgrade.OrderBy(p=>p.Key))&&left.ManualRetrofits.OrderBy(p=>p.Key).SequenceEqual(right.ManualRetrofits.OrderBy(p=>p.Key));
}
