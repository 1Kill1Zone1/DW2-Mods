using System.Reflection;
using LocalModUi;
using DistantWorlds2;
using DistantWorlds.Core;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Input;

namespace BattleLog;

public static class Entry
{
    static readonly Harmony Hooks = new("local.dw2.battle-log");
    static Panel? panel; static DWButton? mapButton; static Galaxy? uiGalaxy; static short uiObserver = -1; static bool initialized;
    [ThreadStatic] static FireState? activeFire;
    [ThreadStatic] static BlastTickState? activeTick;
    static readonly object trackedGate = new();
    static readonly Dictionary<WeaponBlast, TrackedShot> trackedShots = new(ReferenceEqualityComparer.Instance);
    static Galaxy? lastTrackedPruneCampaign;
    static long lastTrackedPruneAt = long.MinValue;
    public static void Init()
    {
        if (initialized) return; initialized = true;
        NativeToolDock.Install(Hooks);
        var central = typeof(Galaxy).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == nameof(Galaxy.InflictDamage) && IsCentralDamage(m));
        var fire = AccessTools.Method(typeof(Weapon), nameof(Weapon.Fire), [typeof(Galaxy), typeof(Location), typeof(StellarObject), typeof(ShipHull), typeof(StellarObject), typeof(DateTime), typeof(Random), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(float)]);
        if (fire == null) throw new MissingMethodException("Weapon.Fire exact current-GOG overload");
        var chance = typeof(Weapon).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == nameof(Weapon.DetermineWillHitTarget) && m.GetParameters().Length == 11);
        var doTasks = AccessTools.Method(typeof(WeaponBlast), "DoTasks", [typeof(DateTime), typeof(Galaxy), typeof(Location), typeof(bool), typeof(ComponentStats), typeof(float), typeof(WeaponFireType), typeof(float), typeof(float), typeof(float), typeof(float)]);
        if (doTasks == null) throw new MissingMethodException("WeaponBlast.DoTasks exact current-GOG overload");
        Hooks.Patch(fire, prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeFire)), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterFire)), finalizer: new HarmonyMethod(typeof(Entry), nameof(RestoreFire)));
        Hooks.Patch(chance, prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeHitChance)));
        Hooks.Patch(doTasks, prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeBlastTick)), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterBlastTick)), finalizer: new HarmonyMethod(typeof(Entry), nameof(RestoreBlastTick)));
        Hooks.Patch(AccessTools.Method(typeof(Location), nameof(Location.CheckAddItem), [typeof(StellarObject)]), postfix: new HarmonyMethod(typeof(Entry), nameof(AddedBlast)));
        Hooks.Patch(central, prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeDamage)), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterDamage)));
        Hooks.Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.WriteToStream)), prefix: new HarmonyMethod(typeof(Entry), nameof(StoreBeforeSerialization)) { priority = Priority.First });
        Hooks.Patch(AccessTools.Method(typeof(UserInterfaceController), "Update"), postfix: new HarmonyMethod(typeof(Entry), nameof(UpdateUi)));
        Hooks.Patch(AccessTools.Method(typeof(UserInterfaceController), nameof(UserInterfaceController.DoMouseDown)), prefix: new HarmonyMethod(typeof(MapControl), nameof(MapControl.ClearPress)));
        Hooks.Patch(AccessTools.Method(typeof(InputSystem), nameof(InputSystem.HandleKeyPress)), prefix: new HarmonyMethod(typeof(Entry), nameof(AllowGameKey)));
    }
    static bool IsCentralDamage(MethodInfo method)
    {
        var p = method.GetParameters();
        return p.Length == 34 && p[0].ParameterType == typeof(Galaxy) && p[1].ParameterType == typeof(StellarObject) && p[2].ParameterType == typeof(StellarObject) && p[28].ParameterType == typeof(DateTime) && p[30].ParameterType == typeof(int) && p[31].ParameterType == typeof(ComponentStats) && p[32].ParameterType == typeof(ComponentDefinition);
    }
    // Fire may exit without creating a blast. Count only a WeaponBlast that was
    // actually appended during this call, never the attempted Fire invocation.
    static void BeforeFire(Galaxy galaxy, Location location, StellarObject firer, StellarObject target, bool? willHitOverride, out FireState __state)
    {
        __state = new FireState { Previous = activeFire, Galaxy = galaxy, Location = location, Firer = firer, Target = target, Forced = willHitOverride.HasValue }; activeFire = __state;
    }
    static void AddedBlast(Location __instance, StellarObject stellarObject)
    {
        if (activeFire is { } state && ReferenceEquals(state.Location, __instance) && stellarObject is WeaponBlast blast &&
            ReferenceEquals(__instance.WeaponBlasts.GetById(blast.WeaponBlastId), blast)) state.Shot = blast;
    }
    static Exception? RestoreFire(Exception? __exception, FireState? __state)
    {
        if (__state != null) activeFire = __state.Previous;
        return __exception;
    }
    // Exact native-equivalent expectation from the static helper inputs. This
    // prefix observes arguments before its RNG calls and never invokes RNG.
    static void BeforeHitChance(Galaxy galaxy, float weaponRange, float targetSize, float targetSpeed, float distance, float targeting, float countermeasures, float rangeChanceMinimum, float rangeChanceMaximum, float rateAngleFactor, float cockpitFactor)
    {
        if (activeFire != null) activeFire.ExpectedChance = ExpectedChance(weaponRange, targetSpeed, distance, targeting, countermeasures, rangeChanceMinimum, rangeChanceMaximum, rateAngleFactor, cockpitFactor);
    }
    static float ExpectedChance(float weaponRange, float targetSpeed, float distance, float targeting, float countermeasures, float minimum, float maximum, float rateAngle, float cockpit)
    {
        var range = maximum + Math.Max(0f, (minimum - maximum) * ((weaponRange - distance) / Math.Max(0f, weaponRange)));
        var speed = .8f + .7f * (1f - Math.Max(0f, Math.Min(1f, targetSpeed / 50f)));
        var score = range * speed * (1f + (targeting - countermeasures));
        if (cockpit > 1f) rateAngle *= cockpit * 2f;
        // These are deliberately separate float operations, matching the
        // decrypted native IL rounding order used by the validated probe.
        score *= rateAngle;
        score *= cockpit;
        var firstPass = score > 0f ? score < 1f ? score : 1f : 0f;
        return 1f / 12f + 10f / 12f * firstPass;
    }
    internal static bool IncludesCombat(ComponentStats? stats, StellarObject? target, bool bombardment = false) =>
        !bombardment && target is not Orb && !(stats?.TractorBeamPower > 0f);

    static void AfterFire(Weapon __instance, Galaxy galaxy, Location location, StellarObject firer, StellarObject target, DateTime time, bool intercepting, bool bombardment, FireState? __state)
    {
        try { if (!IncludesCombat(__instance.ComponentStatsCached, target, bombardment) || __state == null || location == null) return;
            // Native blasts reuse both slots and IDs. Capture the actual registration in this Fire call, not Count growth.
            var shot = __state.Shot;
            if (shot == null || shot.Firer != firer || shot.Target != target || shot.FiringWeaponComponentId != __instance.ComponentId) return;
            var cadence = Math.Max(intercepting ? __instance.InterceptFireRate : __instance.FireRate, __instance.ComponentStatsCached?.WeaponVolleyFireRate ?? 0);
            var targetKnown = TargetDetailVisible(galaxy, target);
            var area = shot.ComponentStats?.WeaponDamageType == WeaponDamageType.AreaEffect;
            var key = Ledger.RegisterLaunch(galaxy, location, firer, target, __instance.ComponentId, __instance.Component?.Name, time, intercepting, cadence, shot.FireType, area, __state.Forced ? null : __state.ExpectedChance, __state.Forced, targetKnown);
            if (key is Ledger.DetailKey detail) lock (trackedGate) trackedShots[shot] = new TrackedShot(detail, __state.Forced, shot.FireType, area, galaxy, galaxy.Empires.GetPlayer()?.EmpireId ?? (short)-1, firer); }
        catch (Exception ex) { Ledger.LatchFault(galaxy, ex); }
    }
    static bool TargetDetailVisible(Galaxy galaxy, StellarObject target)
    {
        var player = galaxy.Empires.GetPlayer();
        return target is Ship ship && player != null && (ship.GetEmpireId() == player.EmpireId || player.VisibilityMap.DetermineVisibilityTypeForShipAllEmpires(ship, player, galaxy, galaxy.StarDate) == VisibilityType.FullyVisible);
    }
    static void BeforeDamage(Galaxy galaxy, StellarObject firer, StellarObject target, float rawDamage, DateTime time, int weaponComponentId, ComponentStats weaponComponentStats, ComponentDefinition weaponComponent, out DamageSnapshot? __state)
    {
        try { __state = IncludesCombat(weaponComponentStats, target) && Ledger.TryEnsure(galaxy) && Ledger.CanObserve(galaxy, firer, target) ? DamageSnapshot.TakeTracked(galaxy, target, weaponComponentId, weaponComponent, firer, time, rawDamage, activeTick?.Tracked?.Key) : null; }
        catch (Exception ex) { Ledger.LatchFault(galaxy, ex); __state = null; }
    }
    static void AfterDamage(DamageSnapshot? __state)
    {
        try
        {
            if (__state == null) return;
            var detailed = Ledger.Hit(__state);
            if (__state.Ordnance != null && __state.Ordnance.IsDestroyed) ResolveTracked(__state.Ordnance, Ledger.ProjectileOutcome.Intercepted);
            if (activeTick?.Tracked is not { } tracked || !Ledger.IsCurrent(tracked.Campaign, tracked.Observer) ||
                __state.Side != tracked.Key.Side || __state.ComponentId != tracked.Key.ComponentId) return;
            if (tracked.Area) { if (detailed) Ledger.AreaTarget(tracked.Key); }
            else if (tracked.FireType == WeaponFireType.FullLengthBeam) { if (detailed) Ledger.BeamTick(tracked.Key); }
            else if (__state.IsShipImpact) activeTick!.ResolvedHit = true;
        }
        catch (Exception ex) { Ledger.LatchActiveFault(ex); }
    }
    static void BeforeBlastTick(WeaponBlast __instance, Galaxy galaxy, out BlastTickState? __state)
    {
        TrackedShot? tracked = null;
        lock (trackedGate) if (trackedShots.TryGetValue(__instance, out var found)) tracked = found;
        // Most native blasts are not ours or are intentionally redacted.  Do
        // not allocate or query targets on their hot path.  A nested untracked
        // call temporarily suspends a tracked outer context so its damage can
        // never be attributed to the outer projectile.
        if (tracked == null)
        {
            if (activeTick == null) { __state = null; return; }
            __state = new BlastTickState { Previous = activeTick };
            activeTick = null;
            return;
        }
        var target = __instance.GetTarget(galaxy);
        __state = new BlastTickState { Previous = activeTick, Blast = __instance, Tracked = tracked, Target = target, TargetWasAlive = target != null && !target.IsDestroyed, HadMissedTarget = __instance.HasMissedTarget };
        activeTick = __state;
    }
    static void AfterBlastTick(WeaponBlast __instance, Galaxy galaxy, BlastTickState? __state)
    {
        if (__state == null) return;
        try
        {
            var tracked = __state.Tracked; if (tracked == null || tracked.Area || tracked.FireType == WeaponFireType.FullLengthBeam) return;
            var terminal = __state.ResolvedHit || !__state.HadMissedTarget && __instance.HasMissedTarget;
            if (terminal && (!Ledger.IsCurrent(tracked.Campaign, tracked.Observer) || !Ledger.CanObserve(galaxy, tracked.Firer, __state.Target)))
            {
                ForgetTracked(__instance); return;
            }
            if (__state.ResolvedHit) ResolveTracked(__instance, Ledger.ProjectileOutcome.Hit);
            else if (!__state.HadMissedTarget && __instance.HasMissedTarget)
            {
                // A missing/destroyed target is native cancellation, not evasive miss.
                var lost = !__state.TargetWasAlive || __state.Target == null || __state.Target.IsDestroyed;
                ResolveTracked(__instance, lost ? Ledger.ProjectileOutcome.LostTarget : Ledger.ProjectileOutcome.Miss);
            }
        }
        finally { activeTick = __state.Previous; }
    }
    static Exception? RestoreBlastTick(Exception? __exception, BlastTickState? __state)
    {
        if (__state != null) activeTick = __state.Previous;
        return __exception;
    }
    static void ResolveTracked(WeaponBlast blast, Ledger.ProjectileOutcome outcome)
    {
        TrackedShot? tracked = null;
        lock (trackedGate)
        {
            if (!trackedShots.TryGetValue(blast, out var found)) return;
            trackedShots.Remove(blast);
            tracked = found;
        }
        if (Ledger.IsCurrent(tracked.Campaign, tracked.Observer))
        {
            if (Ledger.CanObserve(tracked.Campaign, tracked.Firer, blast.GetTarget(tracked.Campaign))) Ledger.ResolveProjectile(tracked.Key, outcome, tracked.Forced);
            else Ledger.MarkProjectileUnavailable(tracked.Key);
        }
    }
    static void ForgetTracked(WeaponBlast blast)
    {
        TrackedShot? shot;
        lock (trackedGate) { if (!trackedShots.Remove(blast, out shot)) return; }
        if (Ledger.IsCurrent(shot.Campaign, shot.Observer)) Ledger.MarkProjectileUnavailable(shot.Key);
    }
    static void StoreBeforeSerialization(Galaxy __instance) => Ledger.Persist(__instance, true);
    static void UpdateUi(int screenHeight)
    {
        SetUiVisible(false);
        var game = Galaxy.Game as DWGame;
        var current = game?.Galaxy;
        short playerId = current?.Empires.GetPlayer()?.EmpireId ?? (short)-1;
        if (!ReferenceEquals(current, uiGalaxy) || uiObserver != playerId)
        {
            ClearTrackedAsUnavailable();
            Close();
            // WeaponBlast references are valid only in their originating
            // campaign/observer.  A load/switch must not resolve old pending
            // ordnance into a newly opened encounter at the same location.
            uiGalaxy = current; uiObserver = playerId; Ledger.TryEnsure(current);
        }
        var input = game?.Input;
        if (current == null || playerId < 0 || input == null) { Close(); NativeToolDock.Detach(mapButton); return; }
        if (!Ledger.TryEnsure(current)) { Close(); NativeToolDock.Detach(mapButton); return; }
        try { Ledger.CloseQuiet(current.Time); }
        catch (Exception ex) { Ledger.LatchFault(current, ex); Close(); NativeToolDock.Detach(mapButton); return; }
        PruneTracked(current);
        if (!game!.IsActive) { panel?.CancelGesture(); MapControl.ClearPress(); return; }
        EnsureMapButton();
        if (panel != null && (!UserInterfaceController.CustomControls.TryGetValue(panel.Name, out var registered) || !ReferenceEquals(registered, panel))) Close();
        NativeToolDock.Update(mapButton!);
        if (NativeToolDock.ToolbarAvailable() && IsShortcut(input)) Toggle();
        if (!UiAvailable()) { MapControl.ClearPress(); if (panel != null) { panel.CancelGesture(); MapUiGate.ReleaseHover(panel); } return; }
        if (panel != null) panel.Visible = true;
        if (panel != null && input.IsKeyPressed(Keys.Escape)) Close();
        panel?.Place(UserInterfaceController.ScreenWidth, screenHeight);
    }
    static void SetUiVisible(bool visible) { if (mapButton != null) mapButton.Visible = visible; if (panel != null) panel.Visible = visible; }
    static void PruneTracked(Galaxy current)
    {
        var now = Environment.TickCount64;
        if (!ReferenceEquals(lastTrackedPruneCampaign, current)) { lastTrackedPruneCampaign = current; lastTrackedPruneAt = long.MinValue; }
        if (lastTrackedPruneAt != long.MinValue && now - lastTrackedPruneAt < 1000) return;
        lastTrackedPruneAt = now;
        List<TrackedShot>? stale = null;
        lock (trackedGate)
        {
            List<WeaponBlast>? staleBlasts = null;
            foreach (var pair in trackedShots)
                if (!ReferenceEquals(pair.Value.Campaign, current) || !Ledger.IsCurrent(pair.Value.Campaign, pair.Value.Observer) || pair.Key.IsDestroyed || pair.Key.HasMissedTarget ||
                    pair.Key.WeaponBlastId < 0 || !ReferenceEquals(pair.Key.GetLocation(current)?.WeaponBlasts.GetById(pair.Key.WeaponBlastId), pair.Key))
                { (stale ??= []).Add(pair.Value); (staleBlasts ??= []).Add(pair.Key); }
            if (staleBlasts != null) foreach (var blast in staleBlasts) trackedShots.Remove(blast);
        }
        if (stale != null) foreach (var shot in stale) Ledger.MarkProjectileUnavailable(shot.Key);
    }
    static void ClearTrackedAsUnavailable()
    {
        List<TrackedShot>? stale = null;
        lock (trackedGate)
        {
            if (trackedShots.Count != 0) stale = trackedShots.Values.ToList();
            trackedShots.Clear(); lastTrackedPruneCampaign = null; lastTrackedPruneAt = long.MinValue;
        }
        if (stale != null) foreach (var shot in stale) Ledger.MarkProjectileUnavailable(shot.Key);
    }
    static bool IsShortcut(InputManager input) => input.IsKeyPressed(Keys.B) && (input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl)) && (input.IsKeyDown(Keys.LeftAlt) || input.IsKeyDown(Keys.RightAlt));
    static bool AllowGameKey(KeyMapping keyMapping)
    {
        if (keyMapping?.Key == Keys.B && (Galaxy.Game as DWGame)?.Input is { } input && IsShortcut(input)) return false;
        if (!UiAvailable()) return true;
        if (keyMapping?.Key == Keys.Escape && panel is { Visible: true }) { Close(); return false; }
        return true;
    }
    static void Toggle()
    {
        if (!NativeToolDock.ToolbarAvailable() || !MapUiGate.TryGetContext(out _, out var current) ||
            !ReferenceEquals(current, uiGalaxy) || current.Empires.GetPlayer()?.EmpireId != uiObserver || Ledger.HasFault(current)) return;
        if (panel != null && NativeToolDock.Selected == NativeToolDock.Battle) { Close(); return; }
        NativeToolDock.Select(NativeToolDock.Battle);
        panel ??= new Panel(Close);
        panel.Visible = true;
        panel.Place(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
        UserInterfaceController.AddCustomControl(panel);
    }
    static void Close()
    {
        MapControl.ClearPress(); NativeToolDock.Clear(NativeToolDock.Battle);
        if (panel == null) return;
        panel.CancelGesture();
        panel.Visible = false; MapUiGate.ReleaseHover(panel);
        UserInterfaceController.RemoveCustomControl(panel); panel = null;
    }
    static void EnsureMapButton() => mapButton ??= NativeToolDock.Register(NativeToolDock.Battle, "Battle Log", "Reports from observed encounters, by side and weapon. Ctrl+Alt+B", Toggle);
    internal static bool UiAvailable() => (Galaxy.Game as DWGame)?.IsActive == true && MapUiGate.TryGetContext(out _, out var current) &&
        ReferenceEquals(current, uiGalaxy) && current.Empires.GetPlayer()?.EmpireId == uiObserver && uiObserver >= 0 &&
        !Ledger.HasFault(current) && NativeToolDock.BodyAvailable(NativeToolDock.Battle);

}

