using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace CommunityPatch;

internal static class StockpileDetails
{
    private const string DetailsHint = "Click to see storage and freight details.";
    private static readonly Func<DWControl, Point, HoverTip?> Hover = AccessTools.MethodDelegate<Func<DWControl, Point, HoverTip?>>(
        AccessTools.Method(typeof(DWControl), "CheckHoveredTip", [typeof(Point)]));
    private static MessageDialog? dialog;
    private static Galaxy? openGalaxy;
    private static ScrollableList? openList;
    private static Action? refreshPolicy;
    private static long nextRefresh;

    internal sealed record Store(string Group, string Name, float Amount);
    internal sealed record Report(Store[] Stores, float Onboard, float ToPickUp);

    internal static void Init()
    {
        var row = typeof(Resource).GetInterfaceMap(typeof(IListDrawable)).TargetMethods.Single(method =>
            method.Name.EndsWith(".DrawItem", StringComparison.Ordinal));
        var harmony = new Harmony("DW2.CommunityPatch.StockpileDetails");
        try
        {
            harmony.Patch(row, postfix: new HarmonyMethod(typeof(StockpileDetails), nameof(LabelStockpile)));
            harmony.Patch(AccessTools.Method(typeof(ScrollableList), nameof(ScrollableList.DoMouseUp)),
                postfix: new HarmonyMethod(typeof(StockpileDetails), nameof(OpenStockpile)));
            harmony.Patch(AccessTools.Method(typeof(UserInterfaceController), nameof(UserInterfaceController.Update)),
                postfix: new HarmonyMethod(typeof(StockpileDetails), nameof(CheckDialog)));
        }
        catch
        {
            harmony.UnpatchAll(harmony.Id);
            throw;
        }
    }

    private static void LabelStockpile(Empire? empire, ref HoverTipList hoverTips)
    {
        if (empire?.IsPlayer != true || hoverTips == null) return;
        var hint = hoverTips.FirstOrDefault(IsStockpile);
        if (hint != null && !hint.Text.EndsWith(DetailsHint, StringComparison.Ordinal))
            hint.Text += "\n\n" + DetailsHint;
    }

    private static bool IsStockpile(HoverTip tip) =>
        string.Equals(tip.Title, TextResolver.GetText("Stockpile"), StringComparison.Ordinal);

    private static void OpenStockpile(ScrollableList __instance, Point clickPoint, bool isLeftClick, Empire empire,
        bool shiftDown, bool ctrlDown)
    {
        if (!isLeftClick || shiftDown || ctrlDown || empire is not { IsPlayer: true, Galaxy: { } galaxy } ||
            !ReferenceEquals((Galaxy.Game as DWGame)?.Galaxy, galaxy) || !__instance.Visible ||
            !ReferenceEquals(ActiveList, __instance) ||
            !ReferenceEquals(UserInterfaceController.HoveredControl, __instance) ||
            __instance.HoveredItem is not Resource resource ||
            Hover(__instance, clickPoint) is not { } tip || !IsStockpile(tip))
            return;
        Close();
        var window = new MessageDialog
        {
            Name = "CommunityPatch.StockpileDetails",
            TextIsScrollable = true,
            PostScene = true,
            Layer = __instance.Layer + 1
        };
        window.Initialize(FontSize.Normal, FontSize.Large, UserInterfaceController.SelectionPanel?.TilingBackground!);
        var report = Read(empire, resource.ResourceId);
        window.BindData(null!, resource.Name + " stockpile", Format(report), false, (_, _) => Close(), (_, _) => Close());
        window.YesButton.Text = TextResolver.GetText("Close");
        window.NoButton.Visible = false;
        int width = UserInterfaceController.ScreenWidth;
        int height = UserInterfaceController.ScreenHeight;
        window.ShowCentered(width, height, new Vector2(Math.Min(width * .84f, UserInterfaceHelper.CalculateScaledValue(660)),
            Math.Min(height * .78f, UserInterfaceHelper.CalculateScaledValue(550))));
        dialog = window;
        openGalaxy = galaxy;
        openList = __instance;
        AddFreightControls(window, empire, resource.ResourceId);
        UserInterfaceController.AddCustomControl(window);
    }

