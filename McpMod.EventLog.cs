using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2EventLog;

// Local-action event log: Harmony postfixes on the engine's local-commit
// methods append {seq, action, args} to a ring buffer served at
// GET /api/v1/events?since=N. Purely additive — nothing existing changes;
// recording is passive whether or not anyone polls.
//
// STANDALONE BY DESIGN: depends only on Harmony + the engine's
// synchronizer/action layer + System. It deliberately references nothing
// in McpMod (state builders, actions, helpers); the mod is its HOST, not
// its foundation — the single seam is the /api/v1/events route in
// McpMod.cs calling BuildEventsResponse. It could be lifted into its own
// mod DLL without modification.
//
// Capture points are the core synchronizer/action layer (NOT UI handlers):
// each local choice has a dedicated *Local* method (prefix-captured) or a
// GameAction captured at EXECUTE time — the queue serializes execution, so
// each action's snapshot is engine-settled. Outcomes (executed/cancelled/
// failed) ride as slim follow-up events linked by "of".
public static class EventLog
{
    /// <summary>
    /// Optional state snapshotter, injected by the HOST at init (dependency
    /// inversion — EventLog never references the host's builder). When set,
    /// each ACTION event carries the state the player acted from (S A
    /// model), enabling game→sim comparison per action. Must be safe to
    /// call on the game's main thread.
    /// </summary>
    public static Func<Dictionary<string, object?>?>? StateProvider;

    private const int EventLogCapacity = 512;
    private static readonly object _eventLock = new();
    private static readonly List<Dictionary<string, object?>> _eventLog = new();
    private static long _eventSeq;

    private static void RecordEvent(string action, Dictionary<string, object?>? args = null,
                                    bool includeState = false)
    {
        try
        {
            // Snapshot OUTSIDE the lock (state builds read live objects).
            var state = includeState ? TryGet(() => StateProvider?.Invoke()) : null;
            lock (_eventLock)
            {
                var entry = new Dictionary<string, object?>
                {
                    ["seq"] = ++_eventSeq,
                    ["action"] = action,
                    ["args"] = args ?? new Dictionary<string, object?>(),
                };
                if (state != null)
                    entry["state"] = state;
                if (includeState)
                    entry["extras"] = TryGet(BuildExtras) ?? new Dictionary<string, object?>();
                _eventLog.Add(entry);
                if (_eventLog.Count > EventLogCapacity)
                    _eventLog.RemoveRange(0, _eventLog.Count - EventLogCapacity);
            }
        }
        catch { }
    }

    public static Dictionary<string, object?> BuildEventsResponse(long since, int? last = null)
    {
        List<Dictionary<string, object?>> events;
        long next;
        lock (_eventLock)
        {
            events = _eventLog.Where(e => (long)e["seq"]! > since).ToList();
            if (last is > 0)
                events = events.Skip(Math.Max(0, events.Count - last.Value)).ToList();
            next = _eventSeq;
        }
        return new Dictionary<string, object?>
        {
            ["events"] = events,
            ["next"] = next,
        };
    }

    // The engine reports non-combat action outcomes as Task<bool> return
    // values — attach a continuation so failures are logged like GameAction
    // cancellations. Unconditional: passive logging, nothing to opt into.
    private static void RecordTaskResult(System.Threading.Tasks.Task<bool>? task)
    {
        if (task == null)
            return;
        long seq = _eventSeq;
        task.ContinueWith(t => RecordEvent("action_result",
            new Dictionary<string, object?>
            {
                ["of"] = seq,
                ["outcome"] = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                    ? (t.Result ? "executed" : "failed")
                    : "faulted",
            }),
            System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }

    // Model: S A — each ACTION event carries the state the player acted
    // FROM (captured in a prefix, before execution). A₁'s effect is A₂'s S;
    // results are slim outcome markers. run_ended will carry the final
    // state (later).
    // Engine-read parity fields attached beside every snapshot. These are
    // OURS to extend — the comparison channel's schema lives here, not in
    // the mod's public GET API.
    private static Dictionary<string, object?> BuildExtras()
    {
        var extras = new Dictionary<string, object?>();
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            extras["seed"] = TryGet(() => runState?.Rng.StringSeed);
            var me = TryGet(() => runState == null ? null : LocalContext.GetMe(runState)) as Player;
            extras["can_act"] = TryGet(() =>
                (object?)(me?.PlayerCombatState?.Phase
                    == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play
                 && MegaCrit.Sts2.Core.Combat.CombatManager.Instance is { IsInProgress: true } cm
                 && !cm.PlayerActionsDisabled));
        }
        catch { }
        return extras;
    }

