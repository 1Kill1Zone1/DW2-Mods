using System.Security.Cryptography;
using System.Text;
using DistantWorlds.Types;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private sealed class GenerationJob(Campaign campaign, Galaxy galaxy)
    {
        internal readonly string Stamp = GenerationGeometry(galaxy);
        internal readonly Waypoint[] Manual = campaign.Points;
        internal readonly GeneratedWaypoint[] Previous = campaign.Generated;
        internal readonly UsefulNetwork Search = CreateUsefulNetwork(galaxy);
    }
    private static string GenerationGeometry(Galaxy galaxy)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes,Encoding.UTF8,true);
        writer.Write(galaxy.SizeX); writer.Write(galaxy.SizeY); writer.Write(galaxy.GameStartSettings?.RandomSeed ?? 0);
        writer.Write(galaxy.Systems.Count);
        foreach(var star in galaxy.Systems.Values.OrderBy(s=>s.SystemId))
        {writer.Write(star.SystemId);writer.Write(star.GalaxyX);writer.Write(star.GalaxyY);}
        writer.Write(galaxy.NebulaClouds.Count(c=>c!=null));
        foreach(var cloud in galaxy.NebulaClouds.Where(c=>c!=null).OrderBy(c=>c.GalaxyX).ThenBy(c=>c.GalaxyY).ThenBy(c=>c.Radius))
        {
            writer.Write(cloud.GalaxyX);writer.Write(cloud.GalaxyY);writer.Write((double)cloud.Radius);writer.Write(cloud.Permeability);
            writer.Write(cloud.EffectGroups!=null);
            if(cloud.EffectGroups is {} effects)
            {
                effects.GetMaximumEffectAmountAll(galaxy,out float a,out float b,out float c,out float d,out float e,
                    out float f,out float g,out float h,out float i,out float j);
                foreach(float value in new[]{a,b,c,d,e,f,g,h,i,j})writer.Write(value);
            }
        }
        foreach(var profile in GenerationProfiles(galaxy))writer.Write(System.Text.Json.JsonSerializer.Serialize(profile));
        writer.Flush();return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    internal static GenerationProfile[] GenerationProfiles(Galaxy galaxy)
    {
        var result=new List<GenerationProfile>();
        foreach(var (driveId,fuelId,reactorId,reactors) in new[]{(65,85,146,1),(138,85,163,2),(155,103,163,1)})
        {
            var drive=galaxy.Components.GetByComponentId((short)driveId);var fuel=galaxy.Components.GetByComponentId((short)fuelId);var reactor=galaxy.Components.GetByComponentId((short)reactorId);
            var d=drive?.Values?.GetByLevel(0);var f=fuel?.Values?.GetByLevel(0);var r=reactor?.Values?.GetByLevel(0);
            if(d==null||f==null||r==null||d.HyperDriveSpeed<=0||f.FuelStorageCapacity<=0||r.ReactorFuelUnitsForFullCharge<=0)continue;
            var summary=new ShipSummary
            {
                FuelCapacity=2*f.FuelStorageCapacity,ReactorEnergyStorageCapacity=reactors*r.ReactorEnergyStorageCapacity,
                ReactorFuelUnitsForFullCharge=reactors*r.ReactorFuelUnitsForFullCharge,
                StaticEnergyConsumption=d.StaticEnergyUsed+2*f.StaticEnergyUsed+reactors*r.StaticEnergyUsed,
                HyperDriveSpeed=d.HyperDriveSpeed,HyperDriveEnergyUsage=d.HyperDriveEnergyUsage,
                HyperDriveJumpRange=d.HyperDriveJumpRange,HyperDriveJumpInitiationTime=d.HyperDriveJumpInitiationTime
            };
            if(reactors*r.ReactorEnergyOutputPerSecond<summary.StaticEnergyConsumption+summary.HyperDriveEnergyUsage)continue;
            double minimum=Math.Max(3200,2.5*Math.Max(800,d.HyperDriveJumpAccuracy==0?2000:d.HyperDriveJumpAccuracy));
            var capability=new RouteCapability(minimum,summary.HyperDriveJumpRange,summary.CalculateMaximumFuelRange(),summary.HyperDriveSpeed,Math.Max(0,summary.HyperDriveJumpInitiationTime));
            if(capability.Valid&&capability.PoweredRange>0)result.Add(new($"REF {drive!.Name} L0; 2x {fuel!.Name}; {reactors}x {reactor!.Name}",capability));
        }
        return result.ToArray();
    }
    internal static UsefulNetwork CreateUsefulNetwork(Galaxy galaxy)
    {
        if(galaxy.NebulaClouds.Any(c=>c!=null&&(!double.IsFinite(c.Radius)||c.Radius<0||!double.IsFinite(c.Permeability)||c.Permeability<=0)))
            throw new InvalidOperationException("Invalid nebula geometry or speed coefficients; existing network retained");
        var profiles=GenerationProfiles(galaxy);
        if(profiles.Length==0)throw new InvalidOperationException("No valid neutral early/mid-game reference definitions");
        var hazards=ReadStorms(galaxy);
        var systems=galaxy.Systems.Values.Where(s=>s?.Star!=null&&!Unsafe(hazards,new(s.GalaxyX,s.GalaxyY),25_000))
            .OrderBy(s=>s.SystemId).GroupBy(s=>new Waypoint(s.GalaxyX,s.GalaxyY)).Select(g=>g.First())
            .Select(s=>new RouteAnchor(s.SystemId,new(s.GalaxyX,s.GalaxyY))).ToArray();
        var clouds=galaxy.NebulaClouds.Where(c=>c!=null&&c.Radius>0).OrderBy(c=>c.GalaxyX).ThenBy(c=>c.GalaxyY)
            .Select(c=>new Storm(new(c.GalaxyX,c.GalaxyY),c.Radius)).ToArray();
        var groups=new List<List<Waypoint>>();
        bool Safe(Waypoint p) => Campaign.InBounds(galaxy,p)&&!Unsafe(clouds,p,25_000)&&
            galaxy.FindNearestSystem(p.X,p.Y,out long distance)!=null&&distance>=SystemMembershipDistanceSquared;
        foreach(var cloud in clouds)
        {
            var group=new List<Waypoint>();
            foreach(int angle in new[]{2,6,10,14,0,8,4,12,1,9,5,13,3,11,7,15})
            {
                double radius=cloud.Radius+25_001;
                var p=new Waypoint((int)Math.Round(cloud.Center.X+Math.Cos(angle*Math.PI/8)*radius),
                    (int)Math.Round(cloud.Center.Y+Math.Sin(angle*Math.PI/8)*radius));
                if(Safe(p))group.Add(p);
            }
            groups.Add(group);
        }
        // Clear interstellar gaps provide candidates even in a galaxy with no clouds.
        foreach(var star in systems)
        {
            var group=new List<Waypoint>();
            foreach(var other in systems.Where(s=>s.LocationId!=star.LocationId).OrderBy(s=>RouteChoice.Distance(star.Point,s.Point)).Take(3))
                foreach(double fraction in new[]{.5,1d/3,2d/3})
                {
                    var p=new Waypoint((int)Math.Round(star.Point.X+((double)other.Point.X-star.Point.X)*fraction),
                        (int)Math.Round(star.Point.Y+((double)other.Point.Y-star.Point.Y)*fraction));
                    if(Safe(p))group.Add(p);
                }
            groups.Add(group);
        }
        var candidates=new List<Waypoint>();var seen=new HashSet<Waypoint>();
        for(int round=0;round<16&&candidates.Count<UsefulNetwork.MaxCandidates;round++)
            foreach(var group in groups)
                if(round<group.Count&&seen.Add(group[round]))
                {candidates.Add(group[round]);if(candidates.Count==UsefulNetwork.MaxCandidates)break;}
        return new(systems,candidates.ToArray(),profiles,(a,b)=>LineCost(galaxy,a,b));
    }
    private static void BeginUsefulGeneration(Campaign campaign)
    {
        if(campaign.Generation is { Search.Complete:false } active){active.Search.Cancel();campaign.GenerationStatus=active.Search.Status;return;}
        campaign.Generation=new(campaign,campaign.Observer.Galaxy);campaign.GenerationStatus="Preparing verified corridors";
    }
    private static void AdvanceUsefulGeneration(Campaign campaign)
    {
        if(campaign.Generation is not {} job||job.Search.Complete)return;
        var galaxy=campaign.Observer.Galaxy;
        if(!ReferenceEquals(job.Manual,campaign.Points)||!ReferenceEquals(job.Previous,campaign.Generated))
        {job.Search.Cancel();campaign.GenerationStatus="Generation stale; network changed";return;}
        job.Search.Tick();campaign.GenerationStatus=job.Search.Status;
        if(!job.Search.Complete||job.Search.Cancelled||job.Search.Error!=null)return;
        if(job.Stamp!=GenerationGeometry(galaxy))
        {campaign.GenerationStatus="Generation stale; geometry or definitions changed";return;}
        if(job.Search.Result.Length==0)return;
        ApplyUsefulGeneration(campaign,job.Search,job.Stamp);
        RefreshGeneratedDiscoveries(galaxy,campaign,force:true);
    }
    private static void ApplyUsefulGeneration(Campaign campaign,UsefulNetwork search,string stamp)
    {
        if(!search.Complete||search.Cancelled||search.Error!=null||search.Proofs.Length!=search.Result.Length||stamp!=GenerationGeometry(campaign.Observer.Galaxy))
            throw new InvalidOperationException("Only a complete current verified network can be applied");
        var galaxy=campaign.Observer.Galaxy;
        var generated=search.Result.Select(point=>new GeneratedWaypoint(point,
            galaxy.FindNearestSystem(point.X,point.Y,out _).SystemId)).ToArray();
        campaign.ReplaceCertifiedGenerated(generated,search.Proofs.ToArray(),stamp);
        campaign.GenerationStatus=search.Status;
    }
    private static bool CurrentGenerationEvidence(Campaign campaign)
    {
        if(Environment.TickCount64>=campaign.NextGenerationValidation)
        {
            campaign.NextGenerationValidation=Environment.TickCount64+1000;
            campaign.GenerationCurrent=campaign.GenerationStamp.Length==64&&campaign.GenerationStamp==GenerationGeometry(campaign.Observer.Galaxy);
        }
        return campaign.GenerationCurrent;
    }
}