    private static ScrollableList? ActiveList =>
        UserInterfaceController.EmpireSectionList?.ActiveSection?.ActiveSubSection?.ListItems;

    private static void CheckDialog()
    {
        if (dialog == null) return;
        if (!ReferenceEquals((Galaxy.Game as DWGame)?.Galaxy, openGalaxy) ||
            !ReferenceEquals(ActiveList, openList) || openList is not { Visible: true } || !dialog.Visible)
        {
            Close();
            return;
        }
        if (Environment.TickCount64 < nextRefresh) return;
        nextRefresh = Environment.TickCount64 + 1000;
        refreshPolicy?.Invoke();
    }

    private static void Close()
    {
        if (dialog == null) return;
        dialog.Visible = false;
        UserInterfaceController.RemoveCustomControl(dialog);
        dialog = null;
        openGalaxy = null;
        openList = null;
        refreshPolicy = null;
    }

    private static void AddFreightControls(MessageDialog window, Empire empire, short resource)
    {
        if (window.TextPanel is not { } textPanel) return;
        float rowHeight = Math.Min(UserInterfaceHelper.CalculateScaledValue(30), window.Size.Y / 11);
        float gap = Math.Max(6, rowHeight / 4);
        float width = window.Size.X - 2 * gap;
        float y = window.Size.Y - window.YesButton.Size.Y - 2 * rowHeight - 3 * gap;
        float header = UserInterfaceHelper.CalculateScaledValue(60);
        if (y <= header + rowHeight) return;
        window.SetText(window.Text, width, y - header);
        textPanel.SetSizeAndPosition(new(width, Math.Min(textPanel.Size.Y, y - header)), new(gap, header));
        var tiling = window.TilingBackground;
        DWButton Add(float x, float top, float w, string name, string label, Action? clicked = null)
        {
            var control = new DWButton { Name = name, Text = label, ShowTexture = clicked != null, ShowTextureHovered = clicked != null };
            control.Initialize(FontSize.Small, FontSize.Small, tiling);
            control.SetSizeAndPosition(new(w, rowHeight), new(gap + x, top));
            if (clicked != null) control.ClickEvent = (_, _) => clicked();
            window.AddControl(control);
            return control;
        }
        var status = Add(0, y, width, "FreightAssigned", "Assigned freighters");
        float cell = width / 3;
        float arrow = Math.Min(rowHeight, cell / 4);
        float row = y + rowHeight;
        var labels = new DWButton[3];
        string[] names = ["Priority", "Max ships", "Max fleet %"];
        bool writeFailed = false;
        for (int i = 0; i < labels.Length; i++)
        {
            int field = i;
            float x = i * cell;
            Add(x, row, arrow, $"Freight{i}Down", "−", () => Change(field, -1));
            labels[i] = Add(x + arrow, row, cell - 2 * arrow - 2, $"Freight{i}Value", names[i]);
            Add(x + cell - arrow - 2, row, arrow, $"Freight{i}Up", "+", () => Change(field, 1));
        }
        void Change(int field, int delta)
        {
            if (!ReferenceEquals(dialog, window) || !ReferenceEquals((Galaxy.Game as DWGame)?.Galaxy, empire.Galaxy)) return;
            var old = ResourceFreightPolicy.Get(empire, resource);
            var policy = field switch
            {
                0 => old with { Priority = Math.Clamp(old.Priority + delta, 1, 10) },
                1 => old with { ShipLimit = (int)Math.Clamp((long)old.ShipLimit + delta, 0, int.MaxValue) },
                _ => old with { PercentLimit = Math.Clamp(old.PercentLimit + delta, 0, 100) }
            };
            writeFailed = policy != old && !ResourceFreightPolicy.Set(empire, resource, policy);
            Refresh();
        }
        void Refresh()
        {
            var policy = ResourceFreightPolicy.Get(empire, resource);
            var fleet = ResourceFreightPolicy.Fleet(empire);
            int count = ResourceFreightPolicy.Assigned(empire, resource, fleet).Count;
            string cap = policy.HasCeiling ? $" · cap {policy.Ceiling(fleet.Count)}" : string.Empty;
            status.Text = writeFailed ? "Freight settings could not be saved" : $"Assigned  {count}/{fleet.Count}{cap}";
            labels[0].Text = $"Priority  {policy.Priority}";
            labels[1].Text = policy.ShipLimit == 0 ? "Ships  Off" : $"Ships  {policy.ShipLimit}";
            labels[2].Text = policy.PercentLimit == 0 ? "Fleet %  Off" : $"Fleet  {policy.PercentLimit}%";
        }
        refreshPolicy = Refresh;
        Refresh();
    }

