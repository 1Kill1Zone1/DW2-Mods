using Stride.Core.Mathematics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using DistantWorlds.Types;
using DistantWorlds2;
using DistantWorlds.UI;
using HarmonyLib;
string root=args[0];
AssemblyLoadContext.Default.Resolving+=(_,n)=>{var p=Path.Combine(root,n.Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
Run(args);
[MethodImpl(MethodImplOptions.NoInlining)]
static void Run(string[] a){
 foreach(var m in new[]{typeof(Ship).Module,typeof(DWGame).Module})RuntimeHelpers.RunModuleConstructor(m.ModuleHandle);
 var host=(DWGame)RuntimeHelpers.GetUninitializedObject(typeof(DWGame));GC.SuppressFinalize(host);
 AccessTools.Field(typeof(Stride.Games.GameBase),"<Content>k__BackingField").SetValue(host,new Stride.Core.Serialization.Contents.ContentManager(new Stride.Core.IO.DatabaseFileProviderService(null)));
 var renderer=(ScaledRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ScaledRenderer));GC.SuppressFinalize(renderer);
  AccessTools.Field(typeof(DWGame),"_Renderer").SetValue(host,renderer);
 AccessTools.Field(typeof(ScaledRenderer),"_SceneRenderType").SetValue(renderer,SceneRenderType.System);
 AccessTools.Field(typeof(ScaledRenderer),"_GraphicsNodesSystem").SetValue(renderer,RuntimeHelpers.GetUninitializedObject(typeof(GraphicsNodeList)));
 AccessTools.Field(typeof(DWGame),"DWSettings").SetValue(host,new GameSettings());
 Galaxy.VersionStatic=DWGame.Version;Galaxy.Game=host;Galaxy.AllowNewTasks=false;Galaxy.LoadSensitiveRenderingPaused=true;
 using var stream=File.OpenRead(a[1]);using var reader=new BinaryReader(stream);
 var g=new Galaxy().ReadFromStream(reader,out var metadata,out var errors);if(errors.Count!=0)throw new Exception(string.Join(",",errors));
 Galaxy.ResearchProjectsStatic=g.ResearchProjects;Galaxy.PlanetaryFacilitiesStatic=g.PlanetaryFacilities;Galaxy.ShipHullsStatic=g.ShipHulls;Galaxy.ComponentsStatic=g.Components;
 var owner=g.Empires.GetPlayer();var station=g.Ships.GetById(264);
 var capture=AccessTools.Method(typeof(Ship),"ChangeEmpire",[typeof(short),typeof(bool),typeof(CharacterHandlingBehavior),typeof(float),typeof(bool)]);
 g.RefreshSystemArray();
 var builder=owner.Ships.First(s=>s.Role==ShipRole.ConstructionShip&&!s.IsDestroyed);
 capture.Invoke(station,[owner.EmpireId,false,default(CharacterHandlingBehavior),0f,true]);
 builder.SetGalaxy(g);station.SetGalaxy(g);station.ReDefine(g);builder.ReDefine(g);
 owner.Research.Update(g,owner,owner.GetDominantRace()!,owner.Policy,owner.IsPirate,false,out _);
 var destination=station.GetLocation(g)!;
 // Model a safe destination and a single available contractor.
 owner.DangerousSystems=new StarSystemList();
 builder.TransferLocation(g,destination);builder.GalaxyX=station.GalaxyX;builder.GalaxyY=station.GalaxyY;
 var orbit=(station.ParentId,station.ParentType,station.LocationId,station.GalaxyX,station.GalaxyY,station.ShipId);
 void Position(string stage)=>Console.WriteLine(stage+" orbit="+(station.ParentId,station.ParentType,station.LocationId,station.GalaxyX,station.GalaxyY,station.ShipId)+" docked="+station.ParentIsDockedAt+" onboard="+station.ParentIsOnboard); Position("before"); var designId=station.ResolveRetrofitDesignId(owner,out var target);
 if(target==null||target.DesignId==station.DesignId||!target.CheckEmpireHasResearchedAllTech(g,owner))throw new Exception("No candidate");
 var assembly=Assembly.LoadFrom(a[2]);var mod=assembly.GetType("CommunityPatch.SalvageOrders",true)!;
 var hermes=AccessTools.Method(mod,"Install");
 var store=Assembly.LoadFrom(Environment.GetEnvironmentVariable("DW2_TEST_MATRIX_DLL") ?? throw new InvalidOperationException("Set DW2_TEST_MATRIX_DLL for the fixture.")).GetType("DW2PositronicMatrix.SavedData",true)!;
 var matrixGet=AccessTools.Method(store,"Get").CreateDelegate<Func<object,string,string,string>>();
 var matrixSet=AccessTools.Method(store,"Set").CreateDelegate<Action<object,string,string,string>>();
 Func<object,string,string,string> read=(ctx,_,key)=>matrixGet(ctx,"CommunityPatch",key);
 Action<object,string,string,string> write=(ctx,_,key,value)=>matrixSet(ctx,"CommunityPatch",key,value);
 hermes.Invoke(null,[read,write]);
 object? Call(string n,params object[] x)=>AccessTools.Method(mod,n).Invoke(null,x);
 void Assert(bool pass,string msg){Console.WriteLine((pass?"PASS ":"FAIL ")+msg);if(!pass)throw new Exception(msg);}
 if(a.Length>3 && (a[3]=="ship" || a[3]=="cancelship")){
    var mobile=g.Ships.Where(x=>x!=null&&!x.IsBase&&!x.IsDestroyed&&x.Role==ShipRole.ExplorationShip && x.Components!=null && x.Components.Any(c=>!c.IsEmpty && c.Status>0 && !owner.Research.ResearchedComponents.ContainsId(c.ComponentId))).OrderByDescending(x=>x.Components.Count(c=>!c.IsEmpty&&!owner.Research.ResearchedComponents.ContainsId(c.ComponentId))).First();
    if(mobile.EmpireId!=owner.EmpireId)capture.Invoke(mobile,[owner.EmpireId,false,default(CharacterHandlingBehavior),0f,true]);
    var port=owner.Spaceports.First(x=>!x.IsDestroyed&&x.ConstructionSystem?.Yards.Count>0);
    foreach(var x in owner.Ships.Where(x=>x!=null&&!x.IsDestroyed)){x.SetGalaxy(g);x.ReDefine(g);x.UpdateBonuses();x.RegenerateSummary();}
    owner.ConstructionSystems=new();owner.ConstructionBasesPerSystem=new();
    var system=port.GetSystem(g)!;owner.ConstructionSystems.Add(system);owner.ConstructionBasesPerSystem[system.SystemId]=new(){port};
    mobile.Parent=default;mobile.ParentIsDockedAt=false;mobile.ParentIsOnboard=false;mobile.TransferLocation(g,port.GetLocation(g)!);mobile.SetPosition(port.GetPositionRaw(),port.GetLocation(g)!);mobile.GalaxyX=port.GalaxyX;mobile.GalaxyY=port.GalaxyY;
    var opts=GameTask.GenerateTaskRetrofitShip(g,owner,mobile,true,false,false);
    Console.WriteLine("ship menu="+string.Join("; ",opts.Select(t=>$"{t.Type}/{t.Enabled}/{t.DisabledReason}/{string.Join(",",t.Values??new())}/{string.Join(",",t.Entities??new())}")));
    var option=opts.First(t=>t.Type==GameTaskType.RetrofitShip&&t.Enabled&&owner.Designs.GetById(t.Values[0]).CheckEmpireHasResearchedAllTech(g,owner));
    int dest=option.Values[0];var original=mobile.DesignId; var science=owner.Research.Projects.ToDictionary(p=>p.ResearchProjectId,p=>p.Progress);
    Assert((bool)Call("Start",g,owner,mobile,dest)!,"ship order");
    g.Time=g.Time.AddSeconds(1);Call("Review",owner,g,g.Time);
    Console.WriteLine("native mission="+mobile.Mission?.Type+" target="+mobile.Mission?.PrimaryTarget+" extra="+mobile.Mission?.ExtraId);
    if(a[3]=="cancelship"){
        Console.WriteLine("design field="+mobile.Mission?.BuildDesignId+" expected="+dest);
        Assert((bool)Call("Cancel",g,owner,mobile)! && mobile.Mission?.Type!=ShipMissionType.Retrofit,"cancel ship before yard");
        Assert(owner.Research.Projects.All(p=>p.Progress==science[p.ResearchProjectId]),"no research for canceled approach");
        return;
    }
    var yardSite=mobile.Mission?.PrimaryTarget.ResolveBase<StellarObject>(g) ?? port;
    var cs=yardSite.GetConstructionSystem()!;mobile.TransferLocation(g,yardSite.GetLocation(g)!);mobile.Parent=new Item(yardSite);mobile.ParentIsDockedAt=true;
    AccessTools.Method(typeof(Ship),"CommandRetrofit").Invoke(mobile,[owner,g.Time,0.1f,yardSite.GetLocation(g)!,dest]);
    for(int j=0;j<2400;j++){g.Time=g.Time.AddSeconds(1);cs.DoTasks(1f,g.Time,g,owner,yardSite.GetLocation(g)!,false);if(mobile.DesignId==dest && mobile.Components.All(c=>c.IsEmpty||c.Status==ShipComponent.StatusFullHealth))break;}
    Console.WriteLine("ship result "+mobile.DesignId+"/"+dest+" orders="+read(g,"","salvage.orders.v1"));
    Assert(mobile.DesignId==dest && !mobile.IsDestroyed && mobile.Components.All(c=>c.IsEmpty||c.Status==ShipComponent.StatusFullHealth),"ship completed native refit");
    Assert(owner.Research.Projects.Any(p=>p.Progress>science[p.ResearchProjectId]),"ship awarded research");
    Assert(owner.Messages.Count(m=>m.Title?.StartsWith("Salvage research: ")==true)==1,"one ship report");
    return;
 }
 var menu=GameTask.GenerateTaskRetrofitShip(g,owner,station,true,false,false);
 Assert(menu.Any(t=>t.Enabled && t.Values.Contains(designId)),"eligible native project offered");
 var candidate=owner.Ships.First(s=>s.Role==ShipRole.ConstructionShip&&!s.IsDestroyed);
 candidate.Mission=null!;candidate.MissionAssignmentType=ShipMissionAssignmentType.FullyAutomate;
 candidate.TransferLocation(g,station.GetLocation(g)!);candidate.SetPosition(station.GetPositionRaw(),station.GetLocation(g)!);
 Console.WriteLine("builder="+candidate.ShipId+" near="+(Vector3.Distance(candidate.GetPositionRaw(),station.GetPositionRaw()))+" speed="+candidate.Summary.ConstructionBuildSpeed+" yards="+string.Join(",",candidate.ConstructionSystem.Yards.Select(y=>y.ShipUnderConstructionId)));
 var initial=owner.Research.Projects.ToDictionary(p=>p.ResearchProjectId,p=>p.Progress);
 if(a[3]=="edges"){
    var ai=g.Empires.First(e=>e!=null && !e.IsPlayer && e.EmpireId!=owner.EmpireId);
    Assert(!(bool)Call("Start",g,ai,station,-1)!,"AI cannot submit a salvage order");
    var panel=assembly.GetType("CommunityPatch.SalvagePanel",true)!;
    var find=AccessTools.Method(panel,"FindSlot");
    GameTask[] row=[new(),new(),new(),new(){Type=GameTaskType.ScrapShip},null!,null!];
    GameTaskList[] lists=[new(),new(),new(),new(),new(),new()];
    Assert((int)find.Invoke(null,[row,lists,6])! ==4,"empty slot next to scuttle");
    lists[4].Add(new GameTask());Assert((int)find.Invoke(null,[row,lists,6])! ==5,"does not override native submenu");
    row[5]=new();Assert((int)find.Invoke(null,[row,lists,6])! ==-1,"preserves a full panel");
    foreach(var b in owner.ConstructionShips)b.MissionAssignmentType=ShipMissionAssignmentType.Manual;
    Assert((bool)Call("Start",g,owner,station,-1)!,"queue full order");
    var now=g.Time;void Tick(){now=now.AddSeconds(1);Call("Review",owner,g,now);}
    var initialCount=station.Components.Count;
    for(int j=0;j<10;j++)Tick();
    Assert(station.Components.Count==initialCount && (bool)Call("HasOrder",g,station)!,"manual builders left alone");
    candidate.MissionAssignmentType=ShipMissionAssignmentType.FullyAutomate;
    var location=station.GetLocation(g)!;
    candidate.SetPositionRaw(station.GetPositionRaw()+new Vector3(200000,0,0));
    Tick();
    Assert(candidate.Mission?.Type==ShipMissionType.Move && candidate.Mission.PrimaryTarget.Id==station.ShipId && station.Components.Count==initialCount,"automatic builder approaches without remote work");
    Assert((bool)Call("Cancel",g,owner,station)! && candidate.Mission?.Type!=ShipMissionType.Move,"cancel clears only our approach");
    candidate.MissionAssignmentType=ShipMissionAssignmentType.FullyAutomate;
    candidate.Mission=null!;candidate.TransferLocation(g,location);candidate.SetPositionRaw(station.GetPositionRaw());
    Assert((bool)Call("Start",g,owner,station,-1)!,"restart canceled order");
    for(int j=0;j<300 && !owner.Research.Projects.Any(p=>p.Progress>initial[p.ResearchProjectId]);j++)Tick();
    Console.WriteLine($"after wait: parts={station.Components.Count}/{initialCount}, job={read(g,"","salvage.orders.v1")}, mission={candidate.Mission?.Type}, assignment={candidate.MissionAssignmentType}, distance={Vector3.Distance(candidate.GetPositionRaw(),station.GetPositionRaw())}");
    Assert(station.Components.Count<initialCount && !station.IsDestroyed,"partial dismantle");
    var earned=owner.Research.Projects.ToDictionary(p=>p.ResearchProjectId,p=>p.Progress);
    var removed=read(g,"","salvage.repair."+station.ShipId);var ticket=read(g,"","salvage.orders.v1");
    int sid=station.ShipId,bid=candidate.ShipId;
    using(var mem=new MemoryStream()){
       using(var bw=new BinaryWriter(mem,System.Text.Encoding.UTF8,true))g.WriteToStream(bw,metadata);
       mem.Position=0;using var rr=new BinaryReader(mem);g=new Galaxy().ReadFromStream(rr,out metadata,out var errs);
       Assert(errs.Count==0 && read(g,"","salvage.orders.v1")==ticket && read(g,"","salvage.repair."+sid)==removed,"native save roundtrip keeps order and bays");
    }
    Galaxy.ResearchProjectsStatic=g.ResearchProjects;Galaxy.PlanetaryFacilitiesStatic=g.PlanetaryFacilities;Galaxy.ShipHullsStatic=g.ShipHulls;Galaxy.ComponentsStatic=g.Components;g.RefreshSystemArray();
    owner=g.Empires.GetPlayer();station=g.Ships.GetById(sid);candidate=g.Ships.GetById(bid);
    foreach(var b in new[]{station,candidate}){b.SetGalaxy(g);b.ReDefine(g);b.UpdateBonuses();b.RegenerateSummary();}
    owner.Research.Update(g,owner,owner.GetDominantRace()!,owner.Policy,owner.IsPirate,false,out _);
    Assert((bool)Call("HasOrder",g,station)! && (bool)Call("Cancel",g,owner,station)!,"cancel resumed order");
    Assert(owner.Research.Projects.All(p=>earned[p.ResearchProjectId]==p.Progress),"earned science retained");
    var before=station.Components.Count;
    Assert((bool)Call("Start",g,owner,station,-2)!,"repair offered after partial dismantle");
    for(int j=0;j<500 && (bool)Call("HasOrder",g,station)!;j++)Tick();
    Assert(!(bool)Call("HasOrder",g,station)! && station.Components.Count>before,"known replacements installed");
    Assert(owner.Research.Projects.All(p=>earned[p.ResearchProjectId]==p.Progress),"repair awards no research");
    Assert((bool)Call("Start",g,owner,station,-1)!,"remaining unknown parts can be dismantled");
    capture.Invoke(station,[ai.EmpireId,false,default(CharacterHandlingBehavior),0f,true]);Tick();
    Assert(!(bool)Call("HasOrder",g,station)! && !station.IsDestroyed,"ownership loss stops work");
    return;
 }
 if(a[3]=="panel"){
    var observed=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("DW2_TEST_OBSERVED_HOOKS") ?? throw new InvalidOperationException("Set DW2_TEST_OBSERVED_HOOKS to your test profile hook report."))).RootElement;
    var native=AccessTools.GetDeclaredMethods(typeof(UserInterfaceHelper)).Single(m=>m.Name=="SetActionButtons"&&m.GetParameters().Length==10);
    var patches=observed.GetProperty("hooks").EnumerateArray().Single(h=>h.GetProperty("target").GetString()!.Contains("UserInterfaceHelper:SetActionButtons(")).GetProperty("patches");
    foreach(var h in patches.EnumerateArray()){
        var id=h.GetProperty("owner").GetString()!.Split(':')[1];
        var content=Path.GetFullPath(Path.Combine(a[0],"../../workshop/content/1531540",id));
        var asm=Assembly.LoadFrom(Directory.GetFiles(content,"*.dll").Single());
        var signature=h.GetProperty("method").GetString()!.Split('(')[0].Split(':');
        var callback=new HarmonyMethod(AccessTools.Method(asm.GetType(signature[0],true)!,signature[1])){
          priority=h.GetProperty("priority").GetInt32(),before=h.GetProperty("before").Deserialize<string[]>(),after=h.GetProperty("after").Deserialize<string[]>()};
        var kind=h.GetProperty("kind").GetString();
        new Harmony(h.GetProperty("harmonyId").GetString()).Patch(native,prefix:kind=="prefix"?callback:null,postfix:kind=="postfix"?callback:null);
    }
    object?[] ctx=[station,null,g,owner,g.StarDate,default(GameTaskType),0,0,null,false];
    var row=(GameTask[])native.Invoke(null,ctx)!;var menus=(GameTaskList[])ctx[8]!;
    var panel=assembly.GetType("CommunityPatch.SalvagePanel",true)!;
    int i=(int)AccessTools.Method(panel,"FindSlot").Invoke(null,[row,menus,row.Length])!;
    Assert(i>0 && row[i-1]?.Type==GameTaskType.ScrapShip,"native action row with current profile hooks");
    RuntimeHelpers.RunModuleConstructor(typeof(EmpireSectionList).Module.ModuleHandle);
    var graphics=new Harmony("test.salvage.ui");
    var font=(Stride.Graphics.SpriteFont)Activator.CreateInstance(typeof(Stride.Graphics.SpriteFont),true)!;GC.SuppressFinalize(font);
    AccessTools.Field(typeof(Stride.Graphics.SpriteFont),"<Size>k__BackingField").SetValue(font,16f);
    AccessTools.Field(typeof(Stride.Graphics.SpriteFont),"DefaultLineSpacing").SetValue(font,20f);
    foreach(var f in AccessTools.GetDeclaredFields(typeof(UserInterfaceHelper)).Where(f=>f.IsStatic && f.FieldType==typeof(Stride.Graphics.SpriteFont)))f.SetValue(null,font);
    foreach(var m in AccessTools.GetDeclaredMethods(typeof(Stride.Graphics.SpriteFont)).Where(m=>m.Name=="MeasureString" && m.ReturnType==typeof(Vector2)))graphics.Patch(m,prefix:new HarmonyMethod(typeof(MockGraphics),"Measure"));
    UserInterfaceHelper.IconImages=new Stride.Graphics.SpriteSheet();
    graphics.Patch(typeof(Stride.Graphics.SpriteSheet).GetProperty("Item",[typeof(string)])!.GetMethod!,prefix:new HarmonyMethod(typeof(MockGraphics),"Icon"));
    UserInterfaceController.ScreenWidth=1280;UserInterfaceController.ScreenHeight=720;
    using var device=Stride.Graphics.GraphicsDevice.New(Stride.Graphics.DeviceCreationFlags.None,Stride.Graphics.GraphicsProfile.Level_11_0);
    AccessTools.PropertySetter(typeof(Stride.Games.GameBase),"GraphicsDevice").Invoke(host,[device]);
    var controls=new DWPanel();var buttons=new DWButton[row.Length];
    for(int n=0;n<buttons.Length;n++){buttons[n]=new DWButton();buttons[n].SetSizeAndPosition(new(30,30),new(n*30,0));controls.AddControl(buttons[n]);}
    AccessTools.Method(panel,"Bind").Invoke(null,[g,owner,station,row,menus,buttons]);
    var extra=controls.Controls.OfType<DWButton>().Single(b=>b.Name=="CommunityPatch.Salvage");
    Assert(extra.Visible && extra.Text=="" && extra.Image!=null && extra.Position==buttons[i].Position,"module icon in native action row");
    var texture=extra.Image!;
    var pixels=texture.GetData<Color>(new Stride.Graphics.GraphicsContext(device).CommandList);
    Console.WriteLine($"Module texture {texture.Width}x{texture.Height} {texture.ViewFormat}: {pixels[0]}, {pixels[10*64+32]}, {pixels[42*64+8]}");
    if(texture.ViewFormat is Stride.Graphics.PixelFormat.B8G8R8A8_UNorm or Stride.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb)
        pixels=Array.ConvertAll(pixels,c=>new Color(c.B,c.G,c.R,c.A));
    Assert(texture.Width==64 && texture.Height==64 && pixels[0].A==0 &&
        pixels.Any(p=>p.A==255 && p.R>200 && p.B>210) && pixels.Any(p=>p.A==255 && p.R<145 && p.B>200),
        "GPU texture contains the silver cartridge and blue socket on transparency");
    Assert((bool)Call("Start",g,owner,station,-1)!,"create order on fixture");
    AccessTools.Method(panel,"Bind").Invoke(null,[g,owner,station,row,menus,buttons]);
    Assert(extra.Visible && extra.Image==null && extra.Text=="…" && extra.ShowDropDownSelector,"active order indicator and menu");
    Assert((bool)Call("Cancel",g,owner,station)!,"cancel fixture order");
    AccessTools.Method(panel,"Bind").Invoke(null,[g,owner,station,row,menus,buttons]);
    Assert(ReferenceEquals(extra.Image,texture) && extra.Text=="","idle icon restored from GPU cache");
    AccessTools.Method(panel,"Bind").Invoke(null,[g,owner,new object(),row,menus,buttons]);
    Assert(!extra.Visible,"button cleared on selection change");return;
 }
 if(a[3]=="hooks"){
   string Signature(MethodBase m)=>m.DeclaringType?.FullName+":"+m.Name+"("+string.Join(",",m.GetParameters().Select(x=>x.ParameterType.FullName))+")";
   var hooks=new List<object>();
   foreach(var m in Harmony.GetAllPatchedMethods()){
      var info=Harmony.GetPatchInfo(m)!;
      foreach(var (kind,rows) in new[]{("prefix",info.Prefixes),("postfix",info.Postfixes),("finalizer",info.Finalizers)})
        foreach(var h in rows.Where(x=>x.owner=="local.dw2.community-patch.salvage"))
          hooks.Add(new{target=Signature(m),kind,effect=kind=="prefix" && h.PatchMethod.ReturnType==typeof(bool)?"skips-original":"observes",why="CommunityPatch "+Signature(h.PatchMethod)});
   }
   File.WriteAllText("salvage-hooks.json",JsonSerializer.Serialize(hooks,new JsonSerializerOptions{WriteIndented=true}));
   Assert(hooks.Count>=7,"observed salvage hooks");return;
 }
 if(a.Length>3&&a[3]=="full"){
   Assert((bool)Call("Start",g,owner,station,-1)!,"full station order");
 }else{
   Assert(!(bool)Call("Start",g,owner,station,-9000+10000)!,"unrelated design rejected");
   Assert((bool)Call("Start",g,owner,station,designId)!,"combined order accepted");
 }
 Assert(!(bool)Call("Start",g,owner,station,designId)!,"duplicate rejected");
 var t0=g.Time;
 for(int i=1;i<2000&&!station.IsDestroyed;i++){
    var t=t0.AddSeconds(i);
    AccessTools.Property(typeof(Galaxy),"Time")?.GetSetMethod(true)?.Invoke(g,[t]);
    Call("Review",owner,g,t);
    station.RetrofitConstructionSystem?.DoTasks(1f,t,g,owner,station.GetLocation(g)!,false);
    if(station.DesignId==designId && !station.IsUnderConstructionOrRepair && station.Components.All(c=>c.IsEmpty||c.Status==ShipComponent.StatusFullHealth))break;
 }
 Console.WriteLine("end orders="+read(g,"","salvage.orders.v1")+" design="+station.DesignId+" native-target="+designId+" messages="+owner.Messages.Count(m=>m.Title?.StartsWith("Salvage research: ")==true));
 if(a.Length>3&&a[3]=="full"){Assert(station.IsDestroyed&&!candidate.IsDestroyed,"complete station-only dismantle");}
 else{Assert(station.DesignId==designId&&!station.IsDestroyed,"native retrofit completed");Assert(orbit==(station.ParentId,station.ParentType,station.LocationId,station.GalaxyX,station.GalaxyY,station.ShipId),"original orbital body retained");}
 Assert(owner.Research.Projects.Any(p=>p.Progress>initial[p.ResearchProjectId]),"actual native science recorded");
 Assert(owner.Messages.Count(m=>m.Title?.StartsWith("Salvage research: ")==true)==1,"one salvage report");
}


internal static class MockGraphics
{
    private static bool Measure(ref Vector2 __result){__result=new(100,20);return false;}
    private static readonly Stride.Graphics.Sprite Symbol=new();
    private static bool Icon(ref Stride.Graphics.Sprite __result){__result=Symbol;return false;}
}
