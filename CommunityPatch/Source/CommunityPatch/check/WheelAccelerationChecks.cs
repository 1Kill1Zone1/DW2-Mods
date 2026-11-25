using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;

internal static class WheelAccelerationChecks
{
    private static long tick;
    private static bool inputAllowed = true;

    internal static void Run(Type camera, ScaledRenderer renderer, MethodInfo wheel, Harmony harness, StarSystem system)
    {
        BurstChecks(camera);
        AccessTools.Field(camera, "wheelRamp").SetValue(null, .5f);

        var scene = AccessTools.Field(typeof(ScaledRenderer), "_SceneRenderType");
        var limit = AccessTools.Field(camera, "wheelLimit");
        var systemPreference = AccessTools.Field(camera, "systemSpeed");
        var galaxyPreference = AccessTools.Field(camera, "galaxySpeed");
        scene.SetValue(renderer, SceneRenderType.System);
        systemPreference.SetValue(null, 1f);
        var speed = AccessTools.MethodDelegate<Func<float, ScaledRenderer, float, float>>(AccessTools.Method(camera, "SelectWheelSpeed"));
        limit.SetValue(null, 4f);
        Measure(speed, renderer);

        var reset = AccessTools.Field(camera, "wheelBurst");
        var burstType = reset.FieldType;
        var leaves = new Harmony("test.community-patch.wheel-transition");
        leaves.Patch(AccessTools.Method(camera, "SelectWheelSpeed"),
            transpiler: new HarmonyMethod(typeof(WheelAccelerationChecks), nameof(UseTestClock)));
        foreach (var entry in new[]
        {
            AccessTools.Method(typeof(ScaledRenderer), "Zoom", [typeof(float), typeof(double), typeof(float), typeof(int), typeof(int)]),
            AccessTools.PropertyGetter(typeof(ScaledRenderer), "CurrentCamera"),
            AccessTools.PropertyGetter(typeof(ScaledRenderer), "CurrentGraphicsNodes"),
        })
            harness.Unpatch(entry, HarmonyPatchType.Prefix, harness.Id);
        leaves.Patch(AccessTools.Method(typeof(ScaledRenderer), "SetScene", [typeof(SceneRenderType)]),
            prefix: new HarmonyMethod(typeof(WheelAccelerationChecks), nameof(Skip)));
        leaves.Patch(AccessTools.Method(typeof(SystemSummary), "BindData",
            [typeof(Galaxy), typeof(Empire), typeof(bool), typeof(StarSystem), typeof(Location), typeof(Stride.Graphics.Texture[]),
                typeof(EventHandler<DWEventArgs>), typeof(EventHandler<DWEventArgs>), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(WheelAccelerationChecks), nameof(Skip)));

        var systemCamera = new CameraInfo();
        var galaxyCamera = new CameraInfo();
        systemCamera.SetDirection(new(0, -1, 0), new(0, 0, 1));
        galaxyCamera.SetDirection(new(0, -1, 0), new(0, 0, 1));
        var systemNodes = (GraphicsNodeList)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsNodeList));
        var galaxyNodes = (GraphicsNodeList)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsNodeList));
        systemNodes.CoordinateDivisor = 1000;
        galaxyNodes.CoordinateDivisor = 1000;
        void Set(string key, object value) => AccessTools.Field(typeof(ScaledRenderer), key).SetValue(renderer, value);
        Set("_GraphicsNodesSystem", systemNodes);
        Set("_GraphicsNodesGalaxy", galaxyNodes);
        Set("_CameraSystem", systemCamera);
        Set("_CameraGalaxy", galaxyCamera);
        Set("_MinY", 30f);
        Set("_MaxY", 2_000_000f);
        Set("_LocationZoomTransitionStart", 50_000L);
        Set("_SystemZoomTransitionStart", 10_000_000L);
        Set("_GalaxyZoomTransitionStart", 10_000_000L);
        Set("_Player", RuntimeHelpers.GetUninitializedObject(typeof(GameClient)));
        Set("CurrentSystem", system);
        AccessTools.Field(typeof(DWRendererBase), "SystemSummary").SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(SystemSummary)));

        int ToGalaxy(float acceleration)
        {
            limit.SetValue(null, acceleration);
            reset.SetValue(null, Activator.CreateInstance(burstType));
            scene.SetValue(renderer, SceneRenderType.System);
            systemNodes.OriginX = system.GalaxyX;
            systemNodes.OriginZ = system.GalaxyY;
            systemCamera.SetPosition(0, 4100, 0);
            tick = 1000;
            for (int steps = 1; steps <= 7; steps++)
            {
                wheel.Invoke(renderer, [-240f, new Point(320, 250), .015f, 1f]);
                if (renderer.SceneRenderType == SceneRenderType.Galaxy) return steps;
                tick += 55;
            }
            throw new InvalidOperationException("Native zoom did not reach the galaxy map");
        }
        int normal = ToGalaxy(0);
        int fast = ToGalaxy(4);
        Require(normal == 4 && fast == 3, $"native transition from the same camera height took {normal} normal steps and {fast} fast steps");
        galaxyPreference.SetValue(null, 2f);
        tick += 55;
        Require(speed(1f, renderer, -240f) == 2f, "the first wheel step after the actual galaxy transition uses the galaxy base speed");
        SmoothingChecks(camera, renderer, wheel, systemCamera, scene, limit);
        leaves.UnpatchAll(leaves.Id);
    }

    private static void SmoothingChecks(Type type, ScaledRenderer renderer, MethodInfo wheel, CameraInfo camera,
        FieldInfo scene, FieldInfo acceleration)
    {
        acceleration.SetValue(null, 0f);
        var duration = AccessTools.Field(type, "wheelSmoothingMs");
        var inertia = AccessTools.Field(type, "wheelCoast");
        var motion = AccessTools.Field(type, "wheelMotion");
        var advance = AccessTools.Method(motion.FieldType, "Tick");
        var active = AccessTools.PropertyGetter(motion.FieldType, "Active");
        var pan = AccessTools.Method(typeof(ScaledRenderer), "DoMouseMove", [typeof(Vector2)]);
        AccessTools.Field(typeof(DWRendererBase), "Viewport").SetValue(null, new Viewport(0, 0, 640, 500));

        void Reset(float milliseconds, float coast = 0, float height = 4100)
        {
            scene.SetValue(renderer, SceneRenderType.System);
            camera.SetPosition(0, height, 0);
            duration.SetValue(null, milliseconds);
            inertia.SetValue(null, coast);
            motion.SetValue(null, Activator.CreateInstance(motion.FieldType));
        }
        float Height() => renderer.CurrentCamera.ViewPosition.Y;
        void Wheel(float delta = -240) => wheel.Invoke(renderer, [delta, new Point(320, 250), .015f, 1f]);
        bool Moving() => (bool)active.Invoke(motion.GetValue(null), [])!;
        void Frame(float ms)
        {
            object state = motion.GetValue(null)!;
            advance.Invoke(state, [renderer, ms, 1f, duration.GetValue(null)!]);
            motion.SetValue(null, state);
        }
        void Finish(float frameMs)
        {
            for (int frame = 0; frame < 60 && Moving(); frame++) Frame(frameMs);
            Require(!Moving(), "wheel motion finishes within its duration");
        }
        bool Near(float a, float b) => Math.Abs(a - b) < .015f;

        Reset(0);
        Wheel();
        float single = Height();
        Wheel();
        float twice = Height();
        Require(single > 4100 && twice > single && !Moving(), "Off applies each step immediately");
        foreach (var ms in new[] { 120f, 220f, 320f, 450f })
        {
            Reset(ms);
            Wheel();
            Frame(16);
            Require(Height() > 4100 && Height() < single, $"{ms} ms mode moves on the first frame and keeps a tail");
            Finish(16);
            Require(Near(Height(), single), $"{ms} ms mode reaches the native one-step scale");
            Reset(ms);
            Wheel();
            Frame(33);
            Wheel();
            Finish(33);
            Require(Near(Height(), twice), $"{ms} ms mode reaches the native two-step scale at 30 FPS");
        }

        foreach (var map in new[] { SceneRenderType.System, SceneRenderType.Galaxy })
        foreach (var direction in new[] { -240f, 240f })
        {
            Reset(320, coast: .5f);
            scene.SetValue(renderer, map);
            var current = renderer.CurrentCamera;
            current.SetPosition(0, map == SceneRenderType.Galaxy ? 80_000 : 4100, 0);
            Wheel(direction);
            Frame(16);
            float afterFirstZoom = Height();
            for (int i = 0; i < 4; i++)
            {
                var beforePan = current.ViewPosition;
                pan.Invoke(renderer, [new Vector2(18, 13)]);
                Require(current.ViewPosition.X != beforePan.X && current.ViewPosition.Z != beforePan.Z,
                    $"{map} native WASD movement pans on both axes during wheel smoothing");
                Frame(16);
                Require(Moving() && Math.Sign(Height() - afterFirstZoom) == -Math.Sign(direction),
                    $"{map} smoothing keeps zooming after native WASD movement, direction {direction}");
            }
            Finish(16);
            Require(renderer.SceneRenderType == map &&
                current.ViewPosition.X != 0 && current.ViewPosition.Z != 0,
                $"{map} smoothing retains the pan displacement at the end of the gesture");
        }

        Reset(220, coast: .25f);
        Wheel();
        Finish(8);
        Require(Near(Height(), single), "Inertia leaves a single wheel step precise");
        Reset(220, coast: .25f);
        Wheel();
        Frame(48);
        Wheel();
        Finish(8);
        Require(Height() > twice && Height() < twice * 1.08f, "Inertia adds a bounded extra movement after a fast repeat");
        float previous = twice;
        foreach (var coast in new[] { 0f, .25f, .5f, .75f })
        {
            Reset(450, coast);
            Wheel();
            Frame(48);
            Wheel();
            Finish(8);
            Require(coast == 0 ? Near(Height(), twice) : Height() > previous && Height() < twice * 1.25f,
                $"the {(int)(coast * 100)}% coast gives bounded travel distinct from the previous setting");
            previous = Height();
        }

        Reset(220, coast: .25f);
        Wheel();
        Frame(16);
        float beforeReverse = Height();
        Wheel(240);
        Frame(16);
        Require(Height() < beforeReverse, "a reversed wheel step changes direction on the first frame");
        camera.SetPosition(0, 5000, 0);
        Frame(16);
        Require(!Moving() && Height() == 5000, "a separate camera move clears the pending wheel motion");

        Reset(220, coast: .25f);
        Wheel();
        AccessTools.Method(typeof(ScaledRenderer), "ZoomOut", [typeof(float)]).Invoke(renderer, [.015f]);
        Require(!Moving(), "keyboard zoom clears the wheel tail and acts independently");

        Reset(0, height: 9600);
        Wheel();
        Require(renderer.SceneRenderType == SceneRenderType.Galaxy, "the native reference wheel crosses to the galaxy map");
        float galacticHeight = Height();
        Reset(220, height: 9600);
        Wheel();
        Finish(16);
        Require(renderer.SceneRenderType == SceneRenderType.Galaxy && Near(Height(), galacticHeight),
            "the smooth wheel uses the native transition and preserves its destination scale");
        Reset(220, height: 9600);
        Wheel();
        pan.Invoke(renderer, [new Vector2(18, 13)]);
        Finish(16);
        Require(renderer.SceneRenderType == SceneRenderType.Galaxy && Near(Height(), galacticHeight),
            "panning during the system-to-galaxy transition preserves the wheel destination scale");

        var inputType = AccessTools.TypeByName("DistantWorlds2.InputSystem");
        var input = RuntimeHelpers.GetUninitializedObject(inputType);
        AccessTools.Field(inputType, "_Renderer").SetValue(input, renderer);
        var update = AccessTools.Method(inputType, "Update",
            [typeof(InputManager), typeof(GameSettings), typeof(GameTime), typeof(float), typeof(float)]);
        var mouse = RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
        GC.SuppressFinalize(mouse);
        var clock = Activator.CreateInstance<GameTime>();
        AccessTools.PropertySetter(typeof(GameTime), "Elapsed").Invoke(clock, [TimeSpan.FromMilliseconds(16)]);
        var simulatedInput = new Harmony("test.community-patch.wheel-input");
        simulatedInput.Patch(update, prefix: new HarmonyMethod(typeof(WheelAccelerationChecks), nameof(Skip)));
        foreach (var method in new[]
        {
            AccessTools.Method(typeof(UserInterfaceController), "CheckCanProcessEvent", [typeof(GameSettings)]),
            AccessTools.Method(typeof(ScaledRenderer), "CanPanZoomView", Type.EmptyTypes),
        })
            simulatedInput.Patch(method, prefix: new HarmonyMethod(typeof(WheelAccelerationChecks), nameof(AllowInput)));
        try
        {
            Reset(120);
            Wheel();
            update.Invoke(input, [mouse, new GameSettings(), clock, .016f, 1f]);
            float afterInputFrame = Height();
            Require(afterInputFrame > 4100 && afterInputFrame < single, "the registered frame callback advances the wheel zoom");
            inputAllowed = false;
            update.Invoke(input, [mouse, new GameSettings(), clock, .016f, 1f]);
            Require(!Moving() && Height() == afterInputFrame, "a blocking UI stops the wheel tail");
            inputAllowed = true;
            Reset(120);
            var nativeWheel = AccessTools.MethodDelegate<Action<ScaledRenderer, float, Point, float, float>>(wheel);
            var frame = AccessTools.MethodDelegate<Action<InputManager, GameSettings, GameTime, float, ScaledRenderer>>(
                AccessTools.Method(type, "AfterInputUpdate"));
            var settings = new GameSettings();
            void Exercise(int count)
            {
                for (int burst = 0; burst < count; burst++)
                {
                    camera.SetPosition(0, 4100, 0);
                    nativeWheel(renderer, -240f, new Point(320, 250), .015f, 1f);
                    for (int i = 0; i < 8; i++) frame((InputManager)mouse, settings, clock, 1f, renderer);
                }
            }
            const int bursts = 2000;
            void MeasureMode(float milliseconds, string label)
            {
                Reset(milliseconds);
                Exercise(500);
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                Exercise(bursts);
                double microseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000 / bursts;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                Require(!Moving() && Near(Height(), single), $"{label} performance sample reached the expected final scale");
                Console.WriteLine($"MEASURE {label}: {microseconds:F2} microseconds and {bytes / bursts} allocated bytes per headless gesture ({bursts} gestures)");
            }
            MeasureMode(0, "Off");
            MeasureMode(120, "Short");
        }
        finally
        {
            simulatedInput.UnpatchAll(simulatedInput.Id);
            inputAllowed = true;
        }
        CloseRangeChecks(type, renderer, wheel, scene, camera);
        Reset(0);
    }

    private static SceneRenderType? testWheelTransition;

    private static bool SubstituteCloseRangeTransition(ScaledRenderer __instance)
    {
        if (testWheelTransition is { } target)
            AccessTools.Field(typeof(ScaledRenderer), "_SceneRenderType").SetValue(__instance, target);
        return false;
    }

    private static void CloseRangeChecks(Type type, ScaledRenderer renderer, MethodInfo wheel,
        FieldInfo scene, CameraInfo systemCamera)
    {
        var motion = AccessTools.Field(type, "wheelMotion");
        var advance = AccessTools.Method(motion.FieldType, "Tick");
        var active = AccessTools.PropertyGetter(motion.FieldType, "Active");
        var duration = AccessTools.Field(type, "wheelSmoothingMs");
        var coast = AccessTools.Field(type, "wheelCoast");
        AccessTools.Field(type, "systemSpeed").SetValue(null, 3f);
        AccessTools.Field(type, "wheelLimit").SetValue(null, 2f);
        AccessTools.Field(type, "wheelRamp").SetValue(null, 1f);
        var location = new CameraInfo();
        location.SetDirection(new(0, -1, 0), new(0, 0, 1));
        var locationNodes = (GraphicsNodeList)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsNodeList));
        locationNodes.CoordinateDivisor = 1;
        AccessTools.Field(typeof(ScaledRenderer), "_CameraLocation").SetValue(renderer, location);
        AccessTools.Field(typeof(ScaledRenderer), "_GraphicsNodesLocation").SetValue(renderer, locationNodes);
        var systemNodes = (GraphicsNodeList)AccessTools.Field(typeof(ScaledRenderer), "_GraphicsNodesSystem").GetValue(renderer)!;
        float savedDivisor = systemNodes.CoordinateDivisor;
        systemNodes.CoordinateDivisor = 25;
        // Compare native camera motion at the same scale. The earlier checks cover actual scene transitions.
        var noRebase = new Harmony("test.community-patch.wheel-close-range");
        noRebase.Patch(AccessTools.Method(typeof(ScaledRenderer), "CheckChangeScene", [typeof(CameraInfo), typeof(Vector3)]),
            prefix: new HarmonyMethod(typeof(WheelAccelerationChecks), nameof(SubstituteCloseRangeTransition)));
        try
        {
            void Reset(SceneRenderType view, float height, float smoothing = 320, float inertia = .5f)
            {
                scene.SetValue(renderer, view);
                (view == SceneRenderType.Location ? location : systemCamera).SetPosition(0, height, 0);
                duration.SetValue(null, smoothing);
                coast.SetValue(null, inertia);
                motion.SetValue(null, Activator.CreateInstance(motion.FieldType));
            }
            float Height() => renderer.CurrentCamera.ViewPosition.Y;
            void Wheel(float delta)
            {
                tick += 20;
                wheel.Invoke(renderer, [delta, new Point(320, 250), .015f, 1f]);
            }
            bool Moving() => (bool)active.Invoke(motion.GetValue(null), [])!;
            void Frame()
            {
                object state = motion.GetValue(null)!;
                advance.Invoke(state, [renderer, 16f, 1f, (float)duration.GetValue(null)!]);
                motion.SetValue(null, state);
            }
            void Finish()
            {
                for (int n = 0; n < 25 && Moving(); n++) Frame();
                Require(!Moving(), "close-range smoothing settles within its configured duration");
            }
            float Burst()
            {
                for (int n = 0; n < 4; n++) { Wheel(240); Frame(); }
                Finish();
                return Height();
            }
            Reset(SceneRenderType.Location, 3200, smoothing: 0);
            float stockBurst = Burst();
            Reset(SceneRenderType.Location, 3200);
            float gentleBurst = Burst();
            Require(stockBurst < 90 && gentleBurst > 300 && gentleBurst < 800,
                $"3x / Inertia 320 ms / 50% moderates four close zoom-in notches: stock {stockBurst:F1}, tuned {gentleBurst:F1}");
            Reset(SceneRenderType.Location, 800, inertia: 0);
            float withoutCoast = Burst();
            Reset(SceneRenderType.Location, 800);
            Require(Math.Abs(Burst() - withoutCoast) < .05f, "close inward bursts have no extra coast");
            float SystemScroll(float inertia, int count, bool batched = false, float height = 4100)
            {
                Reset(SceneRenderType.System, height, inertia: inertia);
                if (batched) Wheel(240 * count);
                else for (int step = 0; step < count; step++) Wheel(240);
                Finish();
                return Height();
            }
            float oneStep = SystemScroll(.5f, 1);
            float systemBurst = SystemScroll(.5f, 4);
            Require(oneStep >= 4100 * .78f - .05f && 4100 - systemBurst > 2 * (4100 - oneStep),
                $"system approach from 4100: one notch ends at {oneStep:F1}, four at {systemBurst:F1}");
            Require(Math.Abs(SystemScroll(.5f, 4, batched: true) - systemBurst) < .1f &&
                Math.Abs(SystemScroll(0, 4) - systemBurst) < .05f,
                "batched input reproduces the four-notch approach without extra inward coast");
            float nearSingle = SystemScroll(.5f, 1, height: 60);
            float nearBurst = SystemScroll(.5f, 4, height: 60);
            Require(nearSingle < 60 && nearSingle >= 60 * .78f - .05f && Math.Abs(nearSingle - nearBurst) < .05f,
                $"near the planet: one notch ends at {nearSingle:F1}, a queued burst at {nearBurst:F1} from 60");
            Require(SystemScroll(.5f, 32, batched: true) >= 64 * .78f - .05f,
                "a large single wheel event cannot skip the near-planet precision zone");
            Reset(SceneRenderType.System, 4100);
            Wheel(960);
            testWheelTransition = SceneRenderType.Location;
            Frame();
            testWheelTransition = null;
            Require(renderer.SceneRenderType == SceneRenderType.Location && !Moving(),
                "an inward scene change releases the remaining wheel movement");
            Reset(SceneRenderType.Location, 800);
            for (int n = 0; n < 6; n++)
            {
                Wheel(240);
                Finish();
            }
            Require(Math.Abs(Height() - 30) < .05f, $"deliberate single notches still reach native close zoom: {Height():F1}");
            Wheel(240);
            Finish();
            Require(Math.Abs(Height() - 30) < .05f, "an extra smoothed notch cannot pass the native minimum height");
            foreach (var view in new[] { SceneRenderType.Location, SceneRenderType.System })
            {
                Reset(view, 30, smoothing: 0);
                Wheel(-240);
                float stockOut = Height();
                Reset(view, 30);
                Wheel(-240);
                Frame();
                Require(Height() > 30, $"{view} zoom-out starts moving on its first frame");
                Finish();
                Require(Height() > stockOut + 4, $"{view} exits deep zoom faster: stock {stockOut:F1}, tuned {Height():F1}");
            }
            Reset(SceneRenderType.Location, 800);
            Wheel(240);
            Frame();
            float beforeReverse = Height();
            Wheel(-240);
            Frame();
            Require(Height() > beforeReverse, "close reversal immediately cancels the inbound tail");
            AccessTools.Field(type, "systemSpeed").SetValue(null, 2f);
            AccessTools.Field(type, "wheelLimit").SetValue(null, 3f);
            AccessTools.Field(type, "wheelRamp").SetValue(null, .5f);
            Reset(SceneRenderType.System, 4100, smoothing: 220);
            tick += 300;
            Wheel(240); Finish();
            float personalSingle = Height();
            Reset(SceneRenderType.System, 4100, smoothing: 220);
            tick += 300;
            float personalBurst = Burst();
            Require(4100 - personalBurst > 2 * (4100 - personalSingle),
                $"2x system, 3x acceleration, 220 ms inertia: one notch {personalSingle:F1}, four with intervening frames {personalBurst:F1}");
        }
        finally
        {
            testWheelTransition = null;
            systemNodes.CoordinateDivisor = savedDivisor;
            noRebase.UnpatchAll(noRebase.Id);
        }
    }

    private static void BurstChecks(Type camera)
    {
        var type = camera.GetNestedType("WheelBurst", BindingFlags.NonPublic)!;
        var advance = AccessTools.Method(type, "Advance");
        object burst = Activator.CreateInstance(type)!;
        float Step(long now, float delta = 240, bool galaxy = false, float speed = 1, float limit = 4, float ramp = .5f)
            => (float)advance.Invoke(burst, [speed, limit, galaxy, delta, now, ramp])!;
        var readRamp = AccessTools.Method(camera, "ReadWheelRamp");
        foreach (var (name, second) in new[] { ("Gentle", 1.75f), ("Standard", 2.5f), ("Fast", 4f) })
        {
            float ramp = (float)readRamp.Invoke(null, [System.Text.Json.Nodes.JsonValue.Create(name)])!;
            burst = Activator.CreateInstance(type)!;
            Require(Step(1000, ramp: ramp) == 1 && Step(1050, ramp: ramp) == second,
                $"{name} acceleration has the expected second-step speed");
        }
        burst = Activator.CreateInstance(type)!;
        Require(Step(1000) == 1 && Step(1250) == 1 && Step(1450) == 1, "slow steps stay at base speed");
        Require(Step(1500) == 2.5f && Step(1555) == 4 && Step(1605) == 4, "rapid steps ramp from 1x to 4x and stay capped");
        Require(Step(1620, -240) == 1 && Step(1670, -240) == 2.5f, "reversing direction starts a new burst");
        Require(Step(1680, -240, galaxy: true, speed: 2) == 2, "crossing to the galaxy map uses its base speed");
        Require(Step(1700, -240, galaxy: true, speed: 2, limit: 3) == 2.5f, "3x can be selected independently of the base");
        Require(Step(1750, -240, galaxy: true, speed: 2, limit: 3) == 3, "3x limit clamps the burst");
        Require(Step(1800, speed: 4, limit: 2) == 4, "a 2x acceleration limit cannot lower a 4x base");
        burst = Activator.CreateInstance(type)!;
        Require(Step(2000, 24) == 1 && Math.Abs(Step(2050, 24) - 1.15f) < .001f, "small high-resolution scroll deltas keep fine control");
        burst = Activator.CreateInstance(type)!;
        Require(Step(2000) == 1 && Step(2180) is > 1 and < 1.3f, "slower cadence gains a smaller boost before the pause threshold");
    }

    private static void Measure(Func<float, ScaledRenderer, float, float> speed, ScaledRenderer renderer)
    {
        const int samples = 250_000;
        float total = 0;
        for (int i = 0; i < 10_000; i++) total += speed(1f, renderer, 240f);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) total += speed(1f, renderer, 240f);
        double elapsedNs = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1_000_000 / samples;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Require(total >= samples && bytes == 0, $"wheel acceleration allocated {bytes} bytes across {samples} calls at {elapsedNs:F1} ns per call in isolation");
    }

    private static IEnumerable<CodeInstruction> UseTestClock(IEnumerable<CodeInstruction> instructions)
    {
        var getTick = AccessTools.PropertyGetter(typeof(Environment), nameof(Environment.TickCount64));
        foreach (var code in instructions)
        {
            if (code.Calls(getTick))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(WheelAccelerationChecks), nameof(TestTick));
            }
            yield return code;
        }
    }

    private static long TestTick() => tick;
    private static bool Skip() => false;
    private static bool AllowInput(ref bool __result) { __result = inputAllowed; return false; }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
