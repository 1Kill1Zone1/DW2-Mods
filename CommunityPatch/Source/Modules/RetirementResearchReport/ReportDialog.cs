using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace RetirementResearchReport;

internal static class ReportDialog
{
    // Only strings bound by our message dialog use the inline-icon renderer.
    static readonly ConditionalWeakTable<string, Row[]> Tables = new();
    static readonly ConditionalWeakTable<ContentManager, Dictionary<string, Texture?>> Textures = new();
    internal sealed record Row(string Label, string Progress, string Gain, string[] Art);

    internal static void Install(Harmony hooks)
    {
        hooks.Patch(AccessTools.DeclaredMethod(typeof(EmpireMessageDialog), nameof(EmpireMessageDialog.BindData)),
            transpiler: new HarmonyMethod(typeof(ReportDialog), nameof(BindText)));
        hooks.Patch(AccessTools.DeclaredMethod(typeof(EmpireMessage), nameof(EmpireMessage.RenderListHover)),
            transpiler: new HarmonyMethod(typeof(ReportDialog), nameof(HideReceipt)));
        hooks.Patch(AccessTools.DeclaredMethod(typeof(DrawingHelper), nameof(DrawingHelper.RenderMarkupText)),
            prefix: new HarmonyMethod(typeof(ReportDialog), nameof(RenderRows)));
    }

    static IEnumerable<CodeInstruction> BindText(IEnumerable<CodeInstruction> instructions)
    {
        var input = instructions.ToArray();
        var setText = AccessTools.DeclaredMethod(typeof(Dialog), nameof(Dialog.SetText), [typeof(string), typeof(float), typeof(float)]);
        if (input.Count(i => i.Calls(setText)) != 1)
            throw new NotSupportedException("Retirement report dialog text call changed.");
        foreach (var instruction in input)
        {
            if (!instruction.Calls(setText)) { yield return instruction; continue; }
            var message = new CodeInstruction(OpCodes.Ldarg_1);
            message.labels.AddRange(instruction.labels);
            message.blocks.AddRange(instruction.blocks);
            yield return message;
            yield return CodeInstruction.Call(typeof(ReportDialog), nameof(SetReportText));
        }
    }

