using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;

namespace CommunityPatch;

internal static class MapCamera
{
    private const string Owner = "DW2.CommunityPatch.MapCamera";
    private static readonly System.Reflection.MethodInfo ZoomToItem = AccessTools.Method(typeof(ScaledRenderer),
        nameof(ScaledRenderer.ZoomToItem), [typeof(StellarObject), typeof(GameTime), typeof(float), typeof(float)]);
    private static readonly MethodInfo NativeMoveToItem = AccessTools.Method(typeof(ScaledRenderer), "MoveViewToItem", [typeof(object)]);
    private static readonly Action<ScaledRenderer, object> MoveViewToItem =
        AccessTools.MethodDelegate<Action<ScaledRenderer, object>>(NativeMoveToItem);
    private static readonly System.Reflection.FieldInfo ZoomSpeed = AccessTools.Field(typeof(GameSettings), nameof(GameSettings.ZoomSpeed));
    private static readonly MethodInfo NativeZoom = AccessTools.Method(typeof(ScaledRenderer), "Zoom",
        [typeof(float), typeof(double), typeof(float), typeof(int), typeof(int)]);
    private static readonly Action<ScaledRenderer, float, double, float, int, int> ApplyZoom =
        AccessTools.MethodDelegate<Action<ScaledRenderer, float, double, float, int, int>>(NativeZoom);

    private enum FocusView { Game, Current, System, Galaxy, Exploration, ExplorerRoster }

    [ThreadStatic] private static FocusView requestedView;
    [ThreadStatic] private static bool focusHandled;
    private static FocusView showMeView;
    private static FocusView shipFocusView;
    private static float systemSpeed;
    private static float galaxySpeed;
    private static float wheelLimit;
    private static float wheelRamp;
    private static ScaledRenderer? wheelRenderer;
    private static WheelBurst wheelBurst;
    private static float wheelSmoothingMs;
    private static float wheelCoast;
    private static WheelMotion wheelMotion;

    internal static void ApplySettings(JsonObject config)
    {
        showMeView = ReadShowMeView(config);
        shipFocusView = ReadFocusView(config["ShipFocusView"], FocusView.Current);
        systemSpeed = ReadSpeed(config["SystemZoomSpeed"]);
        galaxySpeed = ReadSpeed(config["GalaxyZoomSpeed"]);
        wheelLimit = ReadSpeed(config["WheelAcceleration"]);
        if (wheelLimit < 2) wheelLimit = 0;
        wheelRamp = ReadWheelRamp(config["WheelAccelerationRamp"]);
        (wheelSmoothingMs, wheelCoast) = ReadWheelMotion(config);
        wheelMotion = default;
        wheelBurst = default;
        wheelRenderer = null;
    }

