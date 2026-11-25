using System.Reflection;
using System.Text.Json.Nodes;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Input;

internal static class CameraDialogChecks
{
    private static bool Measure(ref Vector2 __result) { __result = new Vector2(100, 20); return false; }
    private static readonly Sprite Icon = new();
    private static bool SampleIcon(ref Sprite __result) { __result = Icon; return false; }

    // Build the native General section. Display enumeration beyond it needs a real graphics presenter.
    private static IEnumerable<CodeInstruction> GeneralOnly(IEnumerable<CodeInstruction> code)
    {
        var add = AccessTools.Method(typeof(CollapsiblePanelList), nameof(CollapsiblePanelList.AddPanel));
        foreach(var op in code)
        {
            yield return op;
            if (op.Calls(add)) { yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ret); yield break; }
        }
    }

    internal static void Run(Galaxy galaxy, string logs)
    {
        string file = Path.Combine(Path.GetDirectoryName(logs)!, "launcher-data/ModSettings/mods_CommunityPatch.json");
        var pack = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CommunityPatch");
        var camera = pack.GetType("CommunityPatch.MapCamera", true)!;
        var hookOwner = "DW2.CommunityPatch.CameraSettings";
        var hooks = Harmony.GetAllPatchedMethods().SelectMany(target => {
            var info = Harmony.GetPatchInfo(target);
            return new[] { ("prefix", info.Prefixes), ("postfix",info.Postfixes) }
                .SelectMany(group=>group.Item2.Where(p=>p.owner==hookOwner)
                    .Select(p=>new {target=target.DeclaringType!.FullName+":"+target.Name+"("+string.Join(",",target.GetParameters().Select(x=>x.ParameterType.FullName))+")",
                        kind=group.Item1,effect=group.Item1=="prefix"?"changes-args":"observes",
                        why="CommunityPatch "+p.PatchMethod.DeclaringType!.FullName+":"+p.PatchMethod.Name}));
        }).ToArray();
        if(hooks.Length!=3)throw new Exception("Missing camera settings patches");
        File.WriteAllText(Path.Combine(logs,"camera-dialog-hooks.json"),System.Text.Json.JsonSerializer.Serialize(hooks));
        var runtime = AccessTools.Field(camera, "systemSpeed");
        var store = pack.GetType("CommunityPatch.CameraSettingsDialog",true)!;
        if (Path.GetFullPath((string)AccessTools.Field(store,"settingsFile").GetValue(null)!) != Path.GetFullPath(file))
            throw new Exception("Expected isolated launcher store");
        byte[] before = File.ReadAllBytes(file);
        var start = JsonNode.Parse(before)!.AsObject();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
        var mock = new Harmony("test.community-patch.settings-graphics");
        var font = (SpriteFont)Activator.CreateInstance(typeof(SpriteFont), true)!;
        GC.SuppressFinalize(font);
        AccessTools.Field(typeof(SpriteFont), "<Size>k__BackingField").SetValue(font, 16f);
        AccessTools.Field(typeof(SpriteFont), "DefaultLineSpacing").SetValue(font, 20f);
        foreach(var f in AccessTools.GetDeclaredFields(typeof(UserInterfaceHelper)).Where(f=>f.IsStatic && f.FieldType==typeof(SpriteFont))) f.SetValue(null,font);
        foreach (var m in AccessTools.GetDeclaredMethods(typeof(SpriteFont)).Where(m=>m.Name=="MeasureString" && m.ReturnType==typeof(Vector2)))
            mock.Patch(m,prefix:new HarmonyMethod(typeof(CameraDialogChecks),nameof(Measure)));
        UserInterfaceHelper.IconImages=new SpriteSheet();
        mock.Patch(typeof(SpriteSheet).GetProperty("Item", [typeof(string)])!.GetMethod!,prefix:new HarmonyMethod(typeof(CameraDialogChecks),nameof(SampleIcon)));
        UserInterfaceController.ScreenWidth=1280;
        UserInterfaceController.ScreenHeight=720;
        mock.Patch(AccessTools.Method(typeof(CollapsiblePanelList), "GenerateGameSettingPanels"),transpiler:new HarmonyMethod(typeof(CameraDialogChecks),nameof(GeneralOnly)));
        var dialog=new GameSettingsDialog();
        var input=new InputManager();
        int accepted=0,cancelled=0;
        void Open()
        {
            dialog.BindData("Settings",input,galaxy.Settings,(_,_)=>{},(_,_)=>{},(_,_)=>accepted++,(_,_)=>cancelled++,(_,_)=>{},(_,_)=>{},(_,_)=>{},(_,_)=>{},(_,_)=>{});
        }
        ValueBindingList Rows() => ((ValueBindingListPanel)dialog.Settings.GetPanelByName("CommunityPatch_Camera").Panel).Values;
        ValueBinding Row(string key) => Rows().Single(row=>row.ControlName=="CommunityPatch_Camera_"+key);
        string Value(string key) => (string)AccessTools.Property(Row(key).ValueObject.GetType(),"Text").GetValue(Row(key).ValueObject)!;
        void Select(string key,string choice)
        {
            var row=Row(key);
            var names=row.GenerateListValues(out _);
            int index=names.IndexOf(choice);
            if(index<0)throw new Exception(key+" missing "+choice);
            row.DropdownIndexSelected(index);
        }
        try
        {
            dialog.Initialize(FontSize.Normal,FontSize.Large,null!);
            dialog.SetSizeAndPosition(new Vector2(850,650),Vector2.Zero);
            Open();
            Check(dialog.Settings.Panels[1].Panel.Name=="CommunityPatch_Camera","Camera immediately follows General");
            Check((float)runtime.GetValue(null)! == float.Parse(start["SystemZoomSpeed"]!.ToString().TrimEnd('x'),System.Globalization.CultureInfo.InvariantCulture),"launcher system speed reaches renderer at startup");
            Check(Rows().Count==10 && Rows().All(x=>!string.IsNullOrWhiteSpace(x.Explanation)), "native Settings has preset and nine described choices");
            Select("CameraPreset","Galaxy 3x / System 2x");
            Check(Value("SystemZoomSpeed")=="2x" && Value("GalaxyZoomSpeed")=="3x", "preset changes the two visible speed controls");
            dialog.CancelButton.ClickEvent(dialog.CancelButton,new DWEventArgs(null!));
            Open();
            Check(cancelled==1 && before.SequenceEqual(File.ReadAllBytes(file)) && Value("SystemZoomSpeed")==start["SystemZoomSpeed"]!.ToString(),"Cancel discards edits");
            Select("CameraPreset","Galaxy 3x / System 2x");
            dialog.OkButton.ClickEvent(dialog.OkButton,new DWEventArgs(null!));
            var saved=JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Console.WriteLine($"Apply accepted={accepted}, stored={saved["SystemZoomSpeed"]}, runtime={runtime.GetValue(null)}, heading={dialog.Settings.GetPanelByName("CommunityPatch_Camera").HeaderText}");
            Check(accepted==1 && (float)runtime.GetValue(null)! ==2f && saved["SystemZoomSpeed"]!.ToString()=="2x","native Apply updates the live camera and launcher store");
            foreach(var (key,value) in start)
                if(key is not ("SystemZoomSpeed" or "GalaxyZoomSpeed")) Check(JsonNode.DeepEquals(value,saved[key]), "preset preserves "+key);
            saved["GalaxyZoomSpeed"]="1x";saved["unrelated-test-setting"]=true;
            File.WriteAllText(file,saved.ToJsonString());
            Open();
            Check(Value("GalaxyZoomSpeed")=="1x" && dialog.Settings.Panels.Count(p=>p.Panel.Name=="CommunityPatch_Camera")==1,"reopen reads manager edits and has one Camera section");
            Select("WheelZoomSmoothing","Off");
            Check(!((DWButton)Row("WheelInertiaCoast").Control).Enabled, "Off disables the coast control");
            Select("WheelZoomSmoothing","Inertia");
            Check(((DWButton)Row("WheelInertiaCoast").Control).Enabled, "Inertia enables the coast control");
            Select("SystemZoomSpeed","1x");
            File.WriteAllText(file,"broken");
            dialog.OkButton.ClickEvent(dialog.OkButton,new DWEventArgs(null!));
            Check(accepted==1 && File.ReadAllText(file)=="broken" && (float)runtime.GetValue(null)! == 2f,"invalid settings are retained and Apply can be retried");
            File.WriteAllText(file,saved.ToJsonString());
            dialog.OkButton.ClickEvent(dialog.OkButton,new DWEventArgs(null!));
            saved=JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Check((float)runtime.GetValue(null)! ==1f && saved["unrelated-test-setting"]!.GetValue<bool>(),"manual speed applies without losing unrelated fields");
        }
        finally { File.WriteAllBytes(file,before); mock.UnpatchAll(mock.Id); input.Dispose(); }
    }
}
