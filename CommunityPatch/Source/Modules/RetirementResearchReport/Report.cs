using System.Globalization;
using System.Text;
using DistantWorlds.Types;

namespace RetirementResearchReport;

internal static class Report
{
    internal const string ColumnSeparator = "  |  ";
    internal static string Token(Ledger ledger, Notice notice) => $"[RRR:{ledger.EmpireId}:{ledger.ShipId}:{notice.Sequence}]";
    static string Percent(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    static double Share(float value, float size) => 100d * value / size;

    internal static string Text(Ledger ledger, Notice notice)
    {
        var text = new StringBuilder();
        if (notice.Sequence > 1) text.AppendLine("New gains since the previous report.");
        if (notice.Gains.Length == 0) text.AppendLine("No research gained.");
        foreach (var group in notice.Gains.GroupBy(g => (g.ProjectId, g.Repeat)))
        {
            var gains = group.ToArray();
            var first = gains[0];
            var last = gains[^1];
            var contiguous = true;
            for (int i = 1; i < gains.Length; i++)
                contiguous &= gains[i].Before == gains[i - 1].After && gains[i].Size == first.Size;

            double applied = gains.Sum(g => 100d * ((double)g.After - g.Before) / g.Size);
            text.Append(first.Name.Replace('\r', ' ').Replace('\n', ' '));
            if (first.Repeat > 0) text.Append(" (repeat ").Append(first.Repeat).Append(')');
            if (gains.Any(g => g.Completed)) text.Append(" [Complete]");
            if (gains.Any(g => g.RacialBonus)) text.Append(" [Racial bonus]");
            text.Append(ColumnSeparator);
            if (contiguous) text.Append(Percent(Share(first.Before, first.Size))).Append(" → ")
                .Append(Percent(Share(last.After, last.Size))).Append("    ");
            string formatted = Percent(applied);
            text.Append('+').Append(formatted == "0.0%" ? "<0.1%" : formatted);
            text.AppendLine();
        }
        return text.Append(Token(ledger, notice)).ToString();
    }

    internal static string? VisibleText(EmpireMessage? message)
    {
        if (message?.Text is not { } text || message.Type != EmpireMessageType.Research ||
            message.SubType != EmpireMessageSubType.Undefined || message.Title == null ||
            !(message.Title.StartsWith("Retirement research: ", StringComparison.Ordinal) ||
              message.Title.StartsWith("Partial retirement research: ", StringComparison.Ordinal))) return message?.Text;

        int end = text.LastIndexOf('\n');
        if (end < 0) return text;
        var receipt = text.AsSpan(end + 1);
        return receipt.StartsWith("[RRR:", StringComparison.Ordinal) && receipt.EndsWith("]", StringComparison.Ordinal)
            ? text[..end].TrimEnd()
            : text;
    }
    internal static bool Publish(Empire owner, Ledger ledger, Notice notice)
    {
        string token = Token(ledger, notice);
        if (owner.Messages?.Any(m => m != null && m.Text?.Contains(token, StringComparison.Ordinal) == true) == true) return true;
        if (owner.Messages == null || owner.EvaluationQueue == null) return false;
        var message = new EmpireMessage(owner.EmpireId, EmpireMessageType.Research, EmpireMessageSubType.Undefined, false,
            notice.Date, (notice.Complete ? "Retirement research: " : "Partial retirement research: ") + ledger.Name,
            Text(ledger, notice), null!, DrawDetailType.Undefined, EmpireMessagePriority.Normal);
        owner.SendMessageToEmpire(owner, message);
        return owner.Messages.Contains(message);
    }
}
