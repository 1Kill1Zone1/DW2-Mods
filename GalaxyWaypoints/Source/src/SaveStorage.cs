using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DistantWorlds.Types;
using HarmonyLib;

namespace GalaxyWaypoints;

// A complete snapshot is committed only after all chunks can be read back.
// Two reusable banks keep a failed write from replacing the last complete snapshot.
internal sealed class SaveStorage(Galaxy galaxy, int empireId, string statePrefix = "state")
{
    private const string Owner = "galaxy-waypoints";
    private const int ChunkSize = 7000, MaxChunks = 4096, MaxJsonBytes = 64 * 1024 * 1024;
    private readonly string prefix = statePrefix + "." + empireId;
    private readonly object gate = new();
    private static Func<object, string, string, string>? getter;
    private static Action<object, string, string, string>? setter;
    private static readonly object channelGate = new();

    public static void RequireChannels()
    {
        lock (channelGate)
        {
            if (getter != null && setter != null) return;
            var live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")
                ?.GetType("DW2ModLauncher.Loader.LiveChannels");
            var get = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null,
                [typeof(string), typeof(string), typeof(int), typeof(Type)], null);
            var read = (Func<object, string, string, string>?)get?.Invoke(null,
                ["GalaxyWaypoints", "saved-data.get", 1, typeof(Func<object, string, string, string>)]);
            var write = (Action<object, string, string, string>?)get?.Invoke(null,
                ["GalaxyWaypoints", "saved-data.set", 1, typeof(Action<object, string, string, string>)]);
            if (read == null || write == null)
                throw new InvalidOperationException("Positronic Matrix is required. Enable it before Galaxy Waypoints and restart through DW2 Mod Launcher.");
            getter = read; setter = write;
        }
    }

    private string? Get(string key) { RequireChannels(); return getter!(galaxy, Owner, key); }
    private void Set(string key, string? value)
    {
        RequireChannels();
        setter!(galaxy, Owner, key, value!);
        // Matrix reports rejected writes rather than throwing; read-back is essential.
        if (Get(key) != value) throw new IOException("Positronic Matrix did not store " + key);
    }

    private sealed record Header(int Version, string Bank, int Chunks, string Hash);
    private Header? ReadHeader()
    {
        string? text = Get(prefix + ".head");
        if (text == null)
        {
            if (galaxy.GlobalVariables.ContainsKey("mod." + Owner + "." + prefix + ".head"))
                throw new InvalidDataException("Positronic Matrix could not read the existing waypoint header. Legacy files will not replace it.");
            return null;
        }
        var h = JsonSerializer.Deserialize<Header>(text);
        if (h == null || h.Version != 1 || (h.Bank != "a" && h.Bank != "b") ||
            h.Chunks is < 1 or > MaxChunks || h.Hash?.Length != 64)
            throw new InvalidDataException("Invalid or unsupported waypoint save header. Legacy files will not replace it.");
        return h;
    }

    public WaypointSave? Read() => Read<WaypointSave>(WaypointSave.ValidateSerializedShape);

    internal T? Read<T>(Action<byte[]>? validateShape = null) where T : class
    {
        lock (gate)
        {
            var header = ReadHeader();
            if (header == null) return null;
            var encoded = new StringBuilder();
            for (int i = 0; i < header.Chunks; i++)
            {
                string part = Get($"{prefix}.{header.Bank}.{i}") ?? throw new InvalidDataException("Missing waypoint save chunk " + i);
                if (part.Length > ChunkSize) throw new InvalidDataException("Oversized waypoint save chunk");
                encoded.Append(part);
            }
            byte[] compressed = Convert.FromBase64String(encoded.ToString());
            if (Convert.ToHexString(SHA256.HashData(compressed)) != header.Hash)
                throw new InvalidDataException("Waypoint save checksum mismatch. Legacy files will not replace it.");
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer)) != 0)
            {
                if (output.Length + read > MaxJsonBytes) throw new InvalidDataException("Waypoint save exceeds supported size");
                output.Write(buffer, 0, read);
            }
            byte[] json = output.ToArray();
            validateShape?.Invoke(json);
            return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Empty waypoint save data");
        }
    }

    public void Write(WaypointSave state) => Write<WaypointSave>(state, WaypointSave.ValidateSerializedShape);

    internal void Write<T>(T state, Action<byte[]>? validateShape = null) where T : class
    {
        lock (gate)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(state);
            if (json.Length > MaxJsonBytes) throw new InvalidDataException("Waypoint state too large; nothing truncated");
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(json);
            byte[] compressed = stream.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(compressed));
            var current = ReadHeader();
            if (current?.Hash == hash) { _ = Read<T>(validateShape); return; }
            string bank = current?.Bank == "a" ? "b" : "a";
            string encoded = Convert.ToBase64String(compressed);
            int count = (encoded.Length + ChunkSize - 1) / ChunkSize;
            if (count > MaxChunks) throw new InvalidDataException("Waypoint state needs too many chunks; nothing truncated");
            string countKey = $"{prefix}.{bank}.count";
            int.TryParse(Get(countKey), out int previousCount);
            if (previousCount < 0 || previousCount > MaxChunks) throw new InvalidDataException("Invalid waypoint bank size");
            // Record the high-water mark before staging, so a retry can clean partially written chunks.
            Set(countKey, Math.Max(previousCount, count).ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < count; i++) Set($"{prefix}.{bank}.{i}", encoded.Substring(i * ChunkSize, Math.Min(ChunkSize, encoded.Length - i * ChunkSize)));
            for (int i = count; i < previousCount; i++) Set($"{prefix}.{bank}.{i}", null);
            Set(countKey, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(prefix + ".head", JsonSerializer.Serialize(new Header(1, bank, count, hash)));
        }
    }
}

