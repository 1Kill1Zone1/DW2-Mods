using System.Collections.Concurrent;
using DistantWorlds.Types;

namespace DW2Mod;

// Sends the player a game message (the empire message list), linked to a colony or other stellar object when there is one.
//
// A message is sent to the empire itself, in the Colonies category. Callers collect their messages while holding their own locks and
// send them afterwards, since the game's message list takes locks of its own.
//
// The game colours a message by its type alone (Colonies: grey), except Critical priority, which turns it red with a flashing border. There
// is no yellow. A warning is therefore sent at High priority, which lifts it in the message list's ordering, and says so in its words. A
// colony in danger can be sent as Military type, which the game draws orange (it sends its own "Colony Overthrown" that way).
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class Notify
{
    // ENGINE 1.3.8.0  DistantWorlds.Types.Empire.SendMessageToEmpire(Empire, EmpireMessageType, EmpireMessageSubType, string title, string text, object linked, DrawDetailType, EmpireMessagePriority)
    //   how:     called directly (not patched)
    //   why:     the game's own way to put a titled message, linked to an object, in the player's message list
    //   relies:  the empire passes itself as the sender; Colonies is a category the player can see by default; a null link is allowed (the game's
    //            own diplomacy messages pass one)
    //   moved?:  search Empire for SendMessageToEmpire
    //   breaks:  loud if the signature changes (compile error)
    public static void Send(Empire empire, string title, string text, Orb linked, EmpireMessagePriority priority = EmpireMessagePriority.Normal,
        EmpireMessageSubType subType = EmpireMessageSubType.ColoniesEventsOther, EmpireMessageType type = EmpireMessageType.Colonies)
    {
        try
        {
            empire.SendMessageToEmpire(empire, type, subType, title, text, linked,
                linked != null ? DrawDetailType.ColonyColonies : DrawDetailType.Undefined, priority);
        }
        catch (Exception ex)
        {
            ModHost.Failed("notification", ex);
        }
    }

    // A message that opens one of the empire screens (a DrawDetailType, such as EmpireFundingLevels for Shift+F2) when clicked in the message list, with no map link.
    //
    // ENGINE 1.3.8.4  DistantWorlds2.ScaledRenderer message click handler (switch on EmpireMessage.UserInterfaceArea)
    //   how:     not patched; the same SendMessageToEmpire overload as Send, with a null object and the screen's DrawDetailType
    //   why:     the click falls to EmpireSectionList.OpenSection(message.UserInterfaceArea) for any area without a special case (the game's own messages do this with EmpireGovernment and DiplomacyRelations)
    //   relies:  the area is not one of the special cases (Characters, ConstructionBuildOrder, ResearchScreen, ColonyApproval, ShipDamage, ResourceStockLevels) and no related item or location is set
    //   moved?:  search ScaledRenderer for message.UserInterfaceArea
    //   breaks:  quiet (the click opens nothing)
    public static void SendToScreen(Empire empire, string title, string text, DrawDetailType screen, EmpireMessagePriority priority = EmpireMessagePriority.Normal,
        EmpireMessageSubType subType = EmpireMessageSubType.ColoniesEventsOther, EmpireMessageType type = EmpireMessageType.Colonies)
    {
        try
        {
            empire.SendMessageToEmpire(empire, type, subType, title, text, (Orb)null, screen, priority);
        }
        catch (Exception ex)
        {
            ModHost.Failed("notification", ex);
        }
    }

    // A message carrying a game item (a resource, say) instead of a map object. The game draws the item's picture large in the message's own dialog, as it does
    // for its "Critical Research Failure" (the research project). Clicking the message in the list opens that dialog; it has no map link.
    //
    // ENGINE 1.3.8.0  DistantWorlds.Types.Empire.SendMessageToEmpire(Empire, EmpireMessageType, EmpireMessageSubType, string title, string text, GameTask, StellarObject, DrawDetailType, EmpireMessagePriority, EmpireMessageViewMode, Item relatedItem)
    //   how:     called directly (not patched)
    //   why:     the game's own way to attach an item to a message; EmpireMessage.ResolveMessageImage draws a Resource item as the resource's image
    //   relies:  null task and object are allowed (the game's research messages pass them); Item(IDw2Item) takes the item's type and id
    //   moved?:  search Empire for SendMessageToEmpire with an Item argument
    //   breaks:  loud if the signature changes (compile error)
    public static void SendWithItem(Empire empire, string title, string text, IDw2Item item, EmpireMessagePriority priority = EmpireMessagePriority.Normal,
        EmpireMessageSubType subType = EmpireMessageSubType.Undefined, EmpireMessageType type = EmpireMessageType.Resources)
    {
        try
        {
            empire.SendMessageToEmpire(empire, type, subType, title, text, (GameTask)null, (StellarObject)null, DrawDetailType.Undefined, priority,
                EmpireMessageViewMode.Undefined, new Item(item));
        }
        catch (Exception ex)
        {
            ModHost.Failed("notification", ex);
        }
    }

    static readonly ConcurrentDictionary<string, long> lastSent = new();

    // True when nothing was sent under this key within the last span of star dates, and records that one is being sent now. A star date earlier
    // than the recorded one (another game was loaded) counts as long ago. A year is 600,000 star dates.
    public static bool Throttle(string key, long starDate, long span)
    {
        bool allowed = false;
        lastSent.AddOrUpdate(key, _ =>
        {
            allowed = true;
            return starDate;
        }, (_, last) =>
        {
            allowed = starDate < last || starDate - last >= span;
            return allowed ? starDate : last;
        });
        return allowed;
    }
}
