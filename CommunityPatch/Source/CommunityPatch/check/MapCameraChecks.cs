using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Games;

internal static class MapCameraChecks
{
    private static (int X, int Y, float Height)? view;
    private static (int X, int Y, float Height)? galaxyView;
    private static StellarObject? panned;
    private static float? zoom;
    private static int actions;
    private static bool refuse;
    private static object? selectedItem;
    private static DWRendererBase? renderLeaf;
    private static readonly CameraInfo Camera = new();
    private static readonly GraphicsNodeList Nodes = (GraphicsNodeList)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsNodeList));

    internal static void Run(Galaxy galaxy, string logs, bool requireExplorerHotkey = false, bool replayMapNotice = false)
    {
        const string owner = "DW2.CommunityPatch.MapCamera";
        var renderer = (ScaledRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ScaledRenderer));
        var host = (DWGame)RuntimeHelpers.GetUninitializedObject(typeof(DWGame));
        GC.SuppressFinalize(renderer);
        GC.SuppressFinalize(host);
        var player = galaxy.Empires.GetPlayer();
        var gameSettings = new GameSettings { ZoomSpeed = 1.5f };
        AccessTools.Field(typeof(DWGame), "DWSettings").SetValue(host, gameSettings);
        AccessTools.Field(typeof(ScaledRenderer), "_Game").SetValue(renderer, host);
        AccessTools.Field(typeof(ScaledRenderer), "_Galaxy").SetValue(renderer, galaxy);
        AccessTools.Field(typeof(ScaledRenderer), "_PlayerEmpire").SetValue(renderer, player);
        var scene = AccessTools.Field(typeof(ScaledRenderer), "_SceneRenderType");
        scene.SetValue(renderer, SceneRenderType.Galaxy);

        MethodInfo Target(string name, params Type[] signature) => AccessTools.Method(typeof(ScaledRenderer), name, signature);
        var message = Target("EmpireMessageShowClick", typeof(object), typeof(DWEventArgs));
        var roster = Target("MoveViewToItemFromEmpireSectionList", typeof(object), typeof(DWEventArgs));
        var hotkey = Target("SelectHotkeyItem", typeof(int));
        var itemZoom = Target("ZoomToItem", typeof(StellarObject), typeof(GameTime), typeof(float), typeof(float));
        var wheel = Target("DoScrollWheel", typeof(float), typeof(Point), typeof(float), typeof(float));
        var keyboardPan = Target("DoMouseMove", typeof(Vector2));
        var keyIn = Target("ZoomIn", typeof(float));
        var keyOut = Target("ZoomOut", typeof(float));
        var inputUpdate = AccessTools.GetDeclaredMethods(AccessTools.TypeByName("DistantWorlds2.InputSystem"))
            .Single(method => method.Name == "Update" && method.GetParameters().Length == 5);
        var hooks = new[] { message, roster, itemZoom, wheel, keyIn, keyOut, inputUpdate, keyboardPan, hotkey };
        Require(Harmony.GetPatchInfo(message)?.Prefixes.Any(p => p.PatchMethod.Module.Assembly.GetName().Name == "DW2SettingsKeeper") == true,
            "actual Settings Keeper Show Me hooks are active alongside the camera patch");

        var headless = new Harmony("test.community-patch.map-camera");
        void Stub(MethodBase m, string callback)
            => headless.Patch(m, prefix: new HarmonyMethod(typeof(MapCameraChecks), callback));
        Stub(Target("SetView", typeof(Location), typeof(int), typeof(int), typeof(float), typeof(float), typeof(float)), nameof(CaptureView));
        Stub(Target("SetViewGalaxy", typeof(int), typeof(int), typeof(float), typeof(float), typeof(float)), nameof(CaptureGalaxyView));
        var pan = Target("MoveViewNoZoom", typeof(StellarObject), typeof(float));
        Stub(pan, nameof(CapturePan));
        Stub(Target("Zoom", typeof(float), typeof(double), typeof(float), typeof(int), typeof(int)), nameof(CaptureZoom));
        Stub(Target("ShowItemInUserInterface", typeof(object), typeof(DrawDetailType), typeof(RectangleF)), nameof(CaptureAction));
        Stub(Target("TriggerRefreshSystemSummary"), nameof(Skip));
        Stub(AccessTools.PropertyGetter(typeof(ScaledRenderer), "CurrentCamera"), nameof(UseCamera));
        Stub(AccessTools.PropertyGetter(typeof(ScaledRenderer), "CurrentGraphicsNodes"), nameof(UseNodes));
        var visibility = new Harmony("local.dw2.last-known-contact");
        visibility.Patch(itemZoom, prefix: new HarmonyMethod(typeof(MapCameraChecks), nameof(NativeVisibilityStub)));
        visibility.Patch(roster, prefix: new HarmonyMethod(typeof(MapCameraChecks), nameof(NativeVisibilityStub)));

        var port = player.Ships.First(s => s.IsBase && !s.IsDestroyed && s.GetSystem(galaxy, false)?.Star is { } star &&
            player.ExplorationMap.CheckExplorationLevelSafely(star.OrbId, 0) > 0);
        var system = port.GetSystem(galaxy, false)!;
        var ship = player.Ships.First(s => !s.IsBase && !s.IsDestroyed && !s.IsHyperjumping() &&
            s.GetSystem(galaxy, false)?.Star is { } star && player.ExplorationMap.CheckExplorationLevelSafely(star.OrbId, 0) > 0);
        void ClearFocus()
        {
            view = null;
            galaxyView = null;
            panned = null;
            zoom = null;
            actions = 0;
        }
        void ShowMessage(EmpireMessage notice)
        {
            ClearFocus();
            message.Invoke(renderer, [null, new DWEventArgs(notice)]);
        }
        void Show(StellarObject? item, EmpireMessageType type = EmpireMessageType.GalaxyNews) =>
            ShowMessage(new EmpireMessage {
                Type = type, RelatedObject = item!, RelatedItem = Item.Null,
                LocationId = -1, ViewMode = EmpireMessageViewMode.Undefined,
            });
        void Focus(object item)
        {
            ClearFocus();
            roster.Invoke(renderer, [null, new DWEventArgs(item)]);
        }
        bool AtGalaxy(StellarObject target) => galaxyView is { } selected && selected.X == target.GalaxyX &&
            selected.Y == target.GalaxyY && selected.Height >= galaxy.SizeX * .25f;

        bool SystemView(StarSystem target) => view is { } captured && captured.X == target.Star.GalaxyX &&
            captured.Y == target.Star.GalaxyY && MathF.Abs(captured.Height - 1_767_500f) < 1;
        if (replayMapNotice)
        {
            var notice = player.Messages.Last(m => m.Title == "Location Maps Recovered");
            var target = notice.RelatedObject;
            var targetSystem = target?.GetSystem(galaxy, false);
            ShowMessage(notice);
            Require(targetSystem != null && SystemView(targetSystem), "saved Location Maps Recovered opens its known system");
            var mapBefore = player.VisibilityMap;
            var exploration = player.ExplorationMap.GetMapDirectly();
            int id = targetSystem!.Star.OrbId;
            byte level = exploration[id];
            try
            {
                player.VisibilityMap = null!;
                exploration[id] = 0;
                scene.SetValue(renderer, SceneRenderType.System);
                ShowMessage(notice);
                Require(target != null && AtGalaxy(target) && view == null && actions == 1 &&
                    exploration[id] == 0, "Location Maps Recovered with no charted system opens Galaxy Map and preserves its UI action");
                scene.SetValue(renderer, SceneRenderType.Galaxy);
                ShowMessage(notice);
                Require(panned == target && view == null && galaxyView == null, "recovered map already on Galaxy Map keeps its zoom");
                refuse = true;
                ShowMessage(notice);
                Require(panned == null && view == null && galaxyView == null, "recovered map respects the native visibility veto");
            }
            finally { refuse = false; player.VisibilityMap = mapBefore; exploration[id] = level; }
            return;
        }
        Show(port);
        Require(SystemView(system) && actions == 1 && zoom is { } autoZoom && Math.Abs(autoZoom - .00001f) < 1e-8,
            "Show Me on a port executes the LNC-style native system frame and keeps the item's UI action");
        Show(ship);
        Require(SystemView(ship.GetSystem(galaxy, false)!) && actions == 1, "Show Me on a ship opens its known system");
        Show(null);
        Require(view == null && zoom == null, "notification without a target does not move the camera");

        var deep = galaxy.Ships.First(s => !s.IsBase && !s.IsDestroyed && s.GetSystem(galaxy, false) == null);
        scene.SetValue(renderer, SceneRenderType.System);
        Show(deep);
        Require(AtGalaxy(deep) && view == null && actions == 1, "System Map Show Me centers a deep-space ship on the galaxy map");
        scene.SetValue(renderer, SceneRenderType.Galaxy);
        Show(deep);
        Require(panned == deep && view == null && galaxyView == null && actions == 1,
            "deep-space Show Me pans at the current galaxy scale");
        var map = player.VisibilityMap;
        var levels = player.ExplorationMap.GetMapDirectly();
        int starId = system.Star.OrbId;
        byte known = levels[starId];
        try
        {
            player.VisibilityMap = null!;
            levels[starId] = 0;
            Show(port);
            Require(view is { } hidden && hidden.X == port.GalaxyX && hidden.Y == port.GalaxyY &&
                hidden.Height != 1_767_500f, "unknown system is not reframed by the mod");
            scene.SetValue(renderer, SceneRenderType.Location);
            Show(port, EmpireMessageType.Exploration);
            Require(AtGalaxy(port) && view == null && levels[starId] == 0,
                "exploration notice with a non-mobile target and no charted system avoids native close-up");
            scene.SetValue(renderer, SceneRenderType.Galaxy);
        }
        finally { player.VisibilityMap = map; levels[starId] = known; }
        refuse = true;
        Show(port);
        refuse = false;
        Require(view == null && zoom == null, "a visibility veto on ZoomToItem still prevents Show Me camera movement");

        view = null;
        itemZoom.Invoke(renderer, [port, Activator.CreateInstance(typeof(GameTime)), 0f, 0f]);
        Require(view is { } direct && direct.X == port.GalaxyX && direct.Y == port.GalaxyY &&
            direct.Height != 1_767_500f, "direct camera commands keep native close-up");
        var settings = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CommunityPatch")
            .GetType("CommunityPatch.MapCamera", true)!;
        var showView = AccessTools.Field(settings, "showMeView");
        var shipView = AccessTools.Field(settings, "shipFocusView");
        void SetMode(FieldInfo field, string name) => field.SetValue(null, Enum.Parse(field.FieldType, name));
        var legacy = AccessTools.Method(settings, "ReadShowMeView");
        Require(legacy.Invoke(null, [JsonNode.Parse("{\"ShowMeSystemMap\":false}")])?.ToString() == "Game" &&
            legacy.Invoke(null, [JsonNode.Parse("{\"ShowMeSystemMap\":true}")])?.ToString() == "System" &&
            legacy.Invoke(null, [JsonNode.Parse("{\"ShowMeSystemMap\":false,\"ShowMeView\":\"Galaxy Map\"}")])?.ToString() == "Galaxy",
            "legacy Show Me settings keep their meaning and an explicit new mode takes precedence");
        Require(showView.GetValue(null)?.ToString() == "System" && shipView.GetValue(null)?.ToString() == "Current",
            "loader uses System Map for Show Me and Keep current zoom for roster focus by default");
        Focus(ship);
        Require(panned == ship && view == null && galaxyView == null, "default roster focus uses the native no-zoom move");
        var other = player.Ships.First(s => !s.IsBase && !s.IsDestroyed && !s.IsHyperjumping() &&
            s.GetSystem(galaxy, false) is { } target && target != ship.GetSystem(galaxy, false));
        scene.SetValue(renderer, SceneRenderType.System);
        Focus(other);
        Require(panned == other && view == null, "focusing a ship in another system uses the current scale");
        SetMode(showView, "Current");
        Show(port);
        Require(panned == port && view == null && actions == 1, "Show Me can keep the scale and still performs the notification UI action");
        SetMode(shipView, "System");
        Focus(ship);
        Require(SystemView(ship.GetSystem(galaxy, false)!) && panned == null, "roster focus can open System Map independently of Show Me");
        Focus(deep);
        Require(AtGalaxy(deep) && view == null, "focusing a deep-space ship from System Map opens Galaxy Map");
        var deepExplorer = player.Ships.FirstOrDefault(s => s.Role == ShipRole.ExplorationShip && !s.IsDestroyed &&
            (s.IsHyperjumping() || s.GetSystem(galaxy, false) == null)) ??
            player.Ships.First(s => !s.IsBase && !s.IsDestroyed && s.GetSystem(galaxy, false) == null);
        var originalRole = deepExplorer.Role;
        var originalPosition = Camera.ViewPosition;
        var originalForward = Camera.Forward;
        var originalUp = Camera.Up;
        try
        {
            deepExplorer.Role = ShipRole.ExplorationShip;
            scene.SetValue(renderer, SceneRenderType.Location);
            renderer.TargetViewPosition = new(5, 5, 5);
            renderer.TargetViewIsZoomIn = true;
            renderer.AllowScreenPanningZooming = false;
            Focus(deepExplorer);
            Require(AtGalaxy(deepExplorer) && panned == null && renderer.CanPanZoomView(),
                "deep-space exploration roster focus from a close-up resets the view and restores input");
            scene.SetValue(renderer, SceneRenderType.Galaxy);
            float galaxyHeight = galaxy.SizeX * .38f;
            Camera.ViewPosition.Y = galaxyHeight / 20000;
            Focus(deepExplorer);
            Require(galaxyView is { } rosterView && Math.Abs(rosterView.Height - galaxyHeight) < 32 && panned == null,
                "deep-space exploration roster centers Galaxy Map at a valid current scale");
            Camera.ViewPosition.Y = float.NaN;
            Camera.SetDirection(Vector3.UnitY, Vector3.UnitZ);
            Focus(deepExplorer);
            Require(AtGalaxy(deepExplorer) && Math.Abs(Camera.Forward.Z) > .1f && panned == null,
                "deep-space exploration roster replaces invalid zoom and vertical camera orientation");
        }
        finally
        {
            deepExplorer.Role = originalRole;
            Camera.ViewPosition = originalPosition;
            Camera.SetDirection(originalForward, originalUp);
            scene.SetValue(renderer, SceneRenderType.System);
        }
        var fleet = new Fleet(31000, -1, player.EmpireId, FleetRole.Manual, galaxy) { LeadShip = deep };
        Focus(fleet);
        Require(AtGalaxy(deep) && view == null, "fleet focus uses its deep-space lead ship");
        fleet.LeadShip = ship;
        Focus(fleet);
        Require(SystemView(ship.GetSystem(galaxy, false)!), "fleet focus uses its lead ship's known system");
        var selectedObject = AccessTools.PropertyGetter(typeof(ScaledRenderer), "SelectedObject");
        var selectFleet = Target("SetSelectedObject", typeof(Fleet));
        var selectShip = Target("SetSelectedObject", typeof(StellarObject));
        Stub(selectedObject, nameof(UseSelectedItem));
        Stub(selectFleet, nameof(SelectItem));
        Stub(selectShip, nameof(SelectItem));
        var previousHotkey = galaxy.HotkeyItems[1];
        var explorerHotkey = galaxy.HotkeyItems[2] as Ship;
        var previousExplorerSlot = galaxy.HotkeyItems[2];
        try
        {
            galaxy.HotkeyItems[1] = fleet;
            ClearFocus();
            hotkey.Invoke(renderer, [1]);
            Require(ReferenceEquals(selectedItem, fleet) && view == null && galaxyView == null && panned == null,
                "first fleet hotkey press selects without moving the camera");
            hotkey.Invoke(renderer, [1]);
            Require(SystemView(ship.GetSystem(galaxy, false)!), "second fleet hotkey press opens the lead ship's system");
            fleet.LeadShip = deep;
            ClearFocus();
            hotkey.Invoke(renderer, [1]);
            Require(AtGalaxy(deep) && view == null, "fleet hotkey opens Galaxy Map for a lead ship in deep space");
            scene.SetValue(renderer, SceneRenderType.Galaxy);
            ClearFocus();
            hotkey.Invoke(renderer, [1]);
            Require(panned == deep && view == null && galaxyView == null, "fleet hotkey retains the current galaxy scale");
            SetMode(shipView, "Game");
            ClearFocus();
            hotkey.Invoke(renderer, [1]);
            Require(view is { } native && native.Height != 1_767_500f && galaxyView == null,
                "Game default restores native fleet hotkey close-up");
            SetMode(shipView, "System");

            if (requireExplorerHotkey)
                Require(explorerHotkey is { Role: ShipRole.ExplorationShip, Name: "Honest Scheme" } &&
                    explorerHotkey.GetSystem(galaxy, false) == null, "save retains Honest Scheme in deep space on key 2");
            var explorer = requireExplorerHotkey ? explorerHotkey! : deep;
            galaxy.HotkeyItems[2] = explorer;
            scene.SetValue(renderer, SceneRenderType.System);
            ClearFocus();
            Require((bool)hotkey.Invoke(renderer, [2])! && ReferenceEquals(selectedItem, explorer) &&
                view == null && galaxyView == null, "first explorer hotkey press selects without zoom");
            renderer.TargetViewPosition = new Vector3(10, 1, 10);
            renderer.TargetViewIsZoomIn = true;
            renderer.AllowScreenPanningZooming = false;
            Require(!renderer.CanPanZoomView(), "pending game camera animation blocks wheel before focusing");
            bool handled = (bool)hotkey.Invoke(renderer, [2])!;
            Require(AtGalaxy(explorer) && view == null && renderer.TargetViewPosition == Vector3.Zero &&
                renderer.CanPanZoomView() && handled, "key 2 opens Galaxy Map, consumes the key and releases the wheel");

            galaxy.HotkeyItems[2] = ship;
            selectedItem = ship;
            int shipStar = ship.GetSystem(galaxy, false)!.Star.OrbId;
            byte oldLevel = levels[shipStar];
            try
            {
                player.VisibilityMap = null!;
                levels[shipStar] = 0;
                ClearFocus();
                hotkey.Invoke(renderer, [2]);
                Require(AtGalaxy(ship) && view == null, "own ship in an uncharted system stays on Galaxy Map");
            }
            finally { player.VisibilityMap = map; levels[shipStar] = oldLevel; }

            Require(hooks.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) == true),
                "camera hooks loaded through the Community Patch bundle");
            if (requireExplorerHotkey) return;
        }
        finally
        {
            galaxy.HotkeyItems[1] = previousHotkey;
            galaxy.HotkeyItems[2] = previousExplorerSlot;
            selectedItem = null;
            headless.Unpatch(selectedObject, HarmonyPatchType.Prefix, headless.Id);
            headless.Unpatch(selectFleet, HarmonyPatchType.Prefix, headless.Id);
            headless.Unpatch(selectShip, HarmonyPatchType.Prefix, headless.Id);
        }
        scene.SetValue(renderer, SceneRenderType.Location);
        Focus(deep);
        Require(AtGalaxy(deep) && view == null, "a local close-up can open Galaxy Map directly for a deep-space ship");
        refuse = true;
        Focus(deep);
        Require(view == null && panned == null && galaxyView == null, "a deep-space focus respects the visibility veto");
        refuse = false;
        scene.SetValue(renderer, SceneRenderType.System);
        SetMode(showView, "Galaxy");
        Show(deep);
        Require(AtGalaxy(deep!) && panned == null && actions == 1, "Show Me centers a deep-space target in Galaxy Map mode");
        SetMode(shipView, "Galaxy");
        Focus(ship);
        Require(AtGalaxy(ship) && view == null && panned == null, "roster focus can center the ship in Galaxy Map mode");
        refuse = true;
        Focus(ship);
        Require(view == null && galaxyView == null && panned == null, "a roster visibility veto prevents the custom view");
        refuse = false;
        Focus(port);
        Require(view is { } baseView && baseView.Height != 1_767_500f && galaxyView == null,
            "the ship focus selector leaves non-ship roster navigation to the game");
        SetMode(shipView, "Game");
        Focus(deep);
        Require(view is { } nativeShip && nativeShip.Height != 1_767_500f && galaxyView == null && panned == null,
            "Game default restores native roster close-up");
        SetMode(showView, "Game");
        Show(port);
        Require(view is { } disabled && disabled.Height != 1_767_500f && actions == 1, "Game default restores native Show Me navigation");
        SetMode(showView, "System");
        SetMode(shipView, "Current");

        headless.Unpatch(pan, HarmonyPatchType.Prefix, headless.Id);
        renderLeaf = (DWRendererBase)RuntimeHelpers.GetUninitializedObject(typeof(CustomOrbRenderer));
        var locationNodes = (GraphicsNodeList)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsNodeList));
        AccessTools.Field(typeof(ScaledRenderer), "_GraphicsNodesSystem").SetValue(renderer, Nodes);
        AccessTools.Field(typeof(ScaledRenderer), "_GraphicsNodesLocation").SetValue(renderer, locationNodes);
        AccessTools.Field(typeof(ScaledRenderer), "_Player").SetValue(renderer, RuntimeHelpers.GetUninitializedObject(typeof(GameClient)));
        Stub(AccessTools.PropertyGetter(typeof(GraphicsNodeList), "RendererPostScene"), nameof(UseRenderLeaf));
        Stub(AccessTools.Method(typeof(GraphicsNodeList), "ClearAllDisplayEffects", [typeof(Stride.Engine.Scene)]), nameof(Skip));
        Stub(AccessTools.Method(typeof(GraphicsNodeList), "ClearAllFixedStructures", [typeof(Stride.Engine.Scene)]), nameof(Skip));
        Stub(AccessTools.Method(typeof(DWRendererBase), "BindNebulaSystem", [typeof(Galaxy), typeof(StarSystem),
            typeof(Location), AccessTools.TypeByName("DistantWorlds2.NebulaCloudSystem")]), nameof(Skip));
        Stub(AccessTools.Method(typeof(DWGame), "GetServerTimeInSeconds", Type.EmptyTypes), nameof(UseServerTime));
        Nodes.CoordinateDivisor = 1000;
        Camera.SetDirection(new(0, -.7071068f, .7071068f), new(0, .7071068f, .7071068f));
        Camera.SetPosition(0, 4100, 0);
        void CheckNativePan(Ship target, SceneRenderType current)
        {
            scene.SetValue(renderer, current);
            Focus(target);
            var position = Camera.ViewPosition;
            var center = position - Camera.Forward * (position.Y / Camera.Forward.Y);
            int originX = current == SceneRenderType.Galaxy ? galaxy.SizeX / 2 : renderer.CurrentSystem.GalaxyX;
            int originY = current == SceneRenderType.Galaxy ? galaxy.SizeY / 2 : renderer.CurrentSystem.GalaxyY;
            double errorX = (originX - (double)target.GalaxyX) - center.X * (double)Nodes.CoordinateDivisor;
            double errorY = (originY - (double)target.GalaxyY) - center.Z * (double)Nodes.CoordinateDivisor;
            Require(renderer.SceneRenderType == current && Math.Abs(position.Y - 4100) < .01 &&
                Math.Abs(errorX) < 128 && Math.Abs(errorY) < 128,
                $"native roster focus centers the ship at the original camera height in {current} (scene {renderer.SceneRenderType}, height {position.Y:F2}, coordinate error {errorX:F1}, {errorY:F1})");
        }
        CheckNativePan(ship, SceneRenderType.System);
        CheckNativePan(other, SceneRenderType.System);
        CheckNativePan(ship, SceneRenderType.Galaxy);
        SetMode(shipView, "System");
        var deepRole = deep.Role;
        try
        {
            // The explorer roster uses SetViewGalaxy instead of this separately tested native pan.
            if (deepRole == ShipRole.ExplorationShip) deep.Role = ShipRole.FreighterSmall;
            CheckNativePan(deep, SceneRenderType.Galaxy);
        }
        finally { deep.Role = deepRole; }

        var smoothing = AccessTools.Field(settings, "wheelSmoothingMs");
        var coast = AccessTools.Field(settings, "wheelCoast");
        var ramp = AccessTools.Field(settings, "wheelRamp");
        Require((float)smoothing.GetValue(null)! == 320 && (float)coast.GetValue(null)! == .5f &&
            (float)ramp.GetValue(null)! == .25f,
            "official loader applied Gentle ramp and Inertia at 320 ms and 50 percent coast");
        var readMotion = AccessTools.Method(settings, "ReadWheelMotion");
        (float, float) Motion(string json) => (ValueTuple<float,float>)readMotion.Invoke(null, [JsonNode.Parse(json)])!;
        Require(Motion("""{"WheelZoomSmoothing":"Inertia"}""") == (220, .25f) &&
            Motion("""{"WheelZoomSmoothing":"Soft","WheelSmoothingDuration":"450 ms","WheelInertiaCoast":"75%"}""") == (450, 0) &&
            Motion("""{"WheelZoomSmoothing":"Short","WheelSmoothingDuration":"450 ms","WheelInertiaCoast":"75%"}""") == (120, 0) &&
            Motion("""{"WheelZoomSmoothing":"Off","WheelSmoothingDuration":"450 ms","WheelInertiaCoast":"75%"}""") == (0, 0) &&
            Motion("""{"WheelZoomSmoothing":"Inertia","WheelSmoothingDuration":"999 ms","WheelInertiaCoast":100}""") == (220, .25f),
            "old profiles keep their default feel, and the new settings apply only in their matching modes");
        smoothing.SetValue(null, 0f);
        var limit = AccessTools.Field(settings, "wheelLimit");
        Require((float)limit.GetValue(null)! == 4, "loader applied the configured 4x wheel limit");
        limit.SetValue(null, 0f);
        void Scroll(SceneRenderType selected, float delta, float expected)
        {
            scene.SetValue(renderer, selected);
            zoom = null;
            wheel.Invoke(renderer, [delta, new Point(320, 250), .015f, 1f]);
            Require(zoom == expected, $"{selected} wheel delta {delta} yields {expected}");
        }
        Scroll(SceneRenderType.Galaxy, 120f, 720f);
        Scroll(SceneRenderType.System, 120f, 360f);
        Scroll(SceneRenderType.Location, -120f, -360f);
        Scroll(SceneRenderType.Galaxy, 1000f, 1440f);
        keyIn.Invoke(renderer, [30f]);
        Require(zoom == 120f, "galaxy keyboard zoom uses 4x");
        scene.SetValue(renderer, SceneRenderType.System);
        keyOut.Invoke(renderer, [30f]);
        Require(zoom == -60f, "system keyboard zoom uses 2x");
        AccessTools.Field(settings, "galaxySpeed").SetValue(null, 0f);
        Scroll(SceneRenderType.Galaxy, 120f, 270f);
        Require(gameSettings.ZoomSpeed == 1.5f, "the Game fallback uses the saved game zoom speed without mutating it");

        WheelAccelerationChecks.Run(settings, renderer, wheel, headless, system);

        var observed = hooks.SelectMany(m => new[] { ("transpiler", Harmony.GetPatchInfo(m).Transpilers),
                ("prefix", Harmony.GetPatchInfo(m).Prefixes), ("postfix", Harmony.GetPatchInfo(m).Postfixes) }
            .SelectMany(kind => kind.Item2.Where(p => p.owner == owner).Select(p => new
            {
                target = Signature(m), kind = kind.Item1, effect = kind.Item1 switch
                {
                    "transpiler" => "rewrites",
                    "prefix" when p.PatchMethod.ReturnType == typeof(bool) => "skips-original",
                    _ => "observes",
                },
                why = "CommunityPatch " + Signature(p.PatchMethod),
            }))).ToArray();
        File.WriteAllText(Path.Combine(logs, "map-camera-hooks.json"), System.Text.Json.JsonSerializer.Serialize(observed));
    }

    private static string Signature(MethodBase m) => m.DeclaringType!.FullName + ":" + m.Name + "(" +
        string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")";

    private static bool CaptureView(int galaxyX, int galaxyY, float height)
    { view = (galaxyX, galaxyY, height); return false; }
    private static bool CaptureGalaxyView(int galaxyX, int galaxyY, float height)
    { galaxyView = (galaxyX, galaxyY, height); return false; }
    private static bool CapturePan(StellarObject item) { panned = item; return false; }
    private static bool CaptureZoom(float zoomAmount) { zoom = zoomAmount; return false; }
    private static bool CaptureAction() { actions++; return false; }
    private static bool Skip() => false;
    private static bool UseCamera(ref CameraInfo __result) { __result = Camera; return false; }
    private static bool UseSelectedItem(ref object? __result) { __result = selectedItem; return false; }
    private static bool SelectItem(object __0) { selectedItem = __0; return false; }
    private static bool UseNodes(ref GraphicsNodeList __result) { __result = Nodes; return false; }
    private static bool UseRenderLeaf(ref DWRendererBase __result) { __result = renderLeaf!; return false; }
    private static bool UseServerTime(ref double __result) { __result = 0; return false; }
    private static bool NativeVisibilityStub() => !refuse;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