sealed class FireState { internal FireState? Previous; internal Galaxy? Galaxy; internal Location? Location; internal StellarObject? Firer; internal StellarObject? Target; internal WeaponBlast? Shot; internal bool Forced; internal float? ExpectedChance; }
sealed class TrackedShot(Ledger.DetailKey key, bool forced, WeaponFireType fireType, bool area, Galaxy campaign, short observer, StellarObject? firer = null) { internal Ledger.DetailKey Key = key; internal bool Forced = forced; internal WeaponFireType FireType = fireType; internal bool Area = area; internal Galaxy Campaign = campaign; internal short Observer = observer; internal StellarObject? Firer = firer; }
sealed class BlastTickState { internal BlastTickState? Previous; internal WeaponBlast? Blast; internal TrackedShot? Tracked; internal StellarObject? Target; internal bool TargetWasAlive; internal bool HadMissedTarget; internal bool ResolvedHit; }

public sealed record DamageSnapshot(short Side, string SideName, int ComponentId, string Name, DateTime Time, int LocationId, bool IsShipImpact, Ship? Ship, WeaponBlast? Ordnance, float OrdnancePower, float Shields, float Hull, Dictionary<byte, float>? Armor, Dictionary<byte, short>? OtherComponents, float RawDamage, bool DetailedImpact)
{
    public Galaxy? Campaign { get; init; }
    public short ObserverEmpireId { get; init; } = -1;
    internal Ledger.DetailKey? DetailKey { get; init; }
    public static DamageSnapshot Take(Galaxy galaxy, StellarObject target, int weaponId, ComponentDefinition? weapon, StellarObject? firer, DateTime time, float rawDamage = 0)
        => TakeCore(galaxy, target, weaponId, weapon, firer, time, rawDamage) with { Campaign = galaxy, ObserverEmpireId = galaxy.Empires.GetPlayer()?.EmpireId ?? (short)-1 };
    internal static DamageSnapshot TakeTracked(Galaxy galaxy, StellarObject target, int weaponId, ComponentDefinition? weapon, StellarObject? firer, DateTime time, float rawDamage, Ledger.DetailKey? detailKey)
        => Take(galaxy, target, weaponId, weapon, firer, time, rawDamage) with { DetailKey = detailKey };

