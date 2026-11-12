using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CatCafePreview.Editor
{
    public static class LiteVerification
    {
        private const string VerificationPath = "CatCafeSimLite.VerificationPath";
        private static readonly string[] Cats = { "cat-mike", "cat-sora", "cat-tama" };

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--catcafe-optional-event");
            Check(index >= 0 && index + 1 < args.Length, "Supply a verification output directory.");
            string output = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(output);

            var narrative = CafeNarrativeResources.Load();
            Check(narrative.cat_bindings.Length == 3 && narrative.entries.Length == 366, "Lite narrative catalog");
            Check(CafeOptionalEventResources.Load().events.Length == 15, "Fifteen optional events");
            var pointer = JsonUtility.FromJson<ContentCatalog>(Resources.Load<TextAsset>("CatCafeContent/catalog").text);
            var bundle = JsonUtility.FromJson<ContentBundle>(Resources.Load<TextAsset>(pointer.bundleResource).text);
            ContentValidation.Validate(bundle);
            string imageRoot = pointer.bundleResource.Substring(0, pointer.bundleResource.LastIndexOf('/') + 1);
            var images = bundle.cats.SelectMany(cat => cat.scenes.Select(scene => scene.image))
                .Concat(narrative.image_bindings.Select(row => row.image))
                .Where(image => !string.IsNullOrEmpty(image)).Distinct().ToArray();
            Check(images.Length == 90 && images.All(image => Resources.Load<Texture2D>(
                imageRoot + Path.ChangeExtension(image, null)) != null), "Ninety cat textures resolve");
            var guests = Enumerable.Range(1, 8).Select(i => "guest-" + i)
                .Concat(new[] { "advanced-white", "advanced-longhair", "reservation-longhair", "vip-calico" });
            Check(guests.All(id => HumanPortraitResources.Resolve(id) != HumanPortraitResources.Placeholder()), "Twelve portraits resolve");

            var backend = new LocalCafeBackend(Path.Combine(output, "business-profile"));
            var view = backend.View();
            PythonStateClient.Validate(view);
            Check(view.cats.Select(cat => cat.cat_id).OrderBy(id => id).SequenceEqual(Cats), "Initial roster");
            Check(view.new_game_options.Select(row => row.choice).OrderBy(id => id)
                .SequenceEqual(new[] { "bond", "free", "popularity" }), "Lite start objectives");
            StandaloneDispatchConversationSmoke.Good(backend, "new_game", "free");
            var sim = backend.Simulation;
            if (sim.StoreEventWaiting)
                StandaloneDispatchConversationSmoke.Good(backend, "resolve_store_event",
                    sim.CurrentStoreEvent.type == "trouble" ? "patch" : "decline");
            StandaloneDispatchConversationSmoke.Good(backend, "set_shifts");
            StandaloneDispatchConversationSmoke.Good(backend, "start_business");
            while (sim.State.phase == "open")
                StandaloneDispatchConversationSmoke.Good(backend, "advance_business");
            Check(sim.State.phase == "closed" && sim.State.visits.Count > 0, "Completed a day with guests");
            StandaloneDispatchConversationSmoke.Good(backend, "save_game", slot: 8);
            var reopened = new LocalCafeBackend(Path.Combine(output, "business-profile"));
            StandaloneDispatchConversationSmoke.Good(reopened, "load_game", slot: 8);
            Check(reopened.Simulation.State.phase == "closed", "Closed business resumes in a new backend");

            foreach (string path in StandaloneScreenSetup.Scenes)
            {
                var scene = EditorSceneManager.OpenScene(path);
                Check(scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .All(node => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject) == 0),
                    "Missing scripts in " + path);
            }
            EditorSceneManager.OpenScene(StandaloneScreenSetup.Scenes[0]);
            Debug.Log("CAT_CAFE_LITE_EDITOR_CHECKS_OK");
            SessionState.SetString(VerificationPath, output);
            WatchPlayMode();
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void WatchPlayMode()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(VerificationPath, "")))
            {
                EditorApplication.update -= ReadPlayModeResult;
                EditorApplication.update += ReadPlayModeResult;
            }
        }

        private static void ReadPlayModeResult()
        {
            string output = SessionState.GetString(VerificationPath, "");
            if (string.IsNullOrEmpty(output)) return;
            string failure = Path.Combine(output, "failure.txt");
            string result = Path.Combine(output, "result.txt");
            if (!File.Exists(failure) && !File.Exists(result)) return;
            bool passed = !File.Exists(failure);
            SessionState.EraseString(VerificationPath);
            Debug.Log(File.ReadAllText(passed ? result : failure));
            EditorApplication.Exit(passed ? 0 : 1);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
