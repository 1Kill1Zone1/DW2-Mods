using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;

namespace ResourceIntelligence;

internal static class Dialogue
{
    internal const string EntryText = "Buy resource intelligence";
    enum Step { Open, Resources, Resource, Origins, Origin, Filters, Filter, Buy, Back, Map, Requote }
    internal sealed class Session
    {
        internal required Galaxy Galaxy; internal required Empire Buyer; internal required Empire Seller;
        internal int Epoch; internal Orb? Origin; internal short ResourceId = -1; internal DepositFilter Filter;
        internal Offer? Offer; internal Orb? Purchased; internal Orb? PendingMap;
    }
    sealed record Choice(DiplomacyTalkSystem Owner, Session Session, int Epoch, Step Step, int Value);
    static readonly ConditionalWeakTable<DiplomacyTalkSystem, Session> sessions = new();
    static readonly ConditionalWeakTable<DiplomacyTalkOption, Choice> choices = new();
    static readonly AccessTools.FieldRef<DiplomacyTalkPanel, DiplomacyTalkSystem> talkSystem = AccessTools.FieldRefAccess<DiplomacyTalkPanel, DiplomacyTalkSystem>("_DiplomacyTalkSystem");

    internal static void Bound(DiplomacyTalkSystem __instance) => sessions.Remove(__instance);

    static Session? Current(DiplomacyTalkSystem talk)
    {
        var g = talk.Galaxy; var buyer = talk.Empire;
        var host = Galaxy.Game;
        var activeGalaxy = host?.GetType().GetProperty("Galaxy");
        if (activeGalaxy != null && !ReferenceEquals(activeGalaxy.GetValue(host), g)) return null;
        var seller = talk.Relation?.GetOtherEmpire(g);
        if (g == null || buyer == null || seller == null || !buyer.IsPlayer || !seller.Active ||
            !talk.Relation!.HaveMet || talk.Relation.OtherIndependentColonyOrbId >= 0) return null;
        if (sessions.TryGetValue(talk, out var old) && old.Galaxy == g && old.Buyer == buyer && old.Seller == seller) return old;
        sessions.Remove(talk);
        var s = new Session { Galaxy = g, Buyer = buyer, Seller = seller, Origin = buyer.Capital as Orb ?? buyer.Colonies.FirstOrDefault() };
        sessions.Add(talk, s); return s;
    }

    static DiplomacyTalkOption Option(DiplomacyTalkSystem talk, Session s, string text, Step step, int value = 0, bool enabled = true, string? tip = null)
    {
        var o = new DiplomacyTalkOption(DiplomacyTalkOptionType.Undefined, text, null!, tip ?? "", !enabled, enabled);
        choices.Add(o, new(talk, s, s.Epoch, step, value)); return o;
    }

    internal static void AddEntry(DiplomacyTalkSystem __instance, DiplomacyTalkOptionType type, DiplomacyTalkOptionList __result)
    {
        if (type != DiplomacyTalkOptionType.Undefined || __result == null) return;
        var s = Current(__instance); if (s == null) return;
        s.Epoch++; s.Offer = null; s.PendingMap = null;
        bool enabled = Market.Eligible(s.Galaxy, s.Buyer, s.Seller);
        __result.Insert(0, Option(__instance, s, EntryText, Step.Open, enabled: enabled,
            tip: enabled ? "Purchase one unknown resource location known to this contact." : "Resource intelligence is unavailable during war."));
    }

    static bool Valid(DiplomacyTalkSystem talk, Choice c) => c.Owner == talk && Current(talk) == c.Session && c.Epoch == c.Session.Epoch;
    static string Context(Session s) => $"Origin: {s.Origin?.Name ?? "no owned colony"}. Locations: {s.Filter}.";
    static void Back(DiplomacyTalkSystem talk, Session s, DiplomacyTalkOptionList options) => options.Add(Option(talk, s, "Back to negotiations (no purchase)", Step.Back));