    static DamageSnapshot TakeCore(Galaxy galaxy, StellarObject target, int weaponId, ComponentDefinition? weapon, StellarObject? firer, DateTime time, float rawDamage)
    {
        var source = Source(galaxy, firer, weaponId, weapon?.Name);
        var location = target.GetLocation(galaxy); var id = location?.LocationId ?? -1;
        if (target is WeaponBlast blast) return new(source.Side, source.SideName, source.Id, source.Name, time, id, false, null, blast, blast.PowerRatio, 0, 0, null, null, rawDamage, false);
        // A hidden foreign target struck by an own weapon does not divulge its
        // private component health. It contributes no damage breakdown until visible.
        var player = galaxy.Empires.GetPlayer();
        var visibleTarget = target is Ship s && player != null && (s.GetEmpireId() == player.EmpireId || player.VisibilityMap.DetermineVisibilityTypeForShipAllEmpires(s, player, galaxy, galaxy.StarDate) == VisibilityType.FullyVisible);
        if (!visibleTarget || target is not Ship ship) return new(source.Side, source.SideName, source.Id, source.Name, time, id, target is Ship, null, null, 0, 0, 0, null, null, rawDamage, false);
        var armor = new Dictionary<byte, float>();
        var other = new Dictionary<byte, short>();
        foreach (var component in ship.Components) if (!component.IsEmpty)
        {
            var capacity = Ledger.ArmorCapacity(component);
            if (capacity > 0f) armor[component.ComponentBayId] = capacity;
            else other[component.ComponentBayId] = component.Status;
        }
        var detailed = source.Id != 0 && float.IsFinite(rawDamage) && rawDamage >= 0f;
        return new(source.Side, source.SideName, source.Id, source.Name, time, id, true, ship, null, 0, ship.Shields, ship.HullDamageLevel, armor, other, rawDamage, detailed);
    }

    public bool HasUnmeasuredComponentChange
    {
        get
        {
            if (Ship == null || OtherComponents == null) return false;
            foreach (var component in Ship.Components)
                if (OtherComponents.TryGetValue(component.ComponentBayId, out var status) && component.Status != status) return true;
            return false;
        }
    }
    static (short Side, string SideName, int Id, string Name) Source(Galaxy g, StellarObject? firer, int id, string? name)
    {
        var p = g.Empires.GetPlayer(); var visible = firer is Ship s && p != null && p.VisibilityMap.DetermineVisibilityTypeForShipAllEmpires(s, p, g, g.StarDate) == VisibilityType.FullyVisible;
        if (p != null && firer?.GetEmpireId() == p.EmpireId) return ((short)p.EmpireId, "Our forces", id, string.IsNullOrWhiteSpace(name) ? "Unknown weapon" : name!);
        if (visible) return ((short)(firer?.GetEmpireId() ?? -1), firer?.GetEmpire(g)?.Name ?? "Observed forces", id, string.IsNullOrWhiteSpace(name) ? "Unknown weapon" : name!);
        return (-1, "Unknown source", 0, "Unknown weapon");
    }
}
