using System.Collections.Generic;

namespace PlannerDNA.Metrics
{
    public sealed class Explanation
    {
        public string Title;
        public string Body;

        public Explanation(string title, string body)
        {
            Title = title;
            Body = body;
        }
    }

    /// <summary>Plain-language definitions shown in the UI. Kept beside the code they describe.</summary>
    public static class MetricDefinitions
    {
        public static readonly IReadOnlyList<Explanation> Metrics = new[]
        {
            new Explanation("Park access (synthetic network proxy)",
                "Share of housing capacity whose road-network walk to the nearest park is at most 400 m.\n" +
                "access % = 100 x accessible housing units / total housing units.\n" +
                "Walk = 40 m parcel-to-road link + 40 m per road cell travelled + 40 m road-to-park link. " +
                "Only roads count: no straight-line shortcuts. A residential parcel with no road path to a park is inaccessible. " +
                "Weighted by housing capacity units, not real residents. Shows N/A if there is no housing."),
            new Explanation("Housing capacity units (proxy)",
                "Sum over residential parcels of floors x units per floor (4 in this scenario). " +
                "A synthetic capacity proxy, not population, households or demand."),
            new Explanation("Park share",
                "park % = 100 x park cells / non-road cells. The denominator excludes only road cells " +
                "(it includes vacant, civic, residential, commercial and park cells). All cells have equal area."),
            new Explanation("Intervention cost (synthetic credits)",
                "Computed from the difference between each parcel's baseline and current state, never from click history. " +
                "A parcel restored exactly to baseline costs 0. Credits are fictional, not money.\n" +
                "Rules (cost-rules-1.0.0): vacant->park 20; new building on vacant/park 25 + 5 per floor; " +
                "building->park 35; demolish building 15; park->vacant 10; residential<->commercial conversion 20 " +
                "(+5 per added / +3 per removed floor); same use: +5 per added floor, +3 per removed floor."),
            new Explanation("Lost original housing (displacement proxy)",
                "Housing units lost on parcels that were residential in the baseline (baseline units minus current units, " +
                "never negative). Reported separately from net housing change. It does not model real residents being displaced."),
            new Explanation("Hard constraints",
                "Budget: credits spent <= 100. Housing: capacity >= 90% of baseline (rounded up to whole units). " +
                "Height: no building above 8 floors. You may explore designs that break constraints, but such designs " +
                "are marked infeasible and are not eligible as feasible training comparisons."),
        };

        public static readonly IReadOnlyList<Explanation> Glossary = new[]
        {
            new Explanation("Synthetic credits", "A fictional budget unit used to make trade-offs explicit. Not currency or a construction estimate."),
            new Explanation("Housing capacity proxy", "Floors x units per floor on residential parcels. Not people or households."),
            new Explanation("Network park access", "Walking distance measured only along the road grid, including the links into and out of parcels."),
            new Explanation("Baseline", "The original generated neighbourhood for this seed. Every cost and change is measured against it."),
            new Explanation("Alternative A / B", "Complete saved designs made from the same baseline, compared side by side."),
            new Explanation("Comparison", "Your explicit choice between A and B (or tie / skip). Only explicit choices are recorded as labels; edits and undo clicks are never treated as preferences."),
            new Explanation("Training", "Only when you press \"Train my model\" in the Learning Lab, the local backend fits a symmetric logistic model to your feasible A/B choices (ties and skips excluded). Nothing is trained automatically."),
            new Explanation("Weights", "One number per feature: how strongly that feature was associated with your choices. Standardised weights are comparable across features; the interval shows bootstrap uncertainty across scenarios."),
            new Explanation("Diagnostic accuracy", "Leave-one-scenario-out: each scenario's choices are predicted by a model fitted without that scenario, compared against simple baselines. It is a diagnostic inside your training data, not a blinded test."),
            new Explanation("Held-out test (later phase)", "Scenarios never used for fitting, used to check whether a locked model predicts your later choices. Not implemented in this build."),
            new Explanation("Prediction uncertainty", "A prediction is a probability from few labels; it is a statistical association, not your reasoning. On the Compare screen it is revealed only after you confirm your own choice."),
            new Explanation("Suggestions", "Seeded rule-based candidate designs, filtered for feasibility, ordered by your model's score. Never optimal plans; always for a synthetic scenario."),
            new Explanation("Seed", "An integer that fully determines the generated neighbourhood. The same seed and generator version always produce the same city."),
        };
    }
}
