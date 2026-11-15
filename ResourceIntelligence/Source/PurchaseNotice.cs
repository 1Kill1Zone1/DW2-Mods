using DistantWorlds.Types;

namespace ResourceIntelligence;

internal static class PurchaseNotice
{
    internal const string TitlePrefix = "Resource intelligence: ";

    internal static string Distance(Offer offer)
    {
        double dx = (double)offer.Target.GalaxyX - offer.Origin.GalaxyX;
        double dy = (double)offer.Target.GalaxyY - offer.Origin.GalaxyY;
        return $"Straight-line distance from {offer.Origin.Name}: {Math.Sqrt(dx * dx + dy * dy) / 1_000_000:N1} million map units. This is not a route estimate.";
    }

    internal static bool Publish(Offer offer, string receipt)
    {
        // Notification failure must never roll back an already completed purchase or retry its payment.
        try
        {
            var message = new EmpireMessage(offer.Seller.EmpireId, EmpireMessageType.Exploration,
                EmpireMessageSubType.ExplorationResources, false, offer.Galaxy.StarDate,
                TitlePrefix + offer.Galaxy.Resources.GetById(offer.ResourceId).Name + " - " + offer.Target.Name,
                receipt + $"\nSeller: {offer.Seller.Name}.\n" + Distance(offer) +
                "\nUse this message's location action to return to the purchased planet after closing negotiations.",
                offer.Target, DrawDetailType.Undefined, EmpireMessagePriority.High)
            {
                RelatedItem = new Item(offer.Target),
                ViewMode = EmpireMessageViewMode.Dialog
            };
            offer.Seller.SendMessageToEmpire(offer.Buyer, message);
            if (!offer.Buyer.Messages.Contains(message)) throw new InvalidOperationException("Notification was not retained");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ResourceIntelligence] Purchase completed; notification unavailable: " + ex.GetType().Name);
            return false;
        }
    }
}