internal sealed class WaypointSave
{
    [System.Text.Json.Serialization.JsonRequired] public int Version { get; set; } = 4;
    [System.Text.Json.Serialization.JsonRequired] public Waypoint[] Points { get; set; } = [];
    [System.Text.Json.Serialization.JsonRequired] public Dictionary<int, TransitGroup> Transit { get; set; } = new();
    [System.Text.Json.Serialization.JsonRequired] public Passage[] Traffic { get; set; } = [];

    // v1/v2 predate discovery state. Once a save declares v3, missing state must not silently reset already visited/notified nodes.
    public static void ValidateSerializedShape(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty(nameof(Version), out JsonElement version) || version.GetInt32() < 3) return;
        foreach (string property in new[] { nameof(VisitedGeneratedAnchors), nameof(DiscoveredGeneratedAnchors), nameof(NotifiedGeneratedAnchors) })
            if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException("Waypoint v3 discovery state is incomplete. Original data retained; no empty fallback.");
        if(version.GetInt32()==4)
            foreach(string property in new[]{nameof(GenerationProofs),nameof(GenerationStamp)})
                if(!root.TryGetProperty(property,out var value)||value.ValueKind==JsonValueKind.Null)
                    throw new InvalidDataException("Waypoint v4 generation evidence is incomplete; original retained");
    }

    public void Validate(Galaxy galaxy)
    {
        if (Version is not (1 or 2 or 3 or 4) || Points == null || Generated == null || Transit == null || Traffic == null || VisitedGeneratedAnchors == null ||
            DiscoveredGeneratedAnchors == null || NotifiedGeneratedAnchors == null || Points.Length > 32 || Generated.Length > 64 ||
            Points.Distinct().Count() != Points.Length || Points.Any(p => p.X <= 0 || p.Y <= 0 || p.X >= galaxy.SizeX || p.Y >= galaxy.SizeY) ||
            Transit.Any(p => p.Key < 0 || !galaxy.Systems.ContainsKey(p.Key) || (p.Value & ~TransitGroup.All) != 0) ||
            Generated.Any(n => !n.Valid(galaxy) || Points.Contains(n.Point)) || Generated.Select(n => n.Point).Distinct().Count() != Generated.Length ||
            Version < 4 && Generated.Select(n => n.AnchorSystemId).Distinct().Count() != Generated.Length ||
            VisitedGeneratedAnchors.Distinct().Count() != VisitedGeneratedAnchors.Length || VisitedGeneratedAnchors.Any(id => !galaxy.Systems.ContainsKey(id)) ||
            DiscoveredGeneratedAnchors.Distinct().Count() != DiscoveredGeneratedAnchors.Length || NotifiedGeneratedAnchors.Distinct().Count() != NotifiedGeneratedAnchors.Length ||
            DiscoveredGeneratedAnchors.Any(id => !Generated.Any(node => node.AnchorSystemId == id)) ||
            NotifiedGeneratedAnchors.Any(id => !DiscoveredGeneratedAnchors.Contains(id)) ||
            Traffic.Any(p => !(Points.Contains(p.Point) || Generated.Any(n => n.Point == p.Point)) || p.ShipId < 0))
            throw new InvalidDataException("Invalid waypoint, transit or traffic state. Original data retained; no empty fallback.");
        if(GenerationProofs==null||GenerationStamp==null||GenerationProofs.Length>64||
            GenerationProofs.Length!=0&&(GenerationProofs.Length!=Generated.Length||GenerationStamp.Length!=64||!GenerationStamp.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid generation evidence header; original retained");
        foreach(var proof in GenerationProofs)
            if(proof==null||!Generated.Any(n=>n.Point==proof.Node)||!galaxy.Systems.ContainsKey(proof.From)||!galaxy.Systems.ContainsKey(proof.To)||
                proof.From==proof.To||proof.Profile==null||string.IsNullOrWhiteSpace(proof.Profile.Name)||proof.Profile.Name.Length>512||
                !proof.Profile.Capability.Valid||proof.Profile.Capability.PoweredRange<=0||proof.Before==null||proof.After==null||
                proof.Before.Length>4096||proof.After.Length is <3 or >4096||!proof.After.Contains(proof.Node)||
                !double.IsFinite(proof.BeforeSeconds)||proof.BeforeSeconds<0&&proof.BeforeSeconds!=-1||
                !double.IsFinite(proof.WithoutNodeSeconds)||proof.WithoutNodeSeconds<0&&proof.WithoutNodeSeconds!=-1||
                (proof.BeforeSeconds<0)!=(proof.Before.Length==0)||
                proof.After[0]!=new Waypoint(galaxy.Systems.GetById(proof.From)!.GalaxyX,galaxy.Systems.GetById(proof.From)!.GalaxyY)||
                proof.After[^1]!=new Waypoint(galaxy.Systems.GetById(proof.To)!.GalaxyX,galaxy.Systems.GetById(proof.To)!.GalaxyY)||
                !UsefulNetwork.Benefit(proof.BeforeSeconds<0?double.PositiveInfinity:proof.BeforeSeconds,proof.AfterSeconds)||
                proof.WithoutNodeSeconds>=0&&proof.WithoutNodeSeconds<=proof.AfterSeconds+1e-6||
                proof.Before.Concat(proof.After).Any(p=>p.X<=0||p.Y<=0||p.X>=galaxy.SizeX||p.Y>=galaxy.SizeY))
                throw new InvalidDataException("Invalid generation route evidence; original retained");
        if(GenerationProofs.Select(p=>p.Node).Distinct().Count()!=GenerationProofs.Length)
            throw new InvalidDataException("Duplicate node evidence; original retained");
    }
    public GeneratedWaypoint[] Generated { get; set; } = [];
    public GenerationProof[] GenerationProofs { get; set; } = [];
    public string GenerationStamp { get; set; } = "";
    public int[] VisitedGeneratedAnchors { get; set; } = [];
    public int[] DiscoveredGeneratedAnchors { get; set; } = [];
    public int[] NotifiedGeneratedAnchors { get; set; } = [];
}

public static partial class Entry
{
    private static long nextStorageWarning;

    private static void InstallSaveStorage(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.WriteToStream)),
            prefix: new HarmonyMethod(typeof(Entry), nameof(StoreBeforeSerialization)) { priority = Priority.First });
    }

    private static void StoreBeforeSerialization(Galaxy __instance)
    {
        try { GetCampaign(__instance)?.Persist(); }
        catch (Exception ex)
        {
            Log("ERROR: Waypoint data could not be stored; stopping this save to avoid silent loss. " + ex);
            throw new InvalidOperationException("Galaxy Waypoints: save data unavailable. See galaxy-waypoints.log; original saves and legacy files are unchanged.", ex);
        }
    }
}