    internal static void Init(JsonObject config)
    {
        ApplySettings(config);

        var harmony = new Harmony(Owner);
        HarmonyMethod Hook(string method) => new(typeof(MapCamera), method) { priority = Priority.Last };
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ScaledRenderer), "EmpireMessageShowClick", [typeof(object), typeof(DWEventArgs)]),
                transpiler: Hook(nameof(ScopeFocusZoom)));
            harmony.Patch(AccessTools.Method(typeof(ScaledRenderer), "MoveViewToItemFromEmpireSectionList", [typeof(object), typeof(DWEventArgs)]),
                transpiler: Hook(nameof(ScopeFocusZoom)));
            harmony.Patch(AccessTools.Method(typeof(ScaledRenderer), "SelectHotkeyItem", [typeof(int)]),
                transpiler: Hook(nameof(ScopeFocusZoom)));
            harmony.Patch(ZoomToItem, prefix: new HarmonyMethod(typeof(MapCamera), nameof(BeforeZoomToItem))
                { priority = Priority.Last, after = ["local.dw2.last-known-contact"] });
            foreach (var method in new[]
            {
                AccessTools.Method(typeof(ScaledRenderer), "DoScrollWheel", [typeof(float), typeof(Point), typeof(float), typeof(float)]),
                AccessTools.Method(typeof(ScaledRenderer), "ZoomIn", [typeof(float)]),
                AccessTools.Method(typeof(ScaledRenderer), "ZoomOut", [typeof(float)]),
            })
                harmony.Patch(method, transpiler: Hook(nameof(ApplyViewSpeed)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("DistantWorlds2.InputSystem"), "Update",
                [typeof(InputManager), typeof(GameSettings), typeof(GameTime), typeof(float), typeof(float)]),
                postfix: Hook(nameof(AfterInputUpdate)));
            harmony.Patch(AccessTools.Method(typeof(ScaledRenderer), "DoMouseMove", [typeof(Vector2)]),
                prefix: Hook(nameof(BeforePan)), postfix: Hook(nameof(AfterPan)));
        }
        catch
        {
            harmony.UnpatchAll(Owner);
            throw;
        }
    }

    private static string? ReadChoice(JsonNode? setting) =>
        setting is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static float ReadSpeed(JsonNode? setting) =>
        ReadChoice(setting) switch { "1x" => 1, "2x" => 2, "3x" => 3, "4x" => 4, _ => 0 };

    private static float ReadWheelRamp(JsonNode? setting) =>
        ReadChoice(setting) switch { "Gentle" => .25f, "Fast" => 1f, _ => .5f };

    private static (float Duration, float Coast) ReadWheelMotion(JsonObject config)
    {
        var mode = ReadChoice(config["WheelZoomSmoothing"]);
        if (mode == "Short") return (120, 0);
        if (mode is not ("Soft" or "Inertia")) return (0, 0);
        float duration = ReadChoice(config["WheelSmoothingDuration"]) switch { "320 ms" => 320, "450 ms" => 450, _ => 220 };
        float coast = mode == "Inertia" ? ReadChoice(config["WheelInertiaCoast"]) switch
        {
            "0%" => 0, "50%" => .5f, "75%" => .75f, _ => .25f,
        } : 0;
        return (duration, coast);
    }

    private static FocusView ReadShowMeView(JsonObject config)
    {
        bool legacyOff = config["ShowMeSystemMap"] is JsonValue legacy && legacy.TryGetValue<bool>(out var enabled) && !enabled;
        return ReadFocusView(config["ShowMeView"], legacyOff ? FocusView.Game : FocusView.System);
    }

    private static FocusView ReadFocusView(JsonNode? setting, FocusView fallback) =>
        setting is JsonValue value && value.TryGetValue<string>(out var text) ? text switch
        {
            "Keep current zoom" => FocusView.Current,
            "System Map" => FocusView.System,
            "Galaxy Map" => FocusView.Galaxy,
            "Game default" => FocusView.Game,
            _ => fallback,
        } : fallback;

    private static IEnumerable<CodeInstruction> ScopeFocusZoom(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = instructions.ToList();
        bool showMe = __originalMethod.Name == "EmpireMessageShowClick";
        bool hotkey = __originalMethod.Name == "SelectHotkeyItem";
        var calls = codes.Where(code => code.Calls(showMe ? ZoomToItem : NativeMoveToItem)).ToArray();
        if (calls.Length != (showMe ? 6 : 1))
            throw new InvalidOperationException("Camera focus calls do not match the verified Steam beta: " + __originalMethod.Name);
        var wrapper = AccessTools.Method(typeof(MapCamera), showMe ? nameof(FocusFromShowMe) :
            hotkey ? nameof(FocusFromHotkey) : nameof(FocusFromRoster));
        foreach (var call in calls)
        {
            if (showMe)
            {
                var args = new CodeInstruction(OpCodes.Ldarg_2).MoveLabelsFrom(call).MoveBlocksFrom(call);
                codes.Insert(codes.IndexOf(call), args);
            }
            call.opcode = OpCodes.Call;
            call.operand = wrapper;
            if (hotkey)
            {
                var exit = codes[codes.IndexOf(call) + 1];
                if (exit.opcode != OpCodes.Br && exit.opcode != OpCodes.Br_S)
                    throw new InvalidOperationException("Unexpected return after hotkey camera focus.");
                // A handled key must not continue to the ordinary key mapping in InputSystem.Update.
                exit.opcode = OpCodes.Ret;
                exit.operand = null;
            }
        }
        return codes;
    }

    private static void FocusFromShowMe(ScaledRenderer renderer, StellarObject item, GameTime time, float passed, float inSeconds,
        DWEventArgs args)
    {
        var previous = requestedView;
        requestedView = showMeView == FocusView.System && args.ExtraData is EmpireMessage { Type: EmpireMessageType.Exploration }
            ? FocusView.Exploration : showMeView;
        try { renderer.ZoomToItem(item, time, passed, inSeconds); }
        finally { requestedView = previous; }
    }

    private static void FocusFromRoster(ScaledRenderer renderer, object item) => FocusShipOrFleet(renderer, item, roster: true);

    private static void FocusShipOrFleet(ScaledRenderer renderer, object item, bool roster = false)
    {
        var previous = requestedView;
        requestedView = item is Ship { IsBase: false } or Fleet { LeadShip: { IsBase: false } }
            ? shipFocusView : FocusView.Game;
        if (roster && requestedView == FocusView.System && item is Ship { Role: ShipRole.ExplorationShip })
            requestedView = FocusView.ExplorerRoster;
        // The original handler still resolves its target, calls the visibility-guarded zoom and refreshes the UI.
        try { MoveViewToItem(renderer, item); }
        finally { requestedView = previous; }
    }

    private static bool FocusFromHotkey(ScaledRenderer renderer, object item)
    {
        var previous = focusHandled;
        focusHandled = false;
        try
        {
            FocusShipOrFleet(renderer, item);
            return focusHandled;
        }
        finally { focusHandled = previous; }
    }

    private static bool BeforeZoomToItem(ScaledRenderer __instance, StellarObject? item, float timePassed,
        float timeInSeconds, Galaxy? ____Galaxy, Empire? ____PlayerEmpire, long ____GalaxyZoomTransitionStart, bool __runOriginal)
    {
        if (!__runOriginal) return true;
        wheelMotion = default;
        if (requestedView == FocusView.Game || item == null || ____Galaxy == null || ____PlayerEmpire == null ||
            __instance.SceneRenderType is not (SceneRenderType.Galaxy or SceneRenderType.System or SceneRenderType.Location)) return true;

        switch (requestedView)
        {
            case FocusView.Current when __instance.CurrentCamera != null && __instance.CurrentGraphicsNodes != null:
                if (__instance.SceneRenderType == SceneRenderType.Location &&
                    ____Galaxy.Locations.FindNearestLocation(item.GalaxyX, item.GalaxyY, true) == null) return true;
                BeginFocus(__instance);
                __instance.MoveViewNoZoom(item);
                return false;
            case FocusView.System:
            case FocusView.Exploration:
            case FocusView.ExplorerRoster:
                var mobileShip = item as Ship;
                bool inTransit = mobileShip is { IsBase: false } && mobileShip.IsHyperjumping();
                var system = inTransit ? null : item.GetSystem(____Galaxy, findNearestIfNotInSystem: false);
                if (ResolveSystemStar(____PlayerEmpire, system) is { } star)
                {
                    BeginFocus(__instance);
                    __instance.SetLockViewOnSelected((StellarObject)null!);
                    __instance.ZoomToSystem(star, timePassed, timeInSeconds);
                    return false;
                }
                if (requestedView == FocusView.ExplorerRoster && mobileShip?.EmpireId == ____PlayerEmpire.EmpireId)
                {
                    if (__instance.CurrentCamera is not { } camera) return false;
                    BeginFocus(__instance);
                    __instance.SetLockViewOnSelected((StellarObject)null!);
                    // Both native pan and the galaxy framing calculation depend on the current camera's direction.
                    var forward = camera.Forward;
                    var up = camera.Up;
                    if (!Finite(forward) || !Finite(up) || forward.X * forward.X + forward.Z * forward.Z < .0001f || Math.Abs(forward.Y) < .01f)
                        camera.SetDirection(new(0, -.7071068f, .7071068f), new(0, .7071068f, .7071068f));
                    float minimum = ____GalaxyZoomTransitionStart * 1.1f;
                    float rosterHeight = Math.Max(____Galaxy.SizeX * .25f, minimum);
                    if (__instance.SceneRenderType == SceneRenderType.Galaxy && float.IsFinite(camera.ViewPosition.Y))
                    {
                        // The native galaxy renderer uses a 20,000:1 camera-to-world coordinate scale.
                        float currentHeight = camera.ViewPosition.Y * 20000;
                        if (currentHeight >= minimum && currentHeight <= ____Galaxy.SizeX) rosterHeight = currentHeight;
                    }
                    __instance.SetViewGalaxy(Math.Clamp(item.GalaxyX, 0, ____Galaxy.SizeX),
                        Math.Clamp(item.GalaxyY, 0, ____Galaxy.SizeY), rosterHeight, timeInSeconds, timePassed);
                    return false;
                }
                if (requestedView == FocusView.Exploration ||
                    mobileShip is { IsBase: false } && (system == null || mobileShip.EmpireId == ____PlayerEmpire.EmpireId))
                {
                    if (__instance.SceneRenderType == SceneRenderType.Galaxy &&
                        __instance.CurrentCamera != null && __instance.CurrentGraphicsNodes != null)
                    {
                        BeginFocus(__instance);
                        __instance.MoveViewNoZoom(item);
                        return false;
                    }
                    goto case FocusView.Galaxy;
                }
                return true;
            case FocusView.Galaxy:
                BeginFocus(__instance);
                __instance.SetLockViewOnSelected((StellarObject)null!);
                float height = Math.Max(____Galaxy.SizeX * .25f, ____GalaxyZoomTransitionStart * 1.1f);
                __instance.SetViewGalaxy(item.GalaxyX, item.GalaxyY, height, timeInSeconds, timePassed);
                return false;
            default:
                return true;
        }
    }

    private static void BeginFocus(ScaledRenderer renderer)
    {
        renderer.TargetViewPosition = Vector3.Zero;
        renderer.TargetViewIsZoomIn = false;
        renderer.AllowScreenPanningZooming = true;
        focusHandled = true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Orb? ResolveSystemStar(Empire player, StarSystem? system)
    {
        if (system?.Star is not { } star) return null;
        bool visible = player.VisibilityMap is { } map && map.GetSystemVisibilityStatus(system) != VisibilityStatus.Unexplored;
        bool charted = player.ExplorationMap?.CheckExplorationLevelSafely(star.OrbId, 0) > 0;
        return visible || charted ? star : null;
    }

    private static IEnumerable<CodeInstruction> ApplyViewSpeed(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = instructions.ToList();
        var index = Enumerable.Range(0, codes.Count).Single(i => codes[i].LoadsField(ZoomSpeed));
        bool wheel = __originalMethod.Name == "DoScrollWheel";
        var adjust = new List<CodeInstruction> { new(OpCodes.Ldarg_0) };
        if (wheel) adjust.Add(new(OpCodes.Ldarg_1));
        adjust.Add(new(OpCodes.Call, AccessTools.Method(typeof(MapCamera), wheel ? nameof(SelectWheelSpeed) : nameof(SelectKeyZoomSpeed))));
        codes.InsertRange(index + 1, adjust);
        if (wheel)
        {
            var zoom = codes.Single(code => code.Calls(NativeZoom));
            codes.Insert(codes.IndexOf(zoom), new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(zoom));
            zoom.opcode = OpCodes.Call;
            zoom.operand = AccessTools.Method(typeof(MapCamera), nameof(QueueWheelZoom));
        }
        return codes;
    }

    private static float SelectKeyZoomSpeed(float gameSpeed, ScaledRenderer renderer)
    {
        wheelMotion = default;
        return SelectZoomSpeed(gameSpeed, renderer);
    }

    private static void QueueWheelZoom(ScaledRenderer renderer, float amount, double stepSeconds, float timeInSeconds, int x, int y, float wheelDelta)
    {
        if (wheelSmoothingMs == 0)
        {
            ApplyZoom(renderer, amount, stepSeconds, timeInSeconds, x, y);
            return;
        }
        if (renderer.CurrentCamera is not { } camera || amount == 0 || !float.IsFinite(amount) ||
            stepSeconds <= 0 || !double.IsFinite(stepSeconds)) return;
        wheelMotion.Queue(renderer, camera, amount, (float)Math.Min(WheelMotion.NativeStepSeconds, stepSeconds),
            x, y, wheelCoast, Math.Abs(wheelDelta) / WheelDeltaPerNotch);
    }

    [SupportedOSPlatform("windows7.0")]
    private static void AfterInputUpdate(InputManager? inputManager, GameSettings gameSettings, GameTime gameTime,
        float timeInSeconds, ScaledRenderer ____Renderer)
    {
        if (!wheelMotion.Active) return;
        if (inputManager == null || (inputManager.MouseWheelDelta != 0 && !wheelMotion.QueuedOnFrame) ||
            !UserInterfaceController.CheckCanProcessEvent(gameSettings) || !____Renderer.CanPanZoomView())
        {
            wheelMotion = default;
            return;
        }
        wheelMotion.Tick(____Renderer, (float)gameTime.Elapsed.TotalMilliseconds, timeInSeconds, wheelSmoothingMs);
    }

    private static void BeforePan(ScaledRenderer __instance, out CameraInfo? __state)
    {
        __state = wheelMotion.Active && __instance.CurrentCamera is { } camera && wheelMotion.Matches(__instance, camera)
            ? camera : null;
    }

    private static void AfterPan(ScaledRenderer __instance, CameraInfo? __state)
    {
        // WASD and edge panning already moved the camera. Carry the remaining wheel movement forward from there.
        if (__state != null) wheelMotion.FollowPan(__instance, __state);
    }

    private static float SelectWheelSpeed(float gameSpeed, ScaledRenderer renderer, float delta)
    {
        float speed = SelectZoomSpeed(gameSpeed, renderer);
        if (wheelLimit == 0) return speed;
        if (!ReferenceEquals(wheelRenderer, renderer))
        {
            wheelRenderer = renderer;
            wheelBurst = default;
        }
        return wheelBurst.Advance(speed, wheelLimit, renderer.SceneRenderType == SceneRenderType.Galaxy,
            delta, Environment.TickCount64, wheelRamp);
    }

    private static float SelectZoomSpeed(float gameSpeed, ScaledRenderer renderer)
    {
        var speed = renderer.SceneRenderType switch
        {
            SceneRenderType.Galaxy => galaxySpeed,
            SceneRenderType.System or SceneRenderType.Location => systemSpeed,
            _ => 0
        };
        return speed > 0 ? speed : gameSpeed;
    }

    private const float WheelDeltaPerNotch = 240;

    internal struct WheelBurst
    {
        private const int ResetAfterMs = 200;
        private const int FullRateMs = 60;
        private long previousAt;
        private int direction;
        private bool galaxyMap;
        private float progress;

        internal float Advance(float speed, float limit, bool onGalaxyMap, float delta, long now, float ramp)
        {
            if (limit <= speed)
            {
                this = default;
                return speed;
            }
            if (!float.IsFinite(delta) || delta == 0) return speed;
            int nextDirection = delta > 0 ? 1 : -1;
            long elapsed = now - previousAt;
            if (direction != nextDirection || galaxyMap != onGalaxyMap || elapsed < 0 || elapsed >= ResetAfterMs)
                progress = 0;
            else
            {
                float rate = Math.Min(1, (ResetAfterMs - elapsed) / (float)(ResetAfterMs - FullRateMs));
                float steps = Math.Min(Math.Abs(delta), WheelDeltaPerNotch) / WheelDeltaPerNotch;
                progress = Math.Min(1, progress + ramp * rate * steps);
            }
            previousAt = now;
            direction = nextDirection;
            galaxyMap = onGalaxyMap;
            return speed + (limit - speed) * progress;
        }
    }

    internal struct WheelMotion
    {
        internal const double NativeStepSeconds = .02;
        private const float CoastAfterMs = 100;
        private const float CloseViewWorldHeight = 1600;
        private const float SystemInwardFraction = .22f;
        private static readonly Func<CameraInfo, int, int, Viewport, Vector3> ClickPosition =
            AccessTools.MethodDelegate<Func<CameraInfo, int, int, Viewport, Vector3>>(
                AccessTools.Method(typeof(DWRendererBase), "CalculateClickPosition", [typeof(CameraInfo), typeof(int), typeof(int), typeof(Viewport)]));
        private static readonly AccessTools.FieldRef<Viewport> Viewport =
            AccessTools.StaticFieldRefAccess<Viewport>(AccessTools.Field(typeof(DWRendererBase), "Viewport"));
        private static readonly Func<CameraInfo, float> ScrollRate = AccessTools.MethodDelegate<Func<CameraInfo, float>>(
            AccessTools.Method(typeof(DWRendererBase), "CalculateScrollRate", [typeof(CameraInfo)]));
        private static readonly AccessTools.FieldRef<ScaledRenderer, float> MinHeight =
            AccessTools.FieldRefAccess<ScaledRenderer, float>("_MinY");
        private static readonly CameraInfo RateProbe = new();

        private ScaledRenderer? renderer;
        private SceneRenderType scene;
        private Vector3 position;
        private float remainingDistance;
        private float coastDistance;
        private float elapsedMs;
        private int x;
        private int y;

        internal bool Active => renderer != null;
        internal bool QueuedOnFrame { get; private set; }

        internal void Queue(ScaledRenderer current, CameraInfo camera, float amount, float stepSeconds, int x, int y, float coast, float notches)
        {
            if (!Matches(current, camera) || (amount > 0) != (remainingDistance > 0)) this = default;
            bool quickRepeat = Active && elapsedMs < CoastAfterMs;
            float closeHeight = current.SceneRenderType is SceneRenderType.Location or SceneRenderType.System &&
                current.CurrentGraphicsNodes is { CoordinateDivisor: > 0 } nodes
                ? CloseViewWorldHeight / nodes.CoordinateDivisor : 0;
            if (!float.IsFinite(closeHeight)) closeHeight = 0;
            float projectedHeight = camera.ViewPosition.Y;
            float vertical = 0;
            if (remainingDistance != 0 || (amount > 0 && closeHeight > 0))
            {
                // Match the native wheel position when new input arrives before the last step finishes.
                Vector3 direction = amount < 0 ? camera.Forward :
                    ClickPosition(camera, x, y, Viewport()) - Matrix.Invert(camera.View).TranslationVector;
                direction.Normalize();
                vertical = float.IsFinite(direction.Y) ? direction.Y : 0;
                projectedHeight += remainingDistance * vertical;
            }
            RateProbe.ViewPosition.Y = projectedHeight;
            if (amount < 0 && closeHeight > 0 && projectedHeight < closeHeight)
            {
                float closeness = Math.Clamp(1 - projectedHeight / closeHeight, 0, 1);
                RateProbe.ViewPosition.Y += closeHeight * .5f * closeness * closeness;
            }
            float distance = amount * stepSeconds * ScrollRate(RateProbe);
            bool inwardSystem = amount > 0 && current.SceneRenderType == SceneRenderType.System;
            float extra = quickRepeat && !inwardSystem ? distance * coast : 0;
            if (amount > 0 && closeHeight > 0 && vertical < 0)
            {
                float descent = -vertical;
                float aboveCloseView = Math.Max(0, projectedHeight - closeHeight);
                // Keep isolated notches precise and limit the portion of a burst that descends below close view.
                float allowance = aboveCloseView + Math.Clamp(projectedHeight, 0, closeHeight) * (quickRepeat ? .2f : .5f);
                if (inwardSystem)
                {
                    // Successive notches descend from the projected height, including coalesced wheel input.
                    float notchTravel = projectedHeight * (float)(1 - Math.Pow(1 - SystemInwardFraction, notches));
                    // A queued burst can cross the distant part freely, then spend at most one fine step near the planet.
                    float startHeight = camera.ViewPosition.Y;
                    float closeFloor = Math.Min(startHeight, closeHeight) * (1 - SystemInwardFraction);
                    allowance = Math.Min(allowance, Math.Min(notchTravel, Math.Max(0, projectedHeight - closeFloor)));
                }
                float clearance = Math.Max(0, projectedHeight - MinHeight(current));
                distance = Math.Min(distance, Math.Min(allowance, clearance) / descent);
                extra = Math.Min(extra, Math.Max(0, aboveCloseView / descent - distance));
            }
            if (distance == 0 && remainingDistance == 0) return;
            remainingDistance += distance;
            coastDistance = extra;
            elapsedMs = 0;
            this.x = x;
            this.y = y;
            QueuedOnFrame = true;
            Remember(current, camera);
        }

        internal void Tick(ScaledRenderer current, float frameMs, float timeInSeconds, float durationMs)
        {
            if (!Active) return;
            QueuedOnFrame = false;
            if (current.CurrentCamera is not { } camera || !Matches(current, camera) || frameMs > 250)
            {
                this = default;
                return;
            }
            if (!float.IsFinite(frameMs) || frameMs <= 0) return;
            if (remainingDistance > 0 && (scene is SceneRenderType.Location or SceneRenderType.System) &&
                camera.ViewPosition.Y <= MinHeight(current))
            {
                this = default;
                return;
            }

            float previous = elapsedMs;
            elapsedMs = Math.Min(durationMs, elapsedMs + frameMs);
            if (coastDistance != 0 && elapsedMs >= CoastAfterMs)
            {
                remainingDistance += coastDistance;
                coastDistance = 0;
            }
            float tailRatio = (durationMs - elapsedMs) / (durationMs - previous);
            float distance = remainingDistance * (1 - tailRatio * tailRatio * tailRatio);
            remainingDistance -= distance;

            // Feed distance through the vanilla zoom path, with the current scene's units and cursor anchor.
            float divisor = current.CurrentGraphicsNodes.CoordinateDivisor;
            ApplyZoom(current, distance / (float)(NativeStepSeconds * ScrollRate(camera)),
                NativeStepSeconds, timeInSeconds, x, y);
            var updatedCamera = current.CurrentCamera;
            if (elapsedMs >= durationMs || updatedCamera == null ||
                (current.SceneRenderType == scene && updatedCamera.ViewPosition == position))
            {
                this = default;
                return;
            }
            if (current.SceneRenderType != scene)
            {
                // Finish the inward gesture at a map boundary so its remaining steps cannot dive into the new view.
                if (distance > 0)
                {
                    this = default;
                    return;
                }
                float ratio = divisor / current.CurrentGraphicsNodes.CoordinateDivisor;
                remainingDistance *= ratio;
                coastDistance *= ratio;
            }
            Remember(current, updatedCamera);
        }

        internal bool Matches(ScaledRenderer current, CameraInfo camera) => ReferenceEquals(renderer, current) &&
            current.SceneRenderType == scene && camera.ViewPosition == position;

        internal void FollowPan(ScaledRenderer current, CameraInfo camera)
        {
            if (ReferenceEquals(renderer, current) && current.SceneRenderType == scene &&
                ReferenceEquals(current.CurrentCamera, camera))
                position = camera.ViewPosition;
        }

        private void Remember(ScaledRenderer current, CameraInfo camera)
        {
            renderer = current;
            scene = current.SceneRenderType;
            position = camera.ViewPosition;
        }
    }
}
