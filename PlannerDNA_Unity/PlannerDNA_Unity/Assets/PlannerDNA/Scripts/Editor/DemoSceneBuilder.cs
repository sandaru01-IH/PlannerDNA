using System.Collections.Generic;
using System.IO;
using PlannerDNA.App;
using PlannerDNA.Networking;
using PlannerDNA.Rendering;
using PlannerDNA.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PlannerDNA.EditorTools
{
    /// <summary>
    /// Menu: PlannerDNA > Create or Repair Demo Scene.
    /// Creates Assets/PlannerDNA/Scenes/PlannerDNA_Main.unity if missing; if it exists, opens it and only
    /// adds missing objects/components and re-wires references, so intentional edits are preserved.
    /// Also creates the material palette and UI PanelSettings assets, and registers the scene in Build Settings.
    /// Command line: Unity.exe -batchmode -quit -projectPath ... -executeMethod PlannerDNA.EditorTools.DemoSceneBuilder.BuildFromCommandLine
    /// </summary>
    public static class DemoSceneBuilder
    {
        public const string Root = "Assets/PlannerDNA";
        public const string ScenePath = Root + "/Scenes/PlannerDNA_Main.unity";
        private const string MaterialsDir = Root + "/Materials";
        private const string PalettePath = MaterialsDir + "/PlannerDNA_MaterialPalette.asset";
        private const string PanelSettingsPath = Root + "/UI/PlannerDNA_PanelSettings.asset";
        private const string ThemePath = Root + "/UI/PlannerDNA_Theme.tss";
        private const string UxmlPath = Root + "/UI/PlannerDNA_Main.uxml";

        [MenuItem("PlannerDNA/Create or Repair Demo Scene", priority = 1)]
        public static void CreateOrRepairMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string report = Build();
            EditorUtility.DisplayDialog("PlannerDNA", report, "OK");
        }

        [MenuItem("PlannerDNA/Open Demo Scene", priority = 2)]
        public static void OpenSceneMenu()
        {
            if (!File.Exists(ScenePath)) { CreateOrRepairMenu(); return; }
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        public static void BuildFromCommandLine()
        {
            Debug.Log("[PlannerDNA] " + Build());
        }

        public static string Build()
        {
            var log = new List<string>();
            EnsureFolder(Root, "Scenes");
            EnsureFolder(Root, "Materials");

            // Assets are loaded after the scene switch: opening a scene in Single mode unloads
            // unreferenced assets, which would leave earlier references destroyed.
            Scene scene;
            if (File.Exists(ScenePath))
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                log.Add("Opened existing scene (repair mode: only missing parts are added).");
            }
            else
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                log.Add("Created new scene.");
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.66f, 0.66f, 0.64f);
            }

            MaterialPalette palette = EnsurePalette(log);
            PanelSettings panel = EnsurePanelSettings(log);
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null) throw new FileNotFoundException("Missing UI layout " + UxmlPath);

            Camera cam = EnsureCamera(log);
            EnsureSun(log);

            GameObject appGo = FindOrCreate("PlannerDNA", log);
            PlannerApp app = GetOrAdd<PlannerApp>(appGo, log);
            ParcelPicker picker = GetOrAdd<ParcelPicker>(appGo, log);
            HealthMonitor health = GetOrAdd<HealthMonitor>(appGo, log);
            SyncService sync = GetOrAdd<SyncService>(appGo, log);

            GameObject cityGo = FindOrCreate("City", log);
            GridRenderer grid = GetOrAdd<GridRenderer>(cityGo, log);

            GameObject uiGo = FindOrCreate("UI", log);
            UIDocument doc = GetOrAdd<UIDocument>(uiGo, log);
            SetRef(doc, "m_PanelSettings", panel);
            SetRef(doc, "sourceAsset", uxml);
            SandboxUi ui = GetOrAdd<SandboxUi>(uiGo, log);

            CameraRig rig = GetOrAdd<CameraRig>(cam.gameObject, log);

            SetRef(grid, "palette", palette);
            SetRef(picker, "targetCamera", cam);
            SetRef(app, "grid", grid);
            SetRef(app, "cameraRig", rig);
            SetRef(app, "picker", picker);
            SetRef(app, "ui", ui);
            SetRef(app, "health", health);
            SetRef(app, "sync", sync);
            SetRef(sync, "health", health);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            RegisterInBuildSettings(log);
            AssetDatabase.SaveAssets();
            log.Add("Saved " + ScenePath + ". Press Play to run.");
            return string.Join("\n", log);
        }

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
        }

        private static MaterialPalette EnsurePalette(List<string> log)
        {
            var palette = AssetDatabase.LoadAssetAtPath<MaterialPalette>(PalettePath);
            if (palette != null && palette.IsComplete) return palette;

            if (palette == null)
            {
                palette = ScriptableObject.CreateInstance<MaterialPalette>();
                AssetDatabase.CreateAsset(palette, PalettePath);
            }
            palette.PopulateDefaults(MaterialPalette.FindLitShader());
            var so = new SerializedObject(palette);
            SerializedProperty prop = so.GetIterator();
            prop.NextVisible(true);
            while (prop.NextVisible(false))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                var mat = prop.objectReferenceValue as Material;
                if (mat == null) continue;
                string path = $"{MaterialsDir}/{mat.name}.mat";
                var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (existing == null)
                {
                    AssetDatabase.CreateAsset(mat, path);
                    existing = mat;
                }
                prop.objectReferenceValue = existing;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(palette);
            log.Add("Created material palette and materials in " + MaterialsDir + ".");
            return palette;
        }

        private static PanelSettings EnsurePanelSettings(List<string> log)
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1920, 1080);
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                panel.match = 0.5f;
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
                log.Add("Created UI PanelSettings.");
            }
            if (panel.themeStyleSheet == null)
            {
                panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
                EditorUtility.SetDirty(panel);
            }
            return panel;
        }

        private static Camera EnsureCamera(List<string> log)
        {
            Camera cam = Object.FindAnyObjectByType<Camera>();
            if (cam != null) return cam;
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.95f, 0.94f, 0.92f);
            go.AddComponent<AudioListener>();
            log.Add("Added orthographic Main Camera.");
            return cam;
        }

        private static void EnsureSun(List<string> log)
        {
            foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
                if (l.type == LightType.Directional) return;
            var go = new GameObject("Sun");
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.color = new Color(1f, 0.97f, 0.92f);
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.55f;
            go.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            log.Add("Added directional light.");
        }

        private static GameObject FindOrCreate(string name, List<string> log)
        {
            GameObject go = GameObject.Find(name);
            if (go != null) return go;
            log.Add("Added GameObject '" + name + "'.");
            return new GameObject(name);
        }

        private static T GetOrAdd<T>(GameObject go, List<string> log) where T : Component
        {
            T c = go.GetComponent<T>();
            if (c != null) return c;
            log.Add($"Added {typeof(T).Name} to '{go.name}'.");
            return go.AddComponent<T>();
        }

        private static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) throw new MissingFieldException(target.GetType().Name, field);
            Object current = p.objectReferenceValue;
            // Unity can inject transient defaults (e.g. a runtime PanelSettings) that are never saved;
            // only a reference to a saved asset or a scene object counts as an intentional assignment.
            bool intentional = current != null && (EditorUtility.IsPersistent(current) || current is Component || current is GameObject);
            if (intentional) return;
            if (current != null) Debug.Log($"[PlannerDNA] Replacing transient {current.GetType().Name} in {target.GetType().Name}.{field}");
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RegisterInBuildSettings(List<string> log)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            int index = scenes.FindIndex(s => s.path == ScenePath);
            if (index == 0 && scenes[0].enabled) return;
            if (index > 0) scenes.RemoveAt(index);
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            log.Add("Registered the scene first in Build Settings (other scenes kept).");
        }

        private sealed class MissingFieldException : System.Exception
        {
            public MissingFieldException(string type, string field) : base($"{type} has no serialized field '{field}'.") { }
        }
    }
}
