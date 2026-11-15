using DistantWorlds.Types;
using HarmonyLib;

namespace ResourceIntelligence;

internal enum DepositFilter { Any, Free, Occupied }
internal sealed record Scarcity(int Level, string Explanation);
internal sealed record ResourceRarity(int Multiplier, string Explanation);
internal sealed record Offer(Galaxy Galaxy, Empire Buyer, Empire Seller, Orb Origin, short ResourceId,
    DepositFilter Filter, Orb Target, byte Discovery, bool Occupied, Scarcity Scarcity, ResourceRarity Rarity, int Price);

internal static class Market
{
    static readonly AccessTools.FieldRef<Economy, object> economyLock = AccessTools.FieldRefAccess<Economy, object>("_LockObject");
    static readonly AccessTools.FieldRef<Economy, EconomyFactorSet> factors = AccessTools.FieldRefAccess<Economy, EconomyFactorSet>("_EconomicFactors");
    static readonly AccessTools.FieldRef<VisibilityMap, VisibilityStatus[]> visibility = AccessTools.FieldRefAccess<VisibilityMap, VisibilityStatus[]>("_SystemVisibilityStatus");
    internal static readonly object Gate = new();

    internal static bool Eligible(Galaxy g, Empire buyer, Empire seller) => buyer != seller && buyer.IsPlayer && buyer.Active && seller.Active &&
        g.Empires.GetById(buyer.EmpireId) == buyer && g.Empires.GetById(seller.EmpireId) == seller &&
        buyer.Relations.Any(r => r.OtherEmpireId == seller.EmpireId && r.HaveMet) &&
        !buyer.Relations.Any(r => r.OtherEmpireId == seller.EmpireId && r.AtWar) &&
        !seller.Relations.Any(r => r.OtherEmpireId == buyer.EmpireId && r.AtWar);

    internal static bool Known(Empire e, Orb o, short resource)
    {
        byte level = e.ExplorationMap.CheckExplorationLevelSafely(o.OrbId, 0);
        return level > 0 && o.Resources?.Any(r => r.ResourceId == resource && r.Abundance > 0 && r.DiscoveryLevel <= level) == true;
    }

    internal static HashSet<int> OccupiedOrbs(Galaxy g, Empire buyer)
    {
        var result = new HashSet<int>();
        foreach (var o in g.Orbs)
            if (o?.Colony is { } colony && colony.EmpireId != buyer.EmpireId) result.Add(o.OrbId);
        foreach (var e in g.Empires)
            if (e != null && e != buyer && e.Active)
                foreach (var s in e.MiningStations)
                    if (s != null && s.EmpireId != buyer.EmpireId && g.Ships.GetById(s.ShipId) == s && s.Parent.ResolveBase<Orb>(g) is { } orb) result.Add(orb.OrbId);
        return result;
    }

    internal static Scarcity Assess(Empire e, short id)
    {
        try
        {
            float critical = e.CriticalMissingResources?.GetAmount(id) ?? 0;
            if (float.IsFinite(critical) && critical > 0) return new(2, $"Crisis: the game reports {critical:N0} critical missing units of this resource.");
            float held = e.CalculateTotalCargoLevels(e.EmpireId).GetAmount(id);
            float target = e.CalculateTotalStockLevels(true).GetAmount(id);
            float need = e.ResourceRatesRequiredComplete.GetAmount(id);
            float supply = e.ResourceRatesCurrent.GetAmount(id) + e.ResourceRatesUnderConstruction.GetAmount(id);
            float orders = e.Orders.GetTotalAmountOutstanding(id);
            if (!new[] { held, target, need, supply, critical, orders }.All(float.IsFinite)) throw new InvalidOperationException("Invalid stock figures");
            bool wanted = target > 0 || need > 0 || orders > 0 || critical > 0;
            int level = critical > 0 || wanted && held < 1 ? 2 :
                wanted && (orders > held || held < target && supply < need) ? 1 : 0;
            return new(level, $"{(level == 2 ? "Crisis" : level == 1 ? "Shortage" : "Base price")}: stock {held:N0}, stock target {target:N0}, supply/need {supply:N1}/{need:N1} per day, orders {orders:N0}.");
        }
        catch { return new(0, "Shortage assessment unavailable; base shortage tier applies."); }
    }

