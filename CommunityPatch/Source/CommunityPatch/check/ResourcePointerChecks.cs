using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

internal static class ResourcePointerChecks
{
    private static readonly List<(Vector2 Start, Vector2 End)> Lines = [];

    internal static void Run(Galaxy galaxy, string game, string logs)
    {
        RuntimeHelpers.RunModuleConstructor(typeof(DWGame).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(EmpireSectionList).Module.ModuleHandle);
        string textPath = Path.Combine(game, "data", "GameText.txt");
        var text = new Dictionary<string, string>();
        using (var stream = File.OpenRead(textPath))
            AccessTools.Method(typeof(TextResolver), "ParseStream").Invoke(null, [textPath, stream, text]);
        AccessTools.Field(typeof(TextResolver), "_Text").SetValue(null, text);
        var preferences = new Harmony("test.community-patch.headless-preferences");
        preferences.Patch(AccessTools.Method(typeof(GameSettings), "Clone"),
            prefix: new HarmonyMethod(typeof(ResourcePointerChecks), nameof(CloneForHarness)));
        try { galaxy.ProcessPostLoad(galaxy.Settings); }
        finally { preferences.UnpatchAll(preferences.Id); }
        var player = galaxy.Empires.GetPlayer();
        var pack = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CommunityPatch");
        var pointers = pack.GetType("CommunityPatch.ResourceSourcePointers", true)!;
        const string owner = "DW2.CommunityPatch.ResourceSourcePointers";
        var row = typeof(Resource).GetInterfaceMap(typeof(IListDrawable)).TargetMethods.Single(m => m.Name.EndsWith(".DrawItem", StringComparison.Ordinal));
        var callouts = AccessTools.Method(typeof(DWRendererBase), "DrawMapCallouts");
        Require(Harmony.GetPatchInfo(row)?.Transpilers.Any(p => p.owner == owner) == true, "resource row is patched by the loaded pack");
        Require(Harmony.GetPatchInfo(callouts)?.Postfixes.Any(p => p.owner == owner) == true, "map renderer is patched by the loaded pack");
        var sources = AccessTools.Method(pointers, "FindSources");
        IReadOnlyList<Orb> Targets(short id) => (IReadOnlyList<Orb>)sources.Invoke(null, [player, id])!;

        var emeros = galaxy.Resources.Single(r => r.Name == "Emeros Crystal");
        var nekros = galaxy.Resources.Single(r => r.Name == "Nekros Stone");
        var body = galaxy.Orbs.GetWithResource(emeros.ResourceId, player).Single();
        var system = body.GetSystem(galaxy, false);
        Require(system.Name == "Rhiannon" && ReferenceEquals(body, galaxy.Orbs.GetWithResource(nekros.ResourceId, player).Single()),
            $"both native sources are on {body.Name} ({body.Category}) in Rhiannon");
        var levels = player.ExplorationMap.GetMapDirectly();
        Require(levels[body.OrbId] == 0 && levels[system.Star.OrbId] > 0, "star is discovered and source body is unexplored");
        var zero = galaxy.Resources.First(r => player.ResourcesKnown.GetAmount(r.ResourceId) == 0 &&
            Resource.CountHasResourceCached(player, r.ResourceId) == 0 &&
            Resource.CountHasMiningResourceCached(player, r.ResourceId, galaxy) == 0 && Targets(r.ResourceId).Count == 0);

        var gpu = new Harmony("test.community-patch.resource-pointers");
        var font = (SpriteFont)Activator.CreateInstance(typeof(SpriteFont), true)!;
        GC.SuppressFinalize(font);
        AccessTools.Field(typeof(SpriteFont), "<Size>k__BackingField").SetValue(font, 16f);
        AccessTools.Field(typeof(SpriteFont), "DefaultLineSpacing").SetValue(font, 20f);
        var icons = new SpriteSheet();
        icons.Sprites.Add(new Sprite { Name = "Money" });
        AccessTools.Field(typeof(UserInterfaceHelper), "IconImages").SetValue(null, icons);
        void Stub(System.Reflection.MethodBase m, string callback)
            => gpu.Patch(m, prefix: new HarmonyMethod(typeof(ResourcePointerChecks), callback));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(SpriteFont)).Where(m => m.Name == "MeasureString" && m.ReturnType == typeof(Vector2)))
            Stub(method, nameof(Measure));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(DrawingHelper)).Where(m =>
            m.Name is "DrawStringDropShadow" or "DrawSpriteUndistorted" or "DrawTextureUndistorted"))
            Stub(method, method.GetParameters().Last().ParameterType == typeof(Vector2).MakeByRefType() &&
                method.GetParameters().Last().Name == "size" ? nameof(MeasureDrawnText) : nameof(NoGpu));
        var drawText = AccessTools.Method(typeof(DrawingHelper), "DrawTextLeftCenterRight",
            [typeof(SpriteBatch), typeof(string[]), typeof(SpriteFont[]), typeof(float[]), typeof(Color[]), typeof(Color),
                typeof(float), typeof(float), typeof(float), typeof(float), typeof(string[]), typeof(string[]), typeof(HoverTipList).MakeByRefType()]);
        Stub(drawText, nameof(CaptureCounter));
        Stub(AccessTools.Method(typeof(DrawingHelper), "DrawLine",
            [typeof(SpriteBatch), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Color), typeof(float)]), nameof(CaptureLine));

        UserInterfaceController.ScreenWidth = 1280;
        UserInterfaceController.ScreenHeight = 720;
        var uiList = new ScrollableList { Visible = true };
        uiList.SetSizeAndPosition(new(420, 300), new(8, 75));
        var panel = (EmpireSectionList)RuntimeHelpers.GetUninitializedObject(typeof(EmpireSectionList));
        var section = (Section)RuntimeHelpers.GetUninitializedObject(typeof(Section));
        var sub = (SubSection)RuntimeHelpers.GetUninitializedObject(typeof(SubSection));
        panel.ActiveSection = section;
        section.ActiveSubSection = sub;
        sub.ListItems = uiList;
        UserInterfaceController.EmpireSectionList = panel;
        UserInterfaceController.HoveredControl = uiList;
        var renderer = (CustomOrbRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CustomOrbRenderer));
        AccessTools.Field(typeof(DWRendererBase), "_PlayerEmpire").SetValue(renderer, player);
        AccessTools.Field(typeof(DWRendererBase), "_Galaxy").SetValue(renderer, galaxy);
        AccessTools.Field(typeof(DWRendererBase), "Viewport").SetValue(null, new Viewport(0, 0, 1280, 720));
        var mouse = AccessTools.Field(typeof(DWRendererBase), "_MousePoint");
        var camera = new CameraInfo { View = Matrix.Identity, Projection = Matrix.Scaling(.001f) };

        void Row(Resource r, int count, int lineCount, bool hover = true)
        {
            LastCounter = null;
            Lines.Clear();
            object?[] args = [null, null, (DrawDetailType)16, new Point(395, 103), new Vector2(16, 90), 400f, 4f,
                font, font, font, font, Color.White, Color.Black, 1f, 1f, 1f, new ListDrawableFieldList(),
                galaxy, player, null, new HoverTipList(), null, null, false, false, false];
            row.Invoke(r, args);
            Require(LastCounter == $"0 of {count} sources", $"{r.Name} native row renders {LastCounter}; expected 0 of {count}");
            uiList.HoveredItem = r;
            uiList.HoverTips = (HoverTipList)args[20]!;
            var tip = uiList.HoverTips.Single(t => t.Text?.Contains(TextResolver.GetText("Known Locations Explanation"), StringComparison.Ordinal) == true);
            var region = tip.Rectangle;
            var point = hover ? new Point((int)(region.Left + region.Width / 2), (int)(region.Top + region.Height / 2)) : new Point(20, 300);
            mouse.SetValue(null, point);
            callouts.Invoke(renderer, [null, camera, body.GalaxyX, body.GalaxyY, 1f]);
            Require(Lines.Count == lineCount, $"{r.Name} has {Lines.Count / 3} drawn arrows (hover={hover})");
        }

        Require(player.ResourcesKnown.GetAmount(emeros.ResourceId) == 1 && player.ResourcesKnown.GetAmount(nekros.ResourceId) == 1,
            "raw economy counts are both one before drilling into survey knowledge");
        Row(emeros, 0, 0);
        Row(nekros, 0, 0);
        Row(zero, 0, 0);

        levels[body.OrbId] = byte.MaxValue;
        Thread.Sleep(1100);
        Require(Targets(emeros.ResourceId).SequenceEqual(new[] { body }) && Targets(nekros.ResourceId).SequenceEqual(new[] { body }),
            "surveyed resources target the actual body with no unrelated targets");
        Row(emeros, 1, 3);
        Row(nekros, 1, 3);
        Row(zero, 0, 0);
        Row(emeros, 1, 0, hover: false);
        Require(player.ResourcesKnown.GetAmount(emeros.ResourceId) == 1 && player.ResourcesKnown.GetAmount(nekros.ResourceId) == 1,
            "display changes leave the economy's raw known-source data untouched");
        var ownedHooks = Harmony.GetAllPatchedMethods()
            .SelectMany(m => new[] { ("transpiler", Harmony.GetPatchInfo(m).Transpilers), ("postfix", Harmony.GetPatchInfo(m).Postfixes) }
                .SelectMany(kind => kind.Item2.Where(p => p.owner == owner).Select(p => new {
                    target = m.DeclaringType!.FullName + ":" + m.Name + "(" + string.Join(",", m.GetParameters().Select(parameter => parameter.ParameterType.FullName)) + ")",
                    kind = kind.Item1, effect = kind.Item1 == "transpiler" ? "rewrites" : "observes",
                    why = "CommunityPatch " + p.PatchMethod.DeclaringType!.FullName + ":" + p.PatchMethod.Name + "(" +
                        string.Join(",", p.PatchMethod.GetParameters().Select(parameter => parameter.ParameterType.FullName)) + ")",
                }))).ToArray();
        File.WriteAllText(Path.Combine(logs, "resource-pointer-hooks.json"), System.Text.Json.JsonSerializer.Serialize(ownedHooks));
    }

    private static string? LastCounter;
    private static void CaptureCounter(string[] text)
    {
        LastCounter = text[2];
    }
    private static bool NoGpu() => false;
    private static bool MeasureDrawnText(out Vector2 size) { size = new Vector2(96, 20); return false; }
    private static bool CloneForHarness(GameSettings __instance, ref GameSettings __result)
    {
        __result = (GameSettings)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(__instance, null)!;
        return false;
    }
    private static bool Measure(ref Vector2 __result) { __result = new Vector2(96, 20); return false; }
    private static bool CaptureLine(float __1, float __2, float __3, float __4)
    { Lines.Add((new Vector2(__1, __2), new Vector2(__3, __4))); return false; }
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
