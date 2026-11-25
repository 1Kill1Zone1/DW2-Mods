using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace CommunityPatch;

internal static class CameraSettingsDialog
{
    private const string PanelId = "CommunityPatch_Camera";
    private const string PresetKey = "CameraPreset";
    private const string Custom = "Custom";
    private const string Balanced = "Galaxy 3x / System 2x";
    private sealed record Choice(string Key, string Label, string Description, string Default, string[] Options);
    private static readonly Choice[] Choices = LoadChoices();
    private static readonly ConditionalWeakTable<CollapsiblePanelList, Editor> Editors = new();
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static JsonObject active = new();
    private static string? settingsFile;

    private static Choice[] LoadChoices()
    {
        using var stream = typeof(CameraSettingsDialog).Assembly.GetManifestResourceStream("CommunityPatch.CameraSchema")!;
        return JsonNode.Parse(stream)!["Camera"]!.Deserialize<Choice[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    internal static void Install(JsonObject options, string? observedPath, string? modKey, string root)
    {
        active = (JsonObject)options.DeepClone();
        // ObservedHooksPath belongs to the active launcher's own user-data root, including portable profiles.
        if (Path.IsPathFullyQualified(observedPath ?? "") && Path.GetFileName(observedPath) == "hooks-observed.json")
        {
            var leaf = Path.GetFileName(root);
            var token = modKey == "managed:" + leaf ? "mods_" + leaf :
                modKey == "workshop:" + leaf && leaf.All(char.IsAsciiDigit) ? "steam_" + leaf : null;
            if (token != null) settingsFile = Path.Combine(Path.GetDirectoryName(observedPath)!, "ModSettings", token + ".json");
        }
        if (settingsFile != null && Text(active, PresetKey) == Balanced)
        {
            try
            {
                var changes = new JsonObject { [PresetKey] = Custom, ["SystemZoomSpeed"] = "2x", ["GalaxyZoomSpeed"] = "3x" };
                if (Text(ReadStored(), PresetKey) == Balanced) Commit(changes);
            }
            catch (Exception ex) when (IsStorageError(ex)) { System.Diagnostics.Trace.WriteLine(ex); }
        }
        var harmony = new Harmony("DW2.CommunityPatch.CameraSettings");
        harmony.Patch(AccessTools.Method(typeof(CollapsiblePanelList), nameof(CollapsiblePanelList.GenerateGameSettingPanels)),
            postfix: new HarmonyMethod(typeof(CameraSettingsDialog), nameof(AddPanel)));
        harmony.Patch(AccessTools.Method(typeof(GameSettingsDialog), nameof(GameSettingsDialog.BindData)),
            prefix: new HarmonyMethod(typeof(CameraSettingsDialog), nameof(BeforeBind)),
            postfix: new HarmonyMethod(typeof(CameraSettingsDialog), nameof(AfterBind)));
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
    private static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject ReadStored()
    {
        if (settingsFile == null) throw new IOException("The active launcher settings folder could not be identified.");
        if (!File.Exists(settingsFile)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject ?? throw new InvalidDataException("Invalid launcher settings.");
    }

    private static void Commit(JsonObject changes)
    {
        if (settingsFile == null && changes.Count == 0) return;
        JsonObject stored;
        if (changes.Count == 0)
        {
            try { stored = ReadStored(); }
            catch (Exception ex) when (IsStorageError(ex)) { return; }
        }
        else
        {
            if (settingsFile == null) throw new IOException("The launcher settings path is unavailable.");
            // A manager write already in progress must finish before we merge this dialog's edited keys.
            using var existing = File.Exists(settingsFile)
                ? new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)
                : null;
            stored = existing == null ? new JsonObject() : JsonNode.Parse(existing) as JsonObject
                ?? throw new InvalidDataException("Invalid launcher settings.");
            foreach (var pair in changes) stored[pair.Key] = pair.Value?.DeepClone();
            var temp = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, stored.ToJsonString(WriteOptions));
                if (existing == null) File.Move(temp, settingsFile);
                else File.Replace(temp, settingsFile, null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        bool changed = false;
        foreach (var choice in Choices)
        {
            if (!stored.TryGetPropertyValue(choice.Key, out var value) || JsonNode.DeepEquals(active[choice.Key], value)) continue;
            active[choice.Key] = value?.DeepClone();
            changed = true;
        }
        if (changed) MapCamera.ApplySettings(active);
    }

    private static void BeforeBind(GameSettingsDialog __instance, ref EventHandler<DWEventArgs> okButtonClickEvent)
    {
        var native = okButtonClickEvent;
        okButtonClickEvent = (sender, args) =>
        {
            if (__instance.Settings != null && Editors.TryGetValue(__instance.Settings, out var editor) && !editor.Save()) return;
            native?.Invoke(sender, args);
        };
    }

    private static void AfterBind(GameSettingsDialog __instance)
    {
        if (__instance.Settings != null && Editors.TryGetValue(__instance.Settings, out var editor)) editor.Open();
    }

    private static void AddPanel(CollapsiblePanelList __instance, Galaxy galaxy, Empire empire)
    {
        if (__instance.GetPanelByName(PanelId) != null) return;
        var template = __instance.Panels.FirstOrDefault();
        if (template == null) return;
        Editors.Remove(__instance);
        var editor = new Editor();
        Editors.Add(__instance, editor);
        editor.Build(__instance, galaxy, empire, template);
    }

    public sealed class Selection
    {
        public string Text { get; set; } = "";
    }

    private sealed class Editor
    {
        private readonly Dictionary<string, Selection> selections = new();
        private readonly Dictionary<string, string> initial = new();
        private readonly ValueBindingList rows = new();
        private CollapsiblePanelData? heading;

        public void Build(CollapsiblePanelList list, Galaxy galaxy, Empire empire, CollapsiblePanelData template)
        {
            float Scale(float v) => UserInterfaceHelper.CalculateScaledValue(v);
            var panel = new ValueBindingListPanel { Name = PanelId, Layer = template.Panel.Layer, Margin = Scale(5), BackColor = template.Panel.BackColor };
            panel.Initialize(FontSize.Normal, FontSize.Large, null!);
            float width = template.Panel.Size.X > Scale(300) ? template.Panel.Size.X : Scale(490);
            float labelWidth = width * .46f;
            float y = 0;
            foreach (var choice in Choices)
            {
                var values = new SortableTextStringList();
                foreach (string option in choice.Options) values.Add(new SortableTextString(option, option));
                var pick = new Selection { Text = choice.Default };
                selections.Add(choice.Key, pick);
                var row = new ValueBinding(galaxy, empire, pick, nameof(Selection.Text), typeof(string), -1, panel,
                    choice.Label, PanelId + "_" + choice.Key, choice.Description, Color.Transparent, list.FontNormal,
                    labelWidth, panel.Margin, labelWidth, y, width - labelWidth - 2 * panel.Margin, Scale(30), values);
                row.UpdateHandler = (_, _) =>
                {
                    if (choice.Key == PresetKey && pick.Text == Balanced)
                    {
                        selections["SystemZoomSpeed"].Text = "2x";
                        selections["GalaxyZoomSpeed"].Text = "3x";
                    }
                    else if (choice.Key is "SystemZoomSpeed" or "GalaxyZoomSpeed") selections[PresetKey].Text = Custom;
                    Refresh();
                };
                rows.Add(row);
                y += row.RowHeight;
            }
            var height = rows.GetTotalHeight();
            panel.SetSizeAndPosition(new Vector2(width, height), Vector2.Zero);
            panel.BindData(rows, new Color(48, 48, 48, 192));
            heading = new CollapsiblePanelData(template.HeaderImage, template.HeaderSprite, "Camera", template.HeaderFont,
                template.HeaderHeight, template.IconSize, template.ArrowSize, height, panel);
            list.AddPanel(heading);
            list.Panels.Remove(heading);
            list.Panels.Insert(1, heading);
        }

        public void Open()
        {
            if (heading == null) return;
            JsonObject stored;
            try { stored = ReadStored(); heading.HeaderText = "Camera"; }
            catch (Exception ex) when (IsStorageError(ex)) { stored = new(); heading.HeaderText = "Camera - launcher settings unavailable"; }
            foreach (var choice in Choices)
            {
                var value = Text(stored, choice.Key) ?? Text(active, choice.Key);
                initial[choice.Key] = selections[choice.Key].Text = choice.Key != PresetKey && choice.Options.Contains(value) ? value! : choice.Default;
            }
            Refresh();
        }

        public bool Save()
        {
            var changes = new JsonObject();
            foreach (var choice in Choices)
                if (choice.Key != PresetKey && initial[choice.Key] != selections[choice.Key].Text)
                    changes[choice.Key] = selections[choice.Key].Text;
            try { Commit(changes); return true; }
            catch (Exception ex) when (IsStorageError(ex))
            {
                heading!.HeaderText = "Camera - settings could not be saved. Retry Apply or Cancel.";
                return false;
            }
        }

        private void Refresh()
        {
            foreach (var row in rows)
            {
                row.BindData(row.ValueObject);
                if (row.Control is not DWButton button) continue;
                string mode = selections["WheelZoomSmoothing"].Text;
                button.Enabled = row.ControlName switch
                {
                    PanelId + "_WheelSmoothingDuration" => mode is "Soft" or "Inertia",
                    PanelId + "_WheelInertiaCoast" => mode == "Inertia",
                    PanelId + "_WheelAccelerationRamp" => selections["WheelAcceleration"].Text != "Off",
                    _ => true
                };
            }
        }
    }
}
