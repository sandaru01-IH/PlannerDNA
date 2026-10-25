using System.IO;
using PlannerDNA.Storage;
using UnityEditor;
using UnityEngine;

namespace PlannerDNA.EditorTools
{
    /// <summary>
    /// Menu: PlannerDNA > Export Backend Contract Fixtures. Writes the deterministic Unity records that
    /// the backend tests replay. Command line: -executeMethod PlannerDNA.EditorTools.ContractFixtureExporter.ExportFromCommandLine
    /// </summary>
    public static class ContractFixtureExporter
    {
        public static string OutputPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", ContractFixtures.RelativePath));

        [MenuItem("PlannerDNA/Export Backend Contract Fixtures", priority = 20)]
        public static void ExportMenu()
        {
            EditorUtility.DisplayDialog("PlannerDNA", "Wrote " + Export(), "OK");
        }

        public static void ExportFromCommandLine()
        {
            Debug.Log("[PlannerDNA] Wrote contract fixtures: " + Export());
        }

        public static string Export()
        {
            string path = OutputPath;
            AtomicFile.WriteAllText(path, ContractFixtures.BuildJson());
            return path;
        }
    }
}
