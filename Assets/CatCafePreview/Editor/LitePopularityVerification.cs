using System;
using System.IO;
using System.Linq;
using UnityEngine;
using CatCafePreview.Standalone;

namespace CatCafePreview.Editor
{
    public static class LitePopularityVerification
    {
        public static void Run()
        {
            string output = Environment.GetEnvironmentVariable("CATCAFE_LITE_QA");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("Set CATCAFE_LITE_QA.");
            var failures = new System.Collections.Generic.List<string>();
            foreach (int seed in new[] { 0, 1, 42 })
            {
                try { Play(seed, Path.Combine(output, "seed-" + seed)); }
                catch (Exception ex)
                {
                    failures.Add("seed=" + seed + " " + ex.Message);
                    Debug.LogWarning(ex.ToString());
                }
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
            Debug.Log("CAT_CAFE_LITE_POPULARITY_OK");
        }

        private static void Play(int seed, string profile)
        {
            var backend = new LocalCafeBackend(profile);
            var sim = backend.Simulation;
            sim.Restore(new CafeSimulation(sim.Rules, seed, "popularity").State);
            string lastDay = "";
            bool vipScheduled = false;
            for (int operation = 0; operation < 12000; operation++)
            {
                var state = sim.State;
                var stage = state.goal.Last();
                if (state.game_over != null || stage.status == "expired")
                    throw new InvalidOperationException($"day={state.day} stage={state.goal.Count} funds={state.funds} popularity={state.popularity} status={stage.status} game_over={state.game_over}");
                if (state.phase != "open") FinishResultConversations(backend);
                if (sim.GrowthWaiting)
                {
                    sim.Execute("resolve_growth_auto");
                    sim.Execute("resolve_growth_batch");
                    continue;
                }
                var missing = state.cats.FirstOrDefault(cat => cat.activity == "missing" && cat.missing_remaining == 0);
                if (missing != null) { sim.Execute("resolve_missing", catId: missing.id); continue; }
                if (sim.StoreEventWaiting) { sim.Execute("resolve_store_event", choice: sim.CurrentStoreEvent.type == "trouble" ? "patch" : "decline"); continue; }
                if (sim.CustomerTrustWaiting)
                {
                    var trust = state.trust_events.First(row => row.status == "waiting");
                    sim.Execute("resolve_customer_trust", source: trust.id, choice: "recover");
                    continue;
                }
                if (sim.ReservationWaiting) { sim.Execute("resolve_reservation", choice: "decline"); continue; }
                if (sim.GoalWaiting)
                {
                    Debug.Log($"LITE_STAGE seed={seed} stage={state.goal.Count} day={state.day} funds={state.funds} popularity={state.popularity}");
                    if (state.goal.Count == sim.Rules.goal_stages.Length)
                    {
                        var ending = backend.PopularityEnding;
                        ending.progress.node = CafePopularityEnding.Lines(CafePopularityEnding.Record(state), "猫").Length;
                        CafePopularityEnding.Finish(ending.progress);
                        backend.RememberViewedConversations();
                    }
                    StandaloneDispatchConversationSmoke.Good(backend, "continue_goal");
                    continue;
                }
                if (stage.status == "cleared" && state.goal.Count < sim.Rules.goal_stages.Length)
                {
                    sim.Execute("advance_goal");
                    continue;
                }
                var schedule = sim.CustomerSchedule(state.day);
                if (schedule.Any(row => row.customer_id == "vip-calico")) vipScheduled = true;
                if (state.visits.Any(row => row.id == "vip-calico"))
                {
                    if (!vipScheduled || !stage.continued) throw new InvalidOperationException("Unexpected VIP or continuation state.");
                    if (state.phase == "closed")
                    {
                        StandaloneDispatchConversationSmoke.Good(backend, "save_game", slot: 8);
                        var reopened = new LocalCafeBackend(profile);
                        StandaloneDispatchConversationSmoke.Good(reopened, "load_game", slot: 8);
                        if (!reopened.Simulation.State.goal.Last().continued ||
                            !reopened.Simulation.State.visits.Any(row => row.id == "vip-calico"))
                            throw new InvalidOperationException("Save did not restore continued business with the VIP.");
                        Debug.Log($"LITE_VIP seed={seed} day={state.day} funds={state.funds} popularity={state.popularity}");
                        return;
                    }
                }
                string dayPhase = state.day + ":" + state.phase;
                if (lastDay != dayPhase && state.phase == "closed")
                {
                    Debug.Log($"LITE_DAY seed={seed} day={state.day} funds={state.funds} popularity={state.popularity}");
                    lastDay = dayPhase;
                }
                var next = sim.PlanAutoplay("popularity", "fast");
                sim.Execute(next.kind, next.working, next.cat_id, next.choice, next.source, next.seat_id);
            }
            throw new InvalidOperationException("Operation limit reached.");
        }

        private static void FinishResultConversations(LocalCafeBackend backend)
        {
            foreach (var row in backend.EquipmentConversations.Where(row => !row.progress.completed && row.progress.branch_id != "introduction"))
            {
                row.progress.node = CafeEquipmentDialogue.Lines(row, row.progress.branch_id).Length;
                CafeEquipmentDialogue.Finish(row);
            }
            foreach (var row in backend.TrustConversations.Where(CafeTrustDialogue.Pending))
            {
                while (CafeTrustDialogue.Pending(row))
                {
                    var progress = row.Current;
                    progress.node = CafeTrustDialogue.Lines(progress == row.result, progress.branch_id,
                        "", backend.Simulation.Rules.trust.popularity_loss).Length;
                    CafeTrustDialogue.Finish(row);
                }
            }
            foreach (var row in backend.ReservationConversations)
            {
                while (row.Current != null && !row.Current.completed && row.Current.branch_id != "introduction")
                {
                    var progress = row.Current;
                    progress.node = CafeReservationDialogue.Lines(row, progress == row.result, progress.branch_id).Length;
                    CafeReservationDialogue.Finish(row);
                }
            }
            foreach (var row in backend.Simulation.State.missing_cases.Where(row => row.result != null && !row.result.completed))
            {
                row.result.node = CafeMissingDialogue.Lines(row, "result", backend.Simulation.Definition(row.cat_id).name).Length;
                CafeMissingDialogue.Finish(row.result);
            }
            backend.RememberViewedConversations();
        }
    }
}