    static IEnumerable<CodeInstruction> HideReceipt(IEnumerable<CodeInstruction> instructions)
    {
        var input = instructions.ToArray();
        var field = AccessTools.Field(typeof(EmpireMessage), nameof(EmpireMessage.Text));
        if (input.Count(i => i.LoadsField(field)) != 4)
            throw new NotSupportedException("Retirement report message hover text calls changed.");
        var visibleText = AccessTools.DeclaredMethod(typeof(Report), nameof(Report.VisibleText));
        foreach (var instruction in input)
        {
            if (instruction.LoadsField(field))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = visibleText;
            }
            yield return instruction;
        }
    }

    internal static void SetReportText(Dialog dialog, string text, float width, float maximumHeight, EmpireMessage message)
    {
        string? visible = Report.VisibleText(message);
        if (visible != null && !ReferenceEquals(visible, text))
        {
            var rows = ParseRows(visible);
            if (rows.Any(row => row.Gain.Length > 0))
            {
                Tables.AddOrUpdate(visible, rows);
                dialog.TextIsScrollable = true;
            }
            text = visible;
        }
        dialog.SetText(text, width, maximumHeight);
    }

    internal static Row[] ParseRows(string text) => text.Split('\n').Select(line =>
    {
        line = line.TrimEnd('\r');
        int separator = line.LastIndexOf(Report.ColumnSeparator, StringComparison.Ordinal);
        int gain = line.LastIndexOf('+');
        if (separator < 0 || gain < separator + Report.ColumnSeparator.Length || !line.EndsWith('%'))
            return new Row(line, "", "", []);
        string label = line[..separator];
        return new Row(label, line[(separator + Report.ColumnSeparator.Length)..gain].TrimEnd(), line[gain..], IconPaths(label).ToArray());
    }).ToArray();

    static bool RenderRows(SpriteBatch? spriteBatch, RectangleF region, string? markupText, float margin, float scrollOffset,
        SpriteFont normalFont, Color textColor, Color shadowColor, ContentManager? content, ref Vector2 __result)
    {
        if (markupText == null || !Tables.TryGetValue(markupText, out var rows)) return true;
        float glyphHeight = normalFont.MeasureString("Ag").Y;
        float gap = Math.Max(3, glyphHeight * .25f);
        float rowHeight = glyphHeight + gap;
        float width = Math.Max(1, region.Width - margin * 2);
        __result = new Vector2(width, rows.Length * rowHeight + margin * 2);
        if (spriteBatch == null) return false;

        float left = region.X + margin;
        float right = left + width;
        float y = region.Y + margin - scrollOffset;
        foreach (var row in rows)
        {
            if (y + rowHeight > region.Y && y < region.Bottom)
            {
                float labelLeft = left;
                float labelRight = right;
                if (row.Gain.Length > 0)
                {
                    DrawIcon(spriteBatch, content, row.Art, new RectangleF(left, y, glyphHeight, glyphHeight), textColor.A);
                    labelLeft += glyphHeight + gap;
                    var gainWidth = normalFont.MeasureString(row.Gain).X;
                    Draw(row.Gain, right - gainWidth, gainWidth, new Color(141, 230, 129, textColor.A));
                    labelRight -= gainWidth + gap;
                    if (row.Progress.Length > 0)
                    {
                        float progressWidth = normalFont.MeasureString(row.Progress).X;
                        Draw(row.Progress, labelRight - progressWidth, progressWidth, textColor);
                        labelRight -= progressWidth + gap;
                    }
                }
                float labelWidth = Math.Max(0, labelRight - labelLeft);
                if (labelWidth > 0) Draw(Fit(row.Label, normalFont, labelWidth), labelLeft, labelWidth, textColor);
            }
            y += rowHeight;
        }
        return false;

        void Draw(string text, float x, float cellWidth, Color color) =>
            DrawingHelper.DrawStringDropShadow(spriteBatch, text, normalFont, color, shadowColor, new Vector2(x, y),
                new Vector2(cellWidth + 1, rowHeight), TextAlignment.Left);
    }

    static string Fit(string text, SpriteFont font, float width)
    {
        if (font.MeasureString(text).X <= width) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (font.MeasureString(text[..middle] + "…").X <= width) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        return font.MeasureString("…").X <= width ? text[..low] + "…" : "";
    }

    internal static IEnumerable<string> IconPaths(string label)
    {
        var name = label;
        foreach (string suffix in new[] { " [Racial bonus]", " [Complete]" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        int repeat = name.LastIndexOf(" (repeat ", StringComparison.Ordinal);
        if (repeat >= 0 && name.EndsWith(')')) name = name[..repeat];

        var project = Galaxy.ResearchProjectsStatic?.FirstOrDefault(p => p?.Name == name);
        if (project == null) yield break;
        if (!string.IsNullOrWhiteSpace(project.ImageFilename) &&
            !project.ImageFilename.EndsWith("/Placeholder", StringComparison.OrdinalIgnoreCase)) yield return project.ImageFilename;
        if (project.Components == null || Galaxy.ComponentsStatic == null) yield break;
        foreach (var component in project.Components)
            if (Galaxy.ComponentsStatic.FirstOrDefault(c => c?.ComponentId == component.ComponentId)?.ImageFilename is { Length: > 0 } image)
                yield return image;
    }

    static void DrawIcon(SpriteBatch batch, ContentManager? content, string[] paths, RectangleF rectangle, byte opacity)
    {
        if (content != null)
        {
            var cache = Textures.GetValue(content, _ => new());
            foreach (string path in paths)
            {
                if (!cache.TryGetValue(path, out var texture))
                {
                    try { if (content.Exists(path)) texture = content.Load<Texture>(path); }
                    catch (Exception) { /* Missing or unusable art falls back to the research symbol. */ }
                    cache[path] = texture;
                }
                if (texture == null) continue;
                DrawingHelper.DrawTextureUndistorted(batch, texture, rectangle, new Color(255, 255, 255, opacity));
                return;
            }
        }
        var icon = EmpireMessage.ResolveMessageImage(EmpireMessageType.Research);
        if (icon != null) DrawingHelper.DrawSpriteUndistorted(batch, icon, rectangle, new Color(255, 255, 255, opacity));
    }
}
