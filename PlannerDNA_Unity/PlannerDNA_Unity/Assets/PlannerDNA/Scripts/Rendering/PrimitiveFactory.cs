using UnityEngine;

namespace PlannerDNA.Rendering
{
    /// <summary>Creates collider-free primitive meshes that share Unity's built-in meshes.</summary>
    public static class PrimitiveFactory
    {
        private static Mesh _cube, _sphere, _cylinder;

        private static Mesh BuiltIn(PrimitiveType type, ref Mesh cache)
        {
            if (cache != null) return cache;
            GameObject temp = GameObject.CreatePrimitive(type);
            cache = temp.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Object.Destroy(temp);
            else Object.DestroyImmediate(temp);
            return cache;
        }

        public static GameObject Box(Transform parent, string name, Vector3 localCentre, Vector3 size, Material material)
        {
            return Make(parent, name, BuiltIn(PrimitiveType.Cube, ref _cube), localCentre, size, material);
        }

        public static GameObject Sphere(Transform parent, string name, Vector3 localCentre, float diameter, Material material)
        {
            return Make(parent, name, BuiltIn(PrimitiveType.Sphere, ref _sphere), localCentre, Vector3.one * diameter, material);
        }

        /// <summary>Unity's cylinder is 2 units tall; height here is the real height.</summary>
        public static GameObject Cylinder(Transform parent, string name, Vector3 localCentre, float diameter, float height, Material material)
        {
            return Make(parent, name, BuiltIn(PrimitiveType.Cylinder, ref _cylinder), localCentre,
                new Vector3(diameter, height * 0.5f, diameter), material);
        }

        /// <summary>Four thin bars outlining a square (selection, hover and change markers).</summary>
        public static GameObject Frame(Transform parent, string name, float size, float barWidth, float barHeight, Material material)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            float half = size * 0.5f - barWidth * 0.5f;
            Box(root.transform, "N", new Vector3(0, 0, half), new Vector3(size, barHeight, barWidth), material);
            Box(root.transform, "S", new Vector3(0, 0, -half), new Vector3(size, barHeight, barWidth), material);
            Box(root.transform, "E", new Vector3(half, 0, 0), new Vector3(barWidth, barHeight, size - 2 * barWidth), material);
            Box(root.transform, "W", new Vector3(-half, 0, 0), new Vector3(barWidth, barHeight, size - 2 * barWidth), material);
            return root;
        }

        private static GameObject Make(Transform parent, string name, Mesh mesh, Vector3 localCentre, Vector3 scale, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCentre;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return go;
        }
    }
}
