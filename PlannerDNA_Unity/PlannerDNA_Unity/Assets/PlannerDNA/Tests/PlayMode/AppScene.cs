using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlannerDNA.Tests
{
    public static class AppScene
    {
        public const string Name = "PlannerDNA_Main";

        /// <summary>
        /// Destroys the app's objects so its coroutines stop before the test deletes their folders.
        /// Only touches the app scene: when a test was ignored before loading it, the active scene is the
        /// test runner's own, and destroying that scene's objects would stop the test run.
        /// </summary>
        public static void DestroyIfLoaded()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != Name) return;
            foreach (GameObject go in scene.GetRootGameObjects()) Object.Destroy(go);
        }
    }
}