    internal static Report Read(Empire empire, short resourceId)
    {
        short id = empire.EmpireId;
        var stores = new List<Store>();
        var seen = new HashSet<CargoList>(ReferenceEqualityComparer.Instance);
        void Add(string group, string name, CargoList? cargo)
        {
            if (cargo == null || !seen.Add(cargo)) return;
            float amount = cargo.GetAmount(id, resourceId);
            if (amount > 0f) stores.Add(new(group, name, amount));
        }
        void AddBases(string group, IEnumerable<Ship> bases)
        {
            foreach (var ship in bases)
                if (ship != null) Add(group, ship.Name, ship.Cargo);
        }

        foreach (var colony in empire.Colonies)
            if (colony != null) Add("Colonies", colony.Name, colony.Colony?.Cargo);
        AddBases("Spaceports", empire.Spaceports.GetBasesNotAtColonies(Galaxy.SpaceportRoles));
        AddBases("Mining stations", empire.MiningStations);
        AddBases("Other bases", empire.ResortBases.GetBasesNotAtColonies([ShipRole.ResortBase]));
        AddBases("Other bases", empire.ResearchStations.GetBasesNotAtColonies([ShipRole.ResearchStation]));
        float onboard = 0;
        var freighters = new HashSet<int>();
        foreach (var ship in empire.Ships)
        {
            if (ship == null || ship.EmpireId != id || ship.IsDestroyed ||
                !Galaxy.FreighterShipRoles.Contains(ship.Role)) continue;
            freighters.Add(ship.ShipId);
            onboard += ship.Cargo?.GetAmount(id, resourceId) ?? 0f;
        }
        float waiting = 0;
        foreach (var order in empire.Orders.GetByResourceId(resourceId).ToArray())
        {
            if (order?.Contracts == null) continue;
            foreach (var contract in order.Contracts.ToArray())
            {
                if (contract == null || !freighters.Contains(contract.FreighterId)) continue;
                waiting += Math.Max(0, (float)contract.AmountToFulfill - (float)contract.AmountPickedUp);
            }
        }
        return new(stores.ToArray(), onboard, waiting);
    }

    internal static string Format(Report report)
    {
        var lines = new List<string>
        {
            $"Stored   {report.Stores.Sum(store => store.Amount):N2}",
            $"Aboard own freighters   {report.Onboard:N2}",
            $"Scheduled for pickup   {report.ToPickUp:N2}"
        };
        foreach (var group in report.Stores.GroupBy(store => store.Group))
        {
            lines.Add("");
            lines.Add($"{group.Key}   {group.Sum(store => store.Amount):N2}");
            foreach (var store in group.OrderByDescending(store => store.Amount).ThenBy(store => store.Name, StringComparer.CurrentCulture))
                lines.Add($"  {store.Name}   {store.Amount:N2}");
        }
        return string.Join("\n", lines);
    }
}