    static string Resources(DiplomacyTalkSystem talk, Session s, DiplomacyTalkOptionList options, int page = 0)
    {
        s.Offer = null;
        var resources = s.Galaxy.Resources.Where(r => r != null && r.ResourceId >= 0).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (resources.Length - 1) / 12));
        options.Add(Option(talk, s, "Change origin colony", Step.Origins));
        options.Add(Option(talk, s, "Change free / occupied location filter", Step.Filters));
        foreach (var r in resources.Skip(page * 12).Take(12)) options.Add(Option(talk, s, r.Name, Step.Resource, r.ResourceId));
        if (page > 0) options.Add(Option(talk, s, "Previous resources", Step.Resources, page - 1));
        if ((page + 1) * 12 < resources.Length) options.Add(Option(talk, s, "More resources", Step.Resources, page + 1));
        Back(talk, s, options);
        return "We can sell one resource location that we know and your empire does not. Select a resource.\n" + Context(s);
    }

    static string Quote(DiplomacyTalkSystem talk, Session s, DiplomacyTalkOptionList options)
    {
        s.Offer = s.Origin == null ? null : Market.Quote(s.Galaxy, s.Buyer, s.Seller, s.Origin, s.ResourceId, s.Filter);
        string result;
        if (s.Offer is not { } q) result = "We have no suitable unknown location to sell for that selection. Nothing was charged.\n" + Context(s);
        else
        {
            result = $"One location of {s.Galaxy.Resources.GetById(s.ResourceId).Name}, nearest to your selected colony among our eligible knowledge.\n{Context(s)}\n" +
                PurchaseNotice.Distance(q) + "\n" +
                $"{q.Scarcity.Explanation}\n{q.Rarity.Explanation}\n{(q.Occupied ? "Foreign colony or mining base: 50% occupied-location discount." : "No foreign colony or mining base.")}\n" +
                $"Calculation: {Market.Price(q.Scarcity.Level, false):N0} shortage-tier credits x {q.Rarity.Multiplier} rarity{(q.Occupied ? " x 0.5 occupation discount" : "")}.\n" +
                $"Price: {q.Price:N0} state credits. Location and abundance are disclosed only after payment.\n" +
                "Buying survey information does not include extraction rights or a safe route. Other details at the same survey level on this body may also be revealed.";
            bool affordable = float.IsFinite(s.Buyer.MoneyAvailable) && s.Buyer.MoneyAvailable >= q.Price;
            options.Add(Option(talk, s, affordable ? $"Confirm purchase: {q.Price:N0} credits" : $"Insufficient available credits ({q.Price:N0} required)", Step.Buy, enabled: affordable));
        }
        options.Add(Option(talk, s, "Choose another resource", Step.Resources));
        options.Add(Option(talk, s, "Change origin colony", Step.Origins));
        options.Add(Option(talk, s, "Change location filter", Step.Filters));
        Back(talk, s, options); return result;
    }

    internal static bool Process(DiplomacyTalkSystem __instance, DiplomacyTalkOption option, ref DiplomacyTalkOptionType nextOptions,
        ref DiplomacyTalkOptionList options, ref CharacterAnimationMood incidentMood, ref string __result)
    {
        if (option == null || !choices.TryGetValue(option, out var c)) return true;
        lock (Market.Gate)
        {
            nextOptions = DiplomacyTalkOptionType.Undefined; incidentMood = (CharacterAnimationMood)0; options = new();
            if (!Valid(__instance, c) || !option.Enabled)
            {
                options = __instance.ResolveOptions(__instance.Galaxy, null!, DiplomacyTalkOptionType.Undefined);
                __result = "This selection has expired. Nothing was charged. Please reopen resource intelligence."; return false;
            }
            var s = c.Session; s.Epoch++;
            if (c.Step == Step.Back)
            { s.Offer = null; options = __instance.ResolveOptions(s.Galaxy, null!, DiplomacyTalkOptionType.Undefined); __result = "What else would you like to discuss?"; return false; }
            if (!Market.Eligible(s.Galaxy, s.Buyer, s.Seller))
            { s.Offer = null; Back(__instance, s, options); __result = "This contact is no longer eligible to sell resource intelligence. Nothing was charged."; return false; }
            try
            {
                switch (c.Step)
                {
                    case Step.Open: case Step.Resources:
                        s.ResourceId = -1; __result = Resources(__instance, s, options, c.Value); break;
                    case Step.Resource:
                        s.ResourceId = (short)c.Value; __result = Quote(__instance, s, options); break;
                    case Step.Requote:
                        __result = Quote(__instance, s, options); break;
                    case Step.Origins:
                        s.Offer = null;
                        var colonies = s.Buyer.Colonies.Where(o => o?.Colony?.EmpireId == s.Buyer.EmpireId).OrderBy(o => o.Name).ToArray();
                        int page = Math.Clamp(c.Value, 0, Math.Max(0, (colonies.Length - 1) / 12));
                        foreach (var o in colonies.Skip(page * 12).Take(12)) options.Add(Option(__instance, s, o.Name, Step.Origin, o.OrbId));
                        if (page > 0) options.Add(Option(__instance, s, "Previous colonies", Step.Origins, page - 1));
                        if ((page + 1) * 12 < colonies.Length) options.Add(Option(__instance, s, "More colonies", Step.Origins, page + 1));
                        Back(__instance, s, options); __result = "Choose your origin colony. Distance is measured in a straight line; no route is promised."; break;
                    case Step.Origin:
                        s.Origin = s.Buyer.Colonies.FirstOrDefault(o => o.OrbId == c.Value && o.Colony?.EmpireId == s.Buyer.EmpireId);
                        __result = s.ResourceId >= 0 ? Quote(__instance, s, options) : Resources(__instance, s, options); break;
                    case Step.Filters:
                        s.Offer = null;
                        foreach (var f in Enum.GetValues<DepositFilter>()) options.Add(Option(__instance, s, f.ToString(), Step.Filter, (int)f));
                        Back(__instance, s, options); __result = "Foreign colonies and foreign mining bases count as occupied. Occupied information costs 50% less."; break;
                    case Step.Filter:
                        s.Filter = (DepositFilter)c.Value;
                        __result = s.ResourceId >= 0 ? Quote(__instance, s, options) : Resources(__instance, s, options); break;
                    case Step.Buy:
                        var q = s.Offer; s.Offer = null;
                        string purchaseMessage = "No current offer. Nothing was charged.";
                        bool bought = q != null && Market.Buy(q, out purchaseMessage);
                        if (bought)
                        {
                            s.Purchased = q!.Target;
                            __result = purchaseMessage + "\n" + PurchaseNotice.Distance(q) +
                                "\nShow on map closes this conversation and selects the purchased planet. A purchase notification is also kept in your message log when available.";
                            options.Add(Option(__instance, s, $"Show on map: {q.Target.Name} (end conversation)", Step.Map));
                        }
                        else __result = purchaseMessage;
                        options.Add(Option(__instance, s, "Request a new offer for this resource", Step.Requote));
                        options.Add(Option(__instance, s, "Choose another resource", Step.Resources)); Back(__instance, s, options); break;
                    case Step.Map:
                        if (s.Purchased is { } target && Market.Known(s.Buyer, target, s.ResourceId)) s.PendingMap = target;
                        __result = "Returning to the map."; Back(__instance, s, options); break;
                    default: throw new InvalidOperationException("Unknown dialogue choice");
                }
            }
            catch (Exception ex)
            {
                s.Offer = null; options.Clear(); Back(__instance, s, options);
                Console.WriteLine("[ResourceIntelligence] Dialogue unavailable: " + ex.GetType().Name);
                __result = "Resource intelligence is temporarily unavailable. Please return to negotiations and try again.";
            }
            return false;
        }
    }

    internal static void AfterClick(DiplomacyConversation __instance)
    {
        var talk = talkSystem(__instance.Conversation);
        if (!sessions.TryGetValue(talk, out var s) || s.PendingMap is not { } target) return;
        s.PendingMap = null;
        var exit = __instance.ExitHandler;
        if (exit == null) return;
        exit(__instance, new DWEventArgs(null!));
        s.Galaxy.ViewItem = target;
    }
}