    internal static ResourceRarity RarityFromCounts(int resourceSystems, int surveyedSystems)
    {
        if (surveyedSystems < 10) return new(1, "Rarity: too little surveyed resource data from this seller; no rarity surcharge (x1).");
        // Integer comparisons preserve the agreed inclusive 5% and 20% boundaries.
        int multiplier = (long)resourceSystems * 100 < (long)surveyedSystems * 5 ? 4 :
            (long)resourceSystems * 100 <= (long)surveyedSystems * 20 ? 2 : 1;
        return new(multiplier, $"Rarity in this seller's surveyed records: {(multiplier == 4 ? "very rare" : multiplier == 2 ? "rare" : "common")} (x{multiplier}).");
    }

    internal static ResourceRarity EstimateRarity(Galaxy g, Empire seller, short resource)
    {
        var surveyed = new HashSet<int>(); var contains = new HashSet<int>();
        foreach (var o in g.Orbs)
        {
            if (o?.Resources == null) continue;
            byte level = seller.ExplorationMap.CheckExplorationLevelSafely(o.OrbId, 0);
            if (level == 0 || o.GetSystem(g) is not { } system) continue;
            bool any = false, selected = false;
            foreach (var r in o.Resources)
                if (r.Abundance > 0 && r.DiscoveryLevel <= level)
                { any = true; selected |= r.ResourceId == resource; }
            if (any) surveyed.Add(system.SystemId);
            if (selected) contains.Add(system.SystemId);
        }
        return RarityFromCounts(contains.Count, surveyed.Count);
    }

    internal static int Price(int level, bool occupied, int rarityMultiplier = 1)
    {
        if (rarityMultiplier is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(rarityMultiplier));
        return (level == 2 ? 80000 : level == 1 ? 40000 : 20000) * rarityMultiplier / (occupied ? 2 : 1);
    }

    internal static Offer? Quote(Galaxy g, Empire buyer, Empire seller, Orb origin, short resource, DepositFilter filter)
    {
        if (!Eligible(g, buyer, seller) || !buyer.Colonies.Contains(origin) || origin.Colony?.EmpireId != buyer.EmpireId || g.Resources.GetById(resource) == null) return null;
        var occupied = OccupiedOrbs(g, buyer);
        Orb? best = null; double distance = double.MaxValue;
        foreach (var o in g.Orbs)
        {
            if (o == null || o.GetSystem(g) == null || !Known(seller, o, resource) || Known(buyer, o, resource)) continue;
            bool taken = occupied.Contains(o.OrbId);
            if (filter == DepositFilter.Free && taken || filter == DepositFilter.Occupied && !taken) continue;
            double dx = (double)o.GalaxyX - origin.GalaxyX, dy = (double)o.GalaxyY - origin.GalaxyY;
            double d = dx * dx + dy * dy;
            if (d < distance || d == distance && o.OrbId < best?.OrbId) { best = o; distance = d; }
        }
        if (best == null) return null;
        var scarcity = Assess(buyer, resource);
        var rarity = EstimateRarity(g, seller, resource);
        byte discovery = best.Resources.Where(r => r.ResourceId == resource && r.Abundance > 0).Min(r => r.DiscoveryLevel);
        return new(g, buyer, seller, origin, resource, filter, best, (byte)Math.Max(1, (int)discovery), occupied.Contains(best.OrbId), scarcity, rarity,
            Price(scarcity.Level, occupied.Contains(best.OrbId), rarity.Multiplier));
    }

    static byte[] SaveEconomy(Economy e)
    { using var s = new MemoryStream(); using var w = new BinaryWriter(s); e.WriteToStream(w); return s.ToArray(); }
    static void RestoreEconomy(Galaxy g, Economy e, byte[] bytes)
    { using var s = new MemoryStream(bytes); using var r = new BinaryReader(s); e.ReadFromStream(g, r); }

