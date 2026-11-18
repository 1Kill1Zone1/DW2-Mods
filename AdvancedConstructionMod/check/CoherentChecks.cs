using AdvancedConstructionMod;
using DistantWorlds.Types;
using DW2ModLauncher.Loader;
using HarmonyLib;
using System.Runtime.Loader;

internal static partial class Checks
{
    private static void CheckCoherentMissions(Galaxy galaxy, Empire empire, string candidatePath)
    {
        var source = empire.Designs.First(d => !d.Obsolete && d.GetRole(galaxy) == ShipRole.Frigate && RetrofitRuntime.Fact(galaxy,empire,d).Buildable);
        var target = source.Clone(galaxy);
        target.DesignId = galaxy.Designs.GetNextId();
        galaxy.Designs.Add(target);
        empire.Designs.Add(target);
        string key = "design." + target.DesignId;
        setSaved(galaxy,"coherent-research","design." + source.DesignId,"Long");
        CoherentMissions.CopyAfterUpgrade(galaxy,source,target);
        Require(string.IsNullOrEmpty(getSaved(galaxy,"coherent-research",key)),"absent provider preserves normal upgrade");
        var coherent = AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
        AccessTools.Method(coherent.GetType("DW2CoherentResearch.MissionChannels",true),"Install").Invoke(null,null);
        var lookup = (Func<object,int,string>)LiveChannels.Channels["coherent-research.design-mission"];
        Require(lookup(galaxy,source.DesignId)=="Long" && lookup(galaxy,target.DesignId)==null,"registered CR reads the source mission only");
        Require(lookup(new object(),source.DesignId)==null && lookup(galaxy,int.MaxValue)==null,"invalid campaign and draft IDs return no mission");
        CoherentMissions.CopyAfterUpgrade(galaxy,source,target);
        Require(lookup(galaxy,target.DesignId)=="Long" && getSaved(galaxy,"coherent-research",key)=="Long","late provider copies a committed mission into real Matrix");
        var lines=coherent.GetType("DW2CoherentResearch.DesignLines",true)!;
        AccessTools.Method(lines,"Mark").Invoke(null,[target,"Picket"]);
        CoherentMissions.CopyAfterUpgrade(galaxy,source,target);
        CoherentMissions.CopyAfterUpgrade(galaxy,source,target);
        Require(lookup(galaxy,target.DesignId)=="Picket" && getSaved(galaxy,"coherent-research",key)=="Picket","explicit Picket wins over Long inheritance and repeated callbacks");
        Require(lookup(galaxy,source.DesignId)=="Long","new version leaves original mission intact");
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var current,out _),"read fixture series");
        var state=current.Clone();
        string series=ConstructionGraph.CreateClass(state,"Integration frigates");
        state.DesignClasses[source.DesignId]=series;
        Require(registry.TryCommit(empire,state,out _),"only one of the two frigates enters the series");
        var upgrade=new EditorSession(galaxy,empire,source.Clone(galaxy),source);
        Require(upgrade.InheritsSeries && !upgrade.SelectClass(null,out var hint) && hint==EditorSession.UpgradeSeriesHint,"Upgrade explains how to branch without moving source");
        var copyDraft=source.Clone(galaxy);
        copyDraft.DesignId=Constants.DesignDesignId;
        Require(!new EditorSession(galaxy,empire,copyDraft,null).InheritsSeries,"Copy as New has no inherited-series restriction");
        bool inspectBays=Environment.GetEnvironmentVariable("ACM_INSPECT_LONG_BAYS")=="1";
        string options=System.Text.Json.JsonSerializer.Serialize(new { ShipDesign=false, DesignLineages=false,EditMissionPresets=Environment.GetEnvironmentVariable("ACM_TEST_MISSION_PRESETS")=="1",LogToSessionFolder=inspectBays,
            OutputDirectory=Environment.GetEnvironmentVariable("DW2_TEST_OUTPUT_DIR") });
        coherent.GetType("DW2CoherentResearch.Entry",true)!.GetMethod("InitWithOptions")!.Invoke(null,[options,new Dictionary<string,string>()]);
        var block=coherent.GetType("DW2CoherentResearch.ShipDesignBlock",true)!;
        bool Applies()=> (bool)AccessTools.Method(block,"Applies").Invoke(null,[empire])!;
        Require(!Applies(),"global ship design rules off before manual preview");
        var edit=new EditorSession(galaxy,empire,source,null);
        Require(CoherentMissions.Choices(edit)?.ContainsKey("Picket")==true,"researched frigate offers Picket");
        var original=source.Components;
        var picket=CoherentMissions.Preview(edit,"Picket");
        var ranged=CoherentMissions.Preview(edit,"Long");
        Require(picket!=null&&ranged!=null&&picket.DesignId==source.DesignId&&picket.Name==source.Name,"editable saved design previews preserve identity and name");
        Require(picket!.Tactics.AttackRangeNormal==TacticsStance.Aggressive&&ranged!.Tactics.AttackRangeNormal==TacticsStance.Cautious,"real disabled-by-default generator produces chosen stances");
        int Pd(Design design)=>design.Components.Count(c=>!c.IsEmpty && c.Component.Category==ComponentCategory.WeaponIntercept);
        Console.WriteLine($"Manual preview, same frigate: Picket PD={Pd(picket)}, Long PD={Pd(ranged!)}");
        foreach(var variant in new[]{("Long",ranged!),("Picket",picket)})
        {
            var hull=variant.Item2.GetShipHull(galaxy);
            var bays=hull.ComponentBays.GetBaysByType(ComponentBayType.Weapon);
            var weapons=variant.Item2.Components.Where(c=>!c.IsEmpty&&bays.GetById(c.ComponentBayId)!=null);
            Console.WriteLine($"{variant.Item1} hull={hull.Name} id={hull.ShipHullId} weapon bays={bays.Count}: "+
                string.Join(", ",weapons.Select(c=>$"{c.Component.Name} ({c.Component.Category})")));
        }
        Require(Pd(picket)>0 && !picket.Components.Select(c=>c.ComponentId).SequenceEqual(ranged!.Components.Select(c=>c.ComponentId)),"Picket and Long produce different real loadouts");
        if(inspectBays)
        {
            foreach(var escort in empire.Designs.Where(d=>!d.Obsolete&&d.GetRole(galaxy)==ShipRole.Escort).DistinctBy(d=>d.HullId))
            {
                var session=new EditorSession(galaxy,empire,escort,null);
                var escortHull=escort.GetShipHull(galaxy);
                var weaponBays=escortHull.ComponentBays.GetBaysByType(ComponentBayType.Weapon);
                var limits=escortHull.GetValues(empire.Research,true);
                foreach(var mission in CoherentMissions.Choices(session)?.Keys.AsEnumerable()??Enumerable.Empty<string>())
                {
                    var variant=CoherentMissions.Preview(session,mission);
                    if(variant==null)continue;
                    var parts=variant.Components.Where(c=>!c.IsEmpty).ToArray();
                    var used=limits.Size+parts.Sum(c=>c.Component.Size);
                    Console.WriteLine($"Escort {escort.Name} hull={escort.HullId} mission={mission} size={used}/{limits.MaximumSize}: "+
                        string.Join("; ",weaponBays.Select(b=>$"bay {b.ComponentBayId} max {b.MaximumComponentSize}: "+
                        string.Join(", ",parts.Where(c=>c.ComponentBayId==b.ComponentBayId).Select(c=>$"{c.Component.Name} size {c.Component.Size}")))));
                }
            }
            var hull=ranged!.GetShipHull(galaxy);
            var bays=hull.ComponentBays.GetBaysByType(ComponentBayType.Weapon);
            var values=hull.GetValues(empire.Research,true);
            void Report(string name, Design design)
            {
                design=design.Clone(galaxy); design.Name="Bay audit "+name; design.DesignId=Constants.DesignDesignId;
                var parts=design.Components.Where(c=>!c.IsEmpty).ToArray();
                object?[] args=[galaxy,empire,design,false,false,null,null];
                var validator=AccessTools.GetDeclaredMethods(typeof(Design)).Single(m=>m.Name=="CheckDesignIsValid"&&m.GetParameters().Length==7);
                var valid=validator.Invoke(null,args);
                Console.WriteLine($"{name}: size={values.Size+parts.Sum(c=>c.Component.Size)}/{values.MaximumSize} summary={ShipSummary.DetermineShipSummary(galaxy,empire,hull,empire.Research,null,null,design.Components).Size} valid={valid} messages={string.Join(" | ",args.OfType<IEnumerable<string>>().SelectMany(s=>s))}");
                foreach(var bay in bays)
                    Console.WriteLine($"  bay={bay.ComponentBayId} limit={bay.MaximumComponentSize} -> "+string.Join(", ",parts.Where(c=>c.ComponentBayId==bay.ComponentBayId).Select(c=>$"{c.Component.Name} size={c.Component.Size}")));
            }
            Report("Long original",ranged);
            var policy=empire.Policy.WeaponLevelIntercept;
            try
            {
                empire.Policy.WeaponLevelIntercept=0;
                Report("Long PD policy zero",CoherentMissions.Preview(edit,"Long")!);
            }
            finally{empire.Policy.WeaponLevelIntercept=policy;}
            var missile=ranged.Components.First(c=>!c.IsEmpty&&c.Component.Category==ComponentCategory.WeaponStandoff);
            foreach(var bay in bays.Where(b=>!ranged.Components.Any(c=>!c.IsEmpty&&c.ComponentBayId==b.ComponentBayId&&c.Component.Category!=ComponentCategory.WeaponIntercept)))
            {
                foreach(bool keepPd in new[]{false,true})
                {
                    var clone=ranged.Clone(galaxy);
                    clone.Components=new ShipComponentList();
                    foreach(var c in ranged.Components)
                    {
                        if(c.ComponentBayId==bay.ComponentBayId||(!keepPd&&!c.IsEmpty&&c.Component.Category==ComponentCategory.WeaponIntercept))continue;
                        clone.Components.Add(c);
                    }
                    clone.Components.Add(new ShipComponent(missile.ComponentId,ShipComponent.StatusFullHealth,bay.ComponentBayId,missile.Level));
                    Report($"Manual third missile in {bay.ComponentBayId} keep other PD={keepPd} fits bay={missile.Component.Size<=bay.MaximumComponentSize}",clone);
                }
            }
        }
        Require(ReferenceEquals(source.Components,original)&&ReferenceEquals(galaxy.Designs.GetById(source.DesignId),source)&&lookup(galaxy,source.DesignId)=="Long","preview leaves registered design and saved mission intact");
        Require(!Applies()&&CoherentMissions.Preview(edit,"invalid")==null&&!Applies(),"manual override is scoped, including invalid requests");
        var nativeEditor=(DistantWorlds2.ShipDesignRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DistantWorlds2.ShipDesignRenderer));
        var editable=AccessTools.Field(nativeEditor.GetType(),"_DesignIsEditable");
        var foreign=AccessTools.Field(nativeEditor.GetType(),"_ForeignMode");
        bool CanEdit()=> (bool)AccessTools.Method(typeof(DesignerIntegration),"CanChangeWeapons").Invoke(null,[nativeEditor])!;
        editable.SetValue(nativeEditor,false);Require(!CanEdit(),"locked Edit cannot request a specialization");
        editable.SetValue(nativeEditor,true);Require(CanEdit(),"native editable design enables specialization");
        foreign.SetValue(nativeEditor,true);Require(!CanEdit(),"foreign component view cannot request a specialization");
        Entry.Init();
        UiChecks(galaxy,empire,source,target,manualMissions:true);
    }
}