    private static bool IsLocal(Player? player)
    {
        try
        {
            return player != null && LocalContext.IsMe(player);
        }
        catch
        {
            return true;  // singleplayer default: everything is local
        }
    }

    // ── GameActions (combat): capture at EXECUTE time, not enqueue ─────────
    // The action queue serializes execution — A2's ExecuteAction only runs
    // after A1 fully finished — so a prefix here sees an engine-settled S
    // regardless of how fast the player clicked. Actions cancelled before
    // execution never appear (correct for replay: they didn't happen).
    // Outcome still via the action's own completion events.

    private static void RecordGameAction(GameAction action, string name,
                                         Dictionary<string, object?> args)
    {
        RecordEvent(name, args, includeState: true);
        long seq = _eventSeq;
        action.AfterFinished += _ => RecordEvent("action_result",
            new Dictionary<string, object?> { ["of"] = seq, ["outcome"] = "executed" });
        action.BeforeCancelled += _ => RecordEvent("action_result",
            new Dictionary<string, object?> { ["of"] = seq, ["outcome"] = "cancelled" });
    }

    // Queue drops: an action cancelled BEFORE it ever executed leaves no
    // trace in the S-A stream (correct for replay) — record a slim explicit
    // marker so a driving client never has to infer failure from absence.
    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
    private static class EventLog_QueueDrop
    {
        private static void Postfix(GameAction action)
        {
            try
            {
                string? name = action switch
                {
                    PlayCardAction pc when IsLocal(pc.Player) => "play_card",
                    UsePotionAction up when IsLocal(up.Player) => "use_potion",
                    EndPlayerTurnAction => "end_turn",
                    DiscardPotionGameAction => "discard_potion",
                    _ => null,
                };
                if (name == null)
                    return;
                bool started = false;
                action.BeforeExecuted += _ => started = true;
                action.BeforeCancelled += _ =>
                {
                    if (!started)
                        RecordEvent("action_dropped",
                            new Dictionary<string, object?> { ["dropped"] = name });
                };
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    private static class EventLog_PlayCard
    {
        private static void Prefix(PlayCardAction __instance)
        {
            try
            {
                if (!IsLocal(__instance.Player))
                    return;
                RecordGameAction(__instance, "play_card", new Dictionary<string, object?>
                {
                    ["card_id"] = __instance.CardModelId.Entry,
                    ["combat_card_index"] = TryGet(() => (object?)__instance.NetCombatCard.CombatCardIndex),
                    ["target_combat_id"] = __instance.TargetId,
                });
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
    private static class EventLog_EndTurn
    {
        private static void Prefix(EndPlayerTurnAction __instance)
        {
            try
            {
                RecordGameAction(__instance, "end_turn", new Dictionary<string, object?>());
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(UsePotionAction), "ExecuteAction")]
    private static class EventLog_UsePotion
    {
        private static void Prefix(UsePotionAction __instance)
        {
            try
            {
                if (!IsLocal(__instance.Player))
                    return;
                RecordGameAction(__instance, "use_potion", new Dictionary<string, object?>
                {
                    ["slot"] = __instance.PotionIndex,
                    ["target_combat_id"] = __instance.TargetId,
                });
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
    private static class EventLog_DiscardPotion
    {
        private static void Prefix(DiscardPotionGameAction __instance)
        {
            try
            {
                RecordGameAction(__instance, "discard_potion", new Dictionary<string, object?>
                {
                    ["slot"] = GetField(__instance, "_potionSlotIndex"),
                });
            }
            catch { }
        }
    }

    // Map choice: merged sink for every player's vote; filter to local.
    [HarmonyPatch(typeof(MapSelectionSynchronizer), "PlayerVotedForMapCoord")]
    private static class EventLog_MapVote
    {
        private static void Prefix(Player player, MapVote? destination)
        {
            try
            {
                if (!IsLocal(player) || destination == null)
                    return;
                var coord = destination.Value.coord;
                RecordEvent("choose_map_node", new Dictionary<string, object?>
                {
                    ["col"] = coord.col,
                    ["row"] = coord.row,
                }, includeState: true);
            }
            catch { }
        }
    }

    // Run end: the engine's authoritative victory signal (the win path
    // kills all players, so HP can never distinguish win from loss).
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
    private static class EventLog_RunEnded
    {
        private static void Postfix(bool isVictory) =>
            RecordEvent("run_ended",
                new Dictionary<string, object?> { ["victory"] = isVictory });
    }

    // ── Non-combat local choices: dedicated *Local* methods ─────────────────

    [HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
    private static class EventLog_EventOption
    {
        private static void Prefix(int index) =>
            RecordEvent("choose_event_option",
                new Dictionary<string, object?> { ["index"] = index }, includeState: true);
    }

    [HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
    private static class EventLog_RestOption
    {
        private static void Prefix(int index) =>
            RecordEvent("choose_rest_option",
                new Dictionary<string, object?> { ["index"] = index }, includeState: true);

        private static void Postfix(System.Threading.Tasks.Task<bool> __result) =>
            RecordTaskResult(__result);
    }

    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward))]
    private static class EventLog_RewardClaim
    {
        private static void Prefix(Reward reward)
        {
            try
            {
                RecordEvent("claim_reward", DescribeReward(reward), includeState: true);
            }
            catch { }
        }

        private static void Postfix(System.Threading.Tasks.Task<bool> __result) =>
            RecordTaskResult(__result);
    }

    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet))]
    private static class EventLog_RewardSkip
    {
        private static void Prefix() => RecordEvent("skip_rewards", includeState: true);
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally))]
    private static class EventLog_TreasurePick
    {
        private static void Prefix(int? index) =>
            RecordEvent("treasure_pick",
                new Dictionary<string, object?> { ["index"] = index }, includeState: true);
    }

    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
    private static class EventLog_ShopPurchase
    {
        private static void Prefix(MerchantEntry __instance)
        {
            try
            {
                RecordEvent("shop_purchase", new Dictionary<string, object?>
                {
                    ["category"] = __instance.GetType().Name,
                    ["cost"] = __instance.Cost,
                    ["id"] = DescribeMerchantEntry(__instance),
                }, includeState: true);
            }
            catch { }
        }

        private static void Postfix(System.Threading.Tasks.Task<bool> __result) =>
            RecordTaskResult(__result);
    }

    // Card-selection results (deck pickers, card rewards, hand selects):
    // generic seam — the result carries indices and/or cards; screen
    // correlation happens client-side against the last shown decision.
    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
    private static class EventLog_PlayerChoice
    {
        private static void Prefix(Player player, uint choiceId, PlayerChoiceResult result)
        {
            try
            {
                if (!IsLocal(player))
                    return;
                RecordEvent("player_choice", new Dictionary<string, object?>
                {
                    ["choice_id"] = choiceId,
                    ["choice_type"] = result.ChoiceType.ToString(),
                    ["indexes"] = TryGet(() =>
                        (GetField(result, "_indexes") as List<int>)?.Cast<object?>().ToList()),
                    ["card_ids"] = TryGet(() => DescribeChoiceCards(result)),
                }, includeState: true);
            }
            catch { }
        }
    }

    // ── payload helpers ─────────────────────────────────────────────────────

    private static object? TryGet(Func<object?> get)
    {
        try { return get(); }
        catch { return null; }
    }

    private static object? GetField(object obj, string name)
    {
        try
        {
            return obj.GetType().GetField(name,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)?.GetValue(obj);
        }
        catch { return null; }
    }

    private static Dictionary<string, object?> DescribeReward(Reward reward)
    {
        var args = new Dictionary<string, object?>
        {
            ["reward_type"] = reward.GetType().Name,
        };
        switch (reward)
        {
            case GoldReward gr:
                args["amount"] = gr.Amount;
                break;
            case RelicReward rr:
                args["id"] = TryGet(() => rr.Relic?.Id.Entry);
                break;
            case PotionReward pr:
                args["id"] = TryGet(() => pr.Potion?.Id.Entry);
                break;
        }
        return args;
    }

    private static string? DescribeMerchantEntry(MerchantEntry entry)
    {
        return entry switch
        {
            MerchantCardEntry ce => TryGet(() => ce.CreationResult?.Card.Id.Entry) as string,
            MerchantRelicEntry re => TryGet(() => re.Model?.Id.Entry) as string,
            MerchantPotionEntry pe => TryGet(() => pe.Model?.Id.Entry) as string,
            MerchantCardRemovalEntry => "card_removal",
            _ => null,
        };
    }

    private static List<object?>? DescribeChoiceCards(PlayerChoiceResult result)
    {
        var cards = GetField(result, "_cards")
            as System.Collections.IEnumerable;
        if (cards == null)
            return null;
        var ids = new List<object?>();
        foreach (var c in cards)
        {
            if (c is MegaCrit.Sts2.Core.Models.CardModel cm)
                ids.Add(cm.Id.Entry);
        }
        return ids;
    }
}