    // Both native treasury locks prevent rollback from overwriting concurrent income/expenses.
    internal static bool Buy(Offer q, out string message)
    {
        lock (Gate)
        {
            var buyer = q.Buyer; var seller = q.Seller; var g = q.Galaxy;
            var first = buyer.EmpireId < seller.EmpireId ? buyer : seller;
            var second = first == buyer ? seller : buyer;
            lock (economyLock(first.Economy)) lock (economyLock(second.Economy))
            {
                var now = Quote(g, buyer, seller, q.Origin, q.ResourceId, q.Filter);
                if (now == null || now.Target != q.Target || now.Price != q.Price || now.Occupied != q.Occupied || now.Discovery != q.Discovery || now.Scarcity.Level != q.Scarcity.Level || now.Rarity.Multiplier != q.Rarity.Multiplier)
                { message = "This offer has changed or expired. Request a new offer; nothing was charged."; return false; }
                if (!float.IsFinite(buyer.MoneyAvailable) || buyer.MoneyAvailable < q.Price)
                { message = "Not enough available state credits. Nothing was charged."; return false; }
                var system = q.Target.GetSystem(g);
                var levels = buyer.ExplorationMap.GetMapDirectly();
                var visible = visibility(buyer.VisibilityMap);
                if (system == null || q.Target.OrbId < 0 || q.Target.OrbId >= levels.Length || system.SystemId < 0 || system.SystemId >= visible.Length)
                { message = "The location cannot be revealed safely. Nothing was charged."; return false; }
                byte oldLevel = levels[q.Target.OrbId]; var oldVisibility = visible[system.SystemId];
                byte[] beforeBuyer, beforeSeller;
                try { beforeBuyer = SaveEconomy(buyer.Economy); beforeSeller = SaveEconomy(seller.Economy); }
                catch { message = "Could not prepare the transaction. Nothing was charged."; return false; }
                try
                {
                    float expenseFactor = factors(buyer.Economy).GetExpenseFactor(ExpenseType.Information);
                    float incomeFactor = factors(seller.Economy).GetIncomeFactor(IncomeType.Information);
                    if (!float.IsFinite(expenseFactor) || expenseFactor <= 0 || !float.IsFinite(incomeFactor) || incomeFactor <= 0) throw new InvalidOperationException("Invalid information price factor");
                    float buyerMoney = buyer.Economy.Money, sellerMoney = seller.Economy.Money;
                    buyer.ExplorationMap.SetExplorationLevel(q.Target, q.Discovery);
                    buyer.VisibilityMap.SetSystemExplored(system.SystemId);
                    if (!Known(buyer, q.Target, q.ResourceId)) throw new InvalidOperationException("Reveal did not take effect");
                    buyer.Economy.Expense(q.Price / expenseFactor, ExpenseType.Information, g.StarDate, seller.EmpireId);
                    seller.Economy.Income(q.Price / incomeFactor, IncomeType.Information, g.StarDate, buyer.EmpireId);
                    if (Math.Abs(buyer.Economy.Money - (buyerMoney - q.Price)) > 1 || Math.Abs(seller.Economy.Money - (sellerMoney + q.Price)) > 1)
                        throw new InvalidOperationException("Treasury amount mismatch");
                    // Derived map summaries are refreshed independently of the completed native knowledge/payment.
                    try { buyer.VisibilityMap.UpdateKnownData(g, buyer, Constants.ResourceAbundanceDefaultThreshold, 0); }
                    catch (Exception ex) { Console.WriteLine("[ResourceIntelligence] Deferred map-summary refresh: " + ex.GetType().Name); }
                    message = $"Purchased for {q.Price:N0} credits. {g.Resources.GetById(q.ResourceId).Name}: {q.Target.Name}, {system.Name}.\n" +
                        $"{(q.Occupied ? "Occupied by a foreign colony or mining base; extraction rights are not included." : "No foreign colony or mining base at purchase time.")}\n" +
                        "Added to your empire's normal surveyed map. Other details at this survey level on the same body may also become known. Route safety is not guaranteed.";
                    if (!PurchaseNotice.Publish(q, message))
                        message += "\nPurchase completed, but the journal notification is unavailable. Use Show on map in this conversation now.";
                    return true;
                }
                catch (Exception ex)
                {
                    levels[q.Target.OrbId] = oldLevel;
                    visible[system.SystemId] = oldVisibility;
                    try { buyer.ExplorationMap.UpdateLowestLevelForSystem(buyer, system); }
                    finally { try { RestoreEconomy(g, buyer.Economy, beforeBuyer); } finally { RestoreEconomy(g, seller.Economy, beforeSeller); } }
                    Console.WriteLine("[ResourceIntelligence] Purchase rolled back: " + ex.GetType().Name);
                    message = "The purchase failed and was rolled back. Nothing was charged. Request a new offer.";
                    return false;
                }
            }
        }
    }
}
