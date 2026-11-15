using System.Text.Json.Serialization;

namespace AdvancedConstructionMod;

// V1 has implicit targets, V2 explicit targets, V3 independent component-upgrade settings.
public sealed class ConstructionSnapshot
{
    public const int CurrentVersion=3;
    [JsonRequired] public int Version { get; set; } = CurrentVersion;
    [JsonRequired] public long Revision { get; set; }
    [JsonRequired] public Dictionary<string, string> Classes { get; set; } = new(StringComparer.Ordinal);
    [JsonRequired] public Dictionary<int, string> DesignClasses { get; set; } = new();
    public Dictionary<string,int> Targets { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string,bool> AutoUpgrade { get; set; } = new(StringComparer.Ordinal);
    public static string TargetKey(string series,int role)=>series+":"+role.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ConstructionSnapshot Clone() => new()
    {
        Version = Version,
        Revision = Revision,
        Classes = new(Classes, StringComparer.Ordinal),
        DesignClasses = new(DesignClasses),Targets=new(Targets,StringComparer.Ordinal),AutoUpgrade=new(AutoUpgrade,StringComparer.Ordinal)
    };

    public void Validate()
    {
        if (Classes == null || DesignClasses == null || Targets==null || AutoUpgrade==null || Version is not (1 or 2 or 3) || Version==1&&Targets.Count!=0 || Version<3&&AutoUpgrade.Count!=0 || Revision < 0 || Classes.Count > 512 || DesignClasses.Count > 200_000 ||Targets.Count>8192 ||AutoUpgrade.Count>8192 ||
            Classes.Any(pair => !Guid.TryParseExact(pair.Key, "N", out _) || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 80) ||
            DesignClasses.Any(pair => pair.Key < 0 || !Classes.ContainsKey(pair.Value))||
            Targets.Any(p=>!ValidRoleKey(p.Key)||p.Value<0&&p.Value!=ConstructionGraph.Disabled)||AutoUpgrade.Keys.Any(k=>!ValidRoleKey(k)))
            throw new InvalidDataException("Invalid Advanced Construction series data; original Matrix snapshot is retained.");
    }
    private bool ValidRoleKey(string key)=>key.Length>=34&&key[32]==':'&&Classes.ContainsKey(key[..32])&&int.TryParse(key[33..],out int role)&&role>=0&&key==TargetKey(key[..32],role);
}

public sealed record DesignFact(int DesignId, short EmpireId, short HullId, int Role, bool Obsolete,
    bool Buildable, long StarDate, int UpgradeDesignId, int HullSize = 0);

public sealed record ConstructionChange(int SourceDesignId, int TargetDesignId, bool Selected, bool WasIncoming = false);

public sealed record ConstructionPreview(bool Accepted, string? Error, ConstructionSnapshot? Next,
    IReadOnlyDictionary<int, int> UpgradeValues,IReadOnlyDictionary<int,bool>? ObsoleteValues=null);

public sealed record RetrofitResolution(int DesignId, string? Error)
{
    public static RetrofitResolution None(string error) => new(int.MinValue, error);
}
