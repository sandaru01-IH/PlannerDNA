using UnityEngine;

namespace PlannerDNA.Rendering
{
    /// <summary>Colours of the urban-design-lab look: neutral light base, one terracotta accent.</summary>
    public static class PaletteColors
    {
        public static readonly Color Ground = Hex(0xE7E4DC);
        public static readonly Color Road = Hex(0x8F949B);
        public static readonly Color Residential = Hex(0xEADFCF);
        public static readonly Color ResidentialBand = Hex(0xB8A68C);
        public static readonly Color Commercial = Hex(0xA9C1D6);
        public static readonly Color CommercialBand = Hex(0x5F7F9C);
        public static readonly Color Park = Hex(0x9CC98A);
        public static readonly Color TreeCrown = Hex(0x4F8F4C);
        public static readonly Color TreeTrunk = Hex(0x7A5B40);
        public static readonly Color Vacant = Hex(0xD9CCB1);
        public static readonly Color Civic = Hex(0xC9B9DA);
        public static readonly Color CivicBand = Hex(0x8C76A8);
        public static readonly Color Lot = Hex(0xD3CEC4);
        public static readonly Color Accent = Hex(0xD9622B);
        public static readonly Color Hover = Hex(0xFFFFFF);
        public static readonly Color Changed = Hex(0xE89B3C);
        public static readonly Color AccessOk = Hex(0x5FB3A8);
        public static readonly Color AccessNo = Hex(0xD98C8C);

        public static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }
    }

    /// <summary>
    /// Material set used by the grid renderer. The editor scene builder saves one as an asset so
    /// the shaders are included in builds; at runtime a fallback set is created if none is assigned.
    /// </summary>
    [CreateAssetMenu(menuName = "PlannerDNA/Material Palette", fileName = "PlannerDNA_MaterialPalette")]
    public sealed class MaterialPalette : ScriptableObject
    {
        public Material ground;
        public Material road;
        public Material lot;
        public Material residential;
        public Material residentialBand;
        public Material commercial;
        public Material commercialBand;
        public Material park;
        public Material treeCrown;
        public Material treeTrunk;
        public Material vacant;
        public Material civic;
        public Material civicBand;
        public Material selection;
        public Material hover;
        public Material changed;
        public Material accessOk;
        public Material accessNo;

        public bool IsComplete =>
            ground && road && lot && residential && residentialBand && commercial && commercialBand && park && treeCrown
            && treeTrunk && vacant && civic && civicBand && selection && hover && changed && accessOk && accessNo;

        public static Shader FindLitShader()
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (s == null) s = Shader.Find("Standard");
            return s;
        }

        public static Material MakeMaterial(Shader shader, string name, Color color, float smoothness = 0.15f)
        {
            var m = new Material(shader) { name = name };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            return m;
        }

        /// <summary>Fills every slot with a newly created material (used by the editor builder and the runtime fallback).</summary>
        public void PopulateDefaults(Shader shader)
        {
            ground = MakeMaterial(shader, "PDNA_Ground", PaletteColors.Ground);
            road = MakeMaterial(shader, "PDNA_Road", PaletteColors.Road);
            lot = MakeMaterial(shader, "PDNA_Lot", PaletteColors.Lot);
            residential = MakeMaterial(shader, "PDNA_Residential", PaletteColors.Residential);
            residentialBand = MakeMaterial(shader, "PDNA_ResidentialBand", PaletteColors.ResidentialBand);
            commercial = MakeMaterial(shader, "PDNA_Commercial", PaletteColors.Commercial, 0.45f);
            commercialBand = MakeMaterial(shader, "PDNA_CommercialBand", PaletteColors.CommercialBand);
            park = MakeMaterial(shader, "PDNA_Park", PaletteColors.Park);
            treeCrown = MakeMaterial(shader, "PDNA_TreeCrown", PaletteColors.TreeCrown);
            treeTrunk = MakeMaterial(shader, "PDNA_TreeTrunk", PaletteColors.TreeTrunk);
            vacant = MakeMaterial(shader, "PDNA_Vacant", PaletteColors.Vacant);
            civic = MakeMaterial(shader, "PDNA_Civic", PaletteColors.Civic);
            civicBand = MakeMaterial(shader, "PDNA_CivicBand", PaletteColors.CivicBand);
            selection = MakeMaterial(shader, "PDNA_Selection", PaletteColors.Accent);
            hover = MakeMaterial(shader, "PDNA_Hover", PaletteColors.Hover);
            changed = MakeMaterial(shader, "PDNA_Changed", PaletteColors.Changed);
            accessOk = MakeMaterial(shader, "PDNA_AccessOk", PaletteColors.AccessOk);
            accessNo = MakeMaterial(shader, "PDNA_AccessNo", PaletteColors.AccessNo);
        }

        public static MaterialPalette CreateRuntimeDefault()
        {
            var palette = CreateInstance<MaterialPalette>();
            palette.name = "PlannerDNA_RuntimePalette";
            palette.PopulateDefaults(FindLitShader());
            return palette;
        }
    }
}
