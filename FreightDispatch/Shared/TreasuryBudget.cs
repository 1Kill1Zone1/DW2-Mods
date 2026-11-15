using DistantWorlds.Types;

namespace DW2Mod;

// The client of the treasury budget (War Chest's service, today Freighter Logistics' Budget.cs; the rules and the reasons for them are in docs/Unified Budget.md). Every
// XL mod that starts something costly asks here first and tells it when the thing was started, so that facilities, troops and ships are counted against one treasury and
// one view of income and upkeep.
//
// Ask when needed, never once at start-up (the provider may load after the consumer). Three states, told apart by `provided`:
//   provided and ready:        the answer is the budget's.
//   not provided at all:       nobody offers the budget (it is not installed, or switched off): the player's choice. `provided` is false and the caller falls back to the
//                              game's own rule for that feature, and says so (MissingReason).
//   offered but not usable:    the provider's contract version or delegate type is not the one this client states (the loader has said so on the status line). `provided`
//                              is true and the answer is no: refusal text from Ask, no room from Limits.
//
// Level and relief cross the channels as ints with the values below, since each mod compiles its own copies of any enum. Every delegate is built from BCL and game types
// only.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class TreasuryBudget
{
    // Ship: bought by the state (constructors, warships). Private: bought by the civilian economy (freighters, mining stations, passenger
    // ships), which has its own money, income and cash flow; Empire.DetermineShipPurchaseCostIsPrivate(role) says which a ship role is.
    public const int Ship = 0, Troop = 1, Facility = 2, Private = 3;

    // what a purchase relieves, so it may go below the whole floor while that stress holds (flags)
    public const int ReliefNone = 0, ReliefIncome = 1, ReliefResource = 2, ReliefWar = 4;

    // how far a stress has gone
    public const int LevelNone = 0, LevelStress = 1, LevelCrisis = 2;

    public const string AskChannel = "xl.budget.ask";
    // the same question, answered with the reason twice: in English for the log, and as a whole sentence in the player's language for what they read
    public const string AskShownChannel = "xl.budget.askShown";
    public const string CommitChannel = "xl.budget.commit";
    public const string LimitsChannel = "xl.budget.limits";
    public const string LevelsChannel = "xl.budget.levels";
    public const string RunwayChannel = "xl.budget.runway";
    public const string ReserveChannel = "xl.budget.reserve";
    public const string ClearReserveChannel = "xl.budget.clearReserve";
    public const string FloorChannel = "xl.budget.floor";
    public const string NoteChannel = "xl.budget.note";
    public const string ShortageChannel = "xl.budget.shortage";
    // asked by the budget, answered by the mods that make the requests
    public const string ClassifyChannel = "xl.budget.classify";
    public const string DescribeChannel = "xl.budget.task.describe";
    public const string PriceChannel = "xl.budget.task.price";
    public const string RefusalChannel = "xl.budget.task.refusal";
    public const string HeldChannel = "xl.budget.task.held";
    public const string MootChannel = "xl.budget.task.moot";

    // Bumped with the provider's constant (dll/DW2FreighterLogistics/Budget.cs) when a delegate's meaning changes. 2: ask gained atPayment; limits, levels, runway, reserve,
    // clearReserve, floor, note, shortage, classify and the task channels were added.
    public const int ChannelVersion = 2;

    // the stock runway a gate without figures asks for: the longest, as the budget's own setting BudgetMonthsLarge defaults to
    public const float LongestRunwayMonths = 18f;

    // null: allowed. Otherwise why not. `provided` is false when no mod offers the budget (the answer is then null and means nothing). `upkeep` and `credit` are yearly
    // amounts; `credit` is the yearly income the thing is expected to add. `runwayYears` is how long the treasury must last at a negative flow, for military things in
    // peace. `atPayment`: the task is being paid now, so only the money is judged.
    public static string Ask(Empire empire, int category, float money, float upkeep, float credit, bool military, float runwayYears, out bool provided, bool atPayment = false)
    {
        var ask = Find<Func<Empire, int, float, float, float, bool, float, bool, string>>(AskChannel, out provided, out string refusal);
        return ask == null ? refusal : ask(empire, category, money, upkeep, credit, military, runwayYears, atPayment);
    }

    // Ask, with the reason also in the player's language (the budget's own, a whole sentence that the requester may place in its own). A budget that does not offer the
    // shown form (an older War Chest) gives the English as both.
    public static (string Why, string Shown) AskShown(Empire empire, int category, float money, float upkeep, float credit, bool military, float runwayYears, out bool provided, bool atPayment = false)
    {
        var ask = LiveChannel.Get<Func<Empire, int, float, float, float, bool, float, bool, (string, string)>>(AskShownChannel, ChannelVersion);
        if (ask != null)
        {
            provided = true;
            return ask(empire, category, money, upkeep, credit, military, runwayYears, atPayment);
        }
        string why = Ask(empire, category, money, upkeep, credit, military, runwayYears, out provided, atPayment);
        return (why, why);
    }

    // The thing was started: its yearly upkeep joins the cash flow the budget reads.
    public static void Commit(Empire empire, int category, float upkeep)
    {
        LiveChannel.Get<Action<Empire, int, float>>(CommitChannel, ChannelVersion)?.Invoke(empire, category, upkeep);
    }

    // What may be spent in a category now, as the two figures the engine's own affordability tests compare with: money (the treasury less the floor of what the purchase
    // relieves, or the private economy's money less its floor) and yearly upkeep room. Not provided: no limit (the game's rule), money and upkeep unbounded.
    public static (float Money, float Upkeep) Limits(Empire empire, int category, int relief, bool rawTreasury, bool ignoreReserve, out bool provided)
    {
        var limits = Find<Func<Empire, int, int, bool, bool, (float, float)>>(LimitsChannel, out provided, out _);
        if (limits != null)
        {
            return limits(empire, category, relief, rawTreasury, ignoreReserve);
        }
        return provided ? (0f, 0f) : (float.MaxValue, float.MaxValue);
    }

    // The level of each stress now (war, economic, resource shortage) as LevelNone/LevelStress/LevelCrisis. Without an answer nothing is taken to hold.
    public static (int Economic, int War, int Resource) Levels(Empire empire, out bool provided)
    {
        var levels = Find<Func<Empire, (int, int, int)>>(LevelsChannel, out provided, out _);
        return levels == null ? (LevelNone, LevelNone, LevelNone) : levels(empire);
    }

    // the reliefs whose stress holds: a purchase with any of them is not held back by the resource block
    public static int Holding((int Economic, int War, int Resource) levels)
        => (levels.Economic != LevelNone ? ReliefIncome : 0) | (levels.Resource != LevelNone ? ReliefResource : 0) | (levels.War != LevelNone ? ReliefWar : 0);

    // How many months of stock count as enough: the budget's floor months, scaled by the empire's spending. Until its figures settle, and without an answer, the longest.
    public static float StockRunwayMonths(Empire empire, out bool provided)
    {
        var runway = Find<Func<Empire, float>>(RunwayChannel, out provided, out _);
        return runway == null ? LongestRunwayMonths : runway(empire);
    }

    // The engine pays a queued task later, so the next test of the treasury would still see the money the queued one is about to take. The path that queues a task puts its
    // public price here under its own name, and clears it when the queue has been paid. Without a budget there is nothing to reserve against.
    public static void Reserve(Empire empire, float money, string source)
    {
        LiveChannel.Get<Action<Empire, float, string>>(ReserveChannel, ChannelVersion)?.Invoke(empire, money, source);
    }

    public static void ClearReserve(Empire empire, string source)
    {
        LiveChannel.Get<Action<Empire, string>>(ClearReserveChannel, ChannelVersion)?.Invoke(empire, source);
    }

    // The whole floor now for a purchase that relieves `relief`, for a requester that shows it (a tip). NaN when no budget answers: there is no floor to show.
    public static float Floor(Empire empire, int relief)
    {
        var floor = LiveChannel.Get<Func<Empire, int, float>>(FloorChannel, ChannelVersion);
        return floor == null ? float.NaN : floor(empire, relief);
    }

    // What the payment about to be made on this thread is for ("2 x Frigate"), for the expense log; null when the thing is paid. Set by the mod that knows, around the call
    // that pays. Without a budget nobody keeps a log.
    public static void Note(string text)
    {
        LiveChannel.Get<Action<string>>(NoteChannel, ChannelVersion)?.Invoke(text);
    }

    // ---- the resource shortage: pushed by the mod that detects it, read by the budget ----

    // After each review of the empire's resources: the galaxy it was made in (the engine makes new Galaxy objects, and the budget ignores a record that is not the empire's
    // own), how far the shortage has gone (LevelNone/LevelStress/LevelCrisis), the ids of the starved resources (fuel and construction), their names and the reason, for the
    // cash tip and the log. Without a budget nobody needs it.
    public static void PushShortage(Empire empire, Galaxy galaxy, int level, short[] starvedIds, string names, string reason)
    {
        LiveChannel.Get<Action<Empire, Galaxy, int, short[], string, string>>(ShortageChannel, ChannelVersion)?.Invoke(empire, galaxy, level, starvedIds, names, reason);
    }

    // ---- what a ship is: asked by the budget, answered by the mod that decides ship purchases ----

    // What a design bought at `at` relieves (ReliefNone or flags) and the resource hold on it, if any (English for the log, and in the player's language for what they read).
    // Known is false when no mod classifies ships: the strict answer then stands, relief none and no hold.
    public static (bool Known, int Relief, string Hold, string HoldShown) Classify(Empire empire, Design design, StellarObject at)
    {
        var classify = LiveChannel.Get<Func<Empire, Design, StellarObject, (bool, int, string, string)>>(ClassifyChannel, ChannelVersion);
        return classify == null ? (false, ReliefNone, null, null) : classify(empire, design, at);
    }

    // Each provider wraps the handler it replaces and passes on what it does not know (the last mod loaded answers first).
    public static void ProvideClassify(Func<Empire, Design, StellarObject, (bool Known, int Relief, string Hold, string HoldShown)> answer)
    {
        Func<Empire, Design, StellarObject, (bool, int, string, string)> previous = null;
        previous = LiveChannel.Provide(ClassifyChannel, ChannelVersion, new Func<Empire, Design, StellarObject, (bool, int, string, string)>((empire, design, at) =>
        {
            var own = answer(empire, design, at);
            return own.Known || previous == null ? own : previous(empire, design, at);
        }));
    }

    // ---- what a task is: asked by the budget about the suggestions it keeps, answered by the mods that make them ----

    // What the task would buy, for the log; the task type's name when no mod says
    public static string TaskDescribe(Empire empire, Galaxy galaxy, GameTask task)
    {
        var (known, text) = AskTask<string>(DescribeChannel, empire, galaxy, task);
        return known ? text : task.Type.ToString();
    }

    // What the task would charge the public treasury when the engine runs it. Unknown: the suggestion is not approved on the clock.
    public static (bool Known, float Money) TaskPrice(Empire empire, Galaxy galaxy, GameTask task) => AskTask<float>(PriceChannel, empire, galaxy, task);

    // Why the budget must refuse the task now (the requester asks it and says); null when allowed. Unknown: refused, no mod judges it.
    public static (bool Known, string Why) TaskRefusal(Empire empire, Galaxy galaxy, GameTask task) => AskTask<string>(RefusalChannel, empire, galaxy, task);

    // Why a resource hold refuses the task, null when not held. Unknown: not held (the hold is the requester's own feature).
    public static (bool Known, string Why) TaskHeld(Empire empire, Galaxy galaxy, GameTask task) => AskTask<string>(HeldChannel, empire, galaxy, task);

    // Why the task no longer means anything (built, at its limit, already upgraded, accepted and waiting), null when it still does. Unknown: not moot.
    public static (bool Known, string Why) TaskMoot(Empire empire, Galaxy galaxy, GameTask task) => AskTask<string>(MootChannel, empire, galaxy, task);

    public static void ProvideDescribe(Func<Empire, Galaxy, GameTask, (bool Known, string Value)> answer) => ProvideTask(DescribeChannel, answer);
    public static void ProvidePrice(Func<Empire, Galaxy, GameTask, (bool Known, float Value)> answer) => ProvideTask(PriceChannel, answer);
    public static void ProvideRefusal(Func<Empire, Galaxy, GameTask, (bool Known, string Value)> answer) => ProvideTask(RefusalChannel, answer);
    public static void ProvideHeld(Func<Empire, Galaxy, GameTask, (bool Known, string Value)> answer) => ProvideTask(HeldChannel, answer);
    public static void ProvideMoot(Func<Empire, Galaxy, GameTask, (bool Known, string Value)> answer) => ProvideTask(MootChannel, answer);

    static (bool Known, T Value) AskTask<T>(string channel, Empire empire, Galaxy galaxy, GameTask task)
    {
        var ask = LiveChannel.Get<Func<Empire, Galaxy, GameTask, (bool, T)>>(channel, ChannelVersion);
        return ask == null ? (false, default) : ask(empire, galaxy, task);
    }

    // The channels this mod has answers for: several features of one mod may answer for their own tasks (ships, facilities), but the loader keeps one handler per mod and
    // channel and hands back a replaced handler only to another mod, so the mod registers once and that handler asks its own answers, the latest first, before the earlier
    // mod's.
    static readonly Dictionary<string, object> ownAnswers = new();

    static void ProvideTask<T>(string channel, Func<Empire, Galaxy, GameTask, (bool Known, T Value)> answer)
    {
        List<Func<Empire, Galaxy, GameTask, (bool Known, T Value)>> answers;
        lock (ownAnswers)
        {
            if (ownAnswers.TryGetValue(channel, out object kept))
            {
                ((List<Func<Empire, Galaxy, GameTask, (bool Known, T Value)>>)kept).Add(answer);
                return;
            }
            answers = new List<Func<Empire, Galaxy, GameTask, (bool Known, T Value)>> { answer };
            ownAnswers[channel] = answers;
        }
        Func<Empire, Galaxy, GameTask, (bool, T)> previous = null;
        previous = LiveChannel.Provide(channel, ChannelVersion, new Func<Empire, Galaxy, GameTask, (bool, T)>((empire, galaxy, task) =>
        {
            for (int i = answers.Count - 1; i >= 0; i--)
            {
                var own = answers[i](empire, galaxy, task);
                if (own.Known)
                {
                    return own;
                }
            }
            return previous == null ? (false, default) : previous(empire, galaxy, task);
        }));
    }

    // Says now which of the budget's channels this mod asks for, so the launcher lists the mod as a receiver of each (see LiveChannel.Expect): the asks themselves come late in
    // play, when something is bought or a suggestion is judged, and a consumer is otherwise only known once it has asked. Name the channels this mod really uses, by their constants.
    public static void ExpectConsumed(params string[] channels)
    {
        foreach (string channel in channels)
        {
            switch (channel)
            {
                case AskChannel: LiveChannel.Expect<Func<Empire, int, float, float, float, bool, float, bool, string>>(channel, ChannelVersion); break;
                case AskShownChannel: LiveChannel.Expect<Func<Empire, int, float, float, float, bool, float, bool, (string, string)>>(channel, ChannelVersion); break;
                case CommitChannel: LiveChannel.Expect<Action<Empire, int, float>>(channel, ChannelVersion); break;
                case LimitsChannel: LiveChannel.Expect<Func<Empire, int, int, bool, bool, (float, float)>>(channel, ChannelVersion); break;
                case LevelsChannel: LiveChannel.Expect<Func<Empire, (int, int, int)>>(channel, ChannelVersion); break;
                case RunwayChannel: LiveChannel.Expect<Func<Empire, float>>(channel, ChannelVersion); break;
                case ReserveChannel: LiveChannel.Expect<Action<Empire, float, string>>(channel, ChannelVersion); break;
                case ClearReserveChannel: LiveChannel.Expect<Action<Empire, string>>(channel, ChannelVersion); break;
                case FloorChannel: LiveChannel.Expect<Func<Empire, int, float>>(channel, ChannelVersion); break;
                case NoteChannel: LiveChannel.Expect<Action<string>>(channel, ChannelVersion); break;
                case ShortageChannel: LiveChannel.Expect<Action<Empire, Galaxy, int, short[], string, string>>(channel, ChannelVersion); break;
                case ClassifyChannel: LiveChannel.Expect<Func<Empire, Design, StellarObject, (bool, int, string, string)>>(channel, ChannelVersion); break;
                case PriceChannel: LiveChannel.Expect<Func<Empire, Galaxy, GameTask, (bool, float)>>(channel, ChannelVersion); break;
                case DescribeChannel:
                case RefusalChannel:
                case HeldChannel:
                case MootChannel: LiveChannel.Expect<Func<Empire, Galaxy, GameTask, (bool, string)>>(channel, ChannelVersion); break;
                default: throw new ArgumentException($"{channel} is not a treasury budget channel", nameof(channels));
            }
        }
    }

    // why the budget is missing, for the status line
    public static string MissingReason => LiveChannel.BindFailure ?? "no mod provides the treasury budget (War Chest is off or not installed), or its channel version differs (see the status line)";

    // The handler, or null with `provided` saying whether a mod offers the channel at all (false: the player's choice) and, when one does but not at this contract,
    // `refusal` as the no it answers with.
    static T Find<T>(string channel, out bool provided, out string refusal) where T : Delegate
    {
        T handler = LiveChannel.Get<T>(channel, ChannelVersion);
        string provider = handler != null ? null : LiveChannel.ProviderOf(channel);
        provided = handler != null || provider != null;
        refusal = provider == null ? null : $"{provider} offers {channel} under a contract this mod does not use (version {ChannelVersion} expected), so the budget cannot be asked";
        return handler;
    }
}
