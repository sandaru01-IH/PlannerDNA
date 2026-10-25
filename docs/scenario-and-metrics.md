# Scenario and metrics: The Neighbourhood Green-Space Dilemma

Versions: generator `green-gen-1.0.0`, metrics `metrics-1.0.0`, cost rules `cost-rules-1.0.0`,
features `features-v1`, schema 1. Every saved file records these versions.

All numbers are **synthetic proxies**: housing units are capacity, not people; credits are
fictional, not money; access is a road-grid distance, not a real walking route.

## Mission

> Improve the share of housing capacity within a 400 m road-network walk of a park, while keeping
> housing capacity at or above 90% of the baseline, spending no more than 100 synthetic credits and
> never exceeding 8 floors.

The mission text is generated from the scenario parameters (`ScenarioDefinition.GreenSpaceDilemma`),
so the text and the rules cannot drift apart.

| Parameter | Value |
|---|---|
| Grid | 20 × 20 parcels, 40 m each (800 m × 800 m) |
| Budget | 100 credits |
| Minimum housing | ceil(0.90 × baseline units), computed with integer arithmetic |
| Maximum floors | 8 |
| Access threshold | 400 m by road |
| Units per floor | 4 |
| Floors for a new building | 2 |

## City generation (deterministic)

The seed fully determines the city. A SplitMix64 generator is used instead of `System.Random`
because its output is specified and identical across runtimes (a unit test pins reference values).

- Rows 0, 3, 6, …, 18 are horizontal roads, so every parcel touches a road.
- Three vertical roads are placed, one column from each band [2,5), [8,12) and [15,18), chosen by
  the seed.
- 10% of parcels are vacant. Parcels beside the middle vertical road are 50% commercial, others 8%.
  The rest are residential with 2–6 floors, 35% of them zoned residential-only.
- 1–2 small (two-parcel) parks sit in one quadrant, so baseline access is incomplete.
- 1–2 civic parcels are fixed and protected.

A candidate city is accepted only if all of these hold:

- the road network is connected and every parcel has road access;
- there is at least one park, at least 3 vacant parcels and some housing;
- baseline access is below 75%;
- at least one vacant→park edit improves access and stays feasible.

Otherwise the generator retries with the next attempt index, up to 32 attempts, then reports an
error rather than producing a broken city. Seed 42 is the demo seed.

## Metrics

| Metric | Definition |
|---|---|
| **Housing capacity** | Σ over residential parcels of floors × 4. Commercial, civic, park, vacant = 0. |
| **Park share** | 100 × park parcels / non-road parcels. The denominator excludes only roads. |
| **Park access** | 100 × housing units on parcels with walk ≤ 400 m / total housing units. Shows **N/A** if housing is 0. |
| **Intervention cost** | Σ over parcels of cost(baseline parcel → current parcel), using the table below. |
| **Changed parcels** | Parcels whose use or floors differ from the baseline. |
| **Lost original housing** | Σ over baseline-residential parcels of max(0, baseline units − current units). A displacement proxy, reported separately from net change. |
| **Net housing change** | Current units − baseline units. |

### Walk distance (network proxy)

```
walk = 40 m (parcel → adjacent road) + 40 m × road cells travelled + 40 m (road → adjacent park)
```

A multi-source breadth-first search starts from every road cell next to a park. A residential
parcel uses the best adjacent road cell. Only roads are walkable: there are no diagonal moves,
no shortcuts through parcels, and no straight-line distance. A parcel with no road path to any park
is inaccessible. For example, a house and a park on the same road cell are 80 m apart. With 40 m
cells the cut-off works out as: 400 m is accessible, while the next possible distance, 440 m, is not.

## Cost table (cost-rules-1.0.0)

Cost compares each parcel's **baseline** state with its **current** state. It never depends on
click history. A parcel restored exactly to baseline costs 0, and no transition is negative.

| Baseline → current | Credits |
|---|---|
| unchanged | 0 |
| vacant → park | 20 |
| vacant or park → new residential/commercial | 25 + 5 × floors (2 floors → 35) |
| residential/commercial → park | 35 |
| residential/commercial → vacant (demolish) | 15 |
| park → vacant | 10 |
| residential ↔ commercial | 20, + 5 per added floor or + 3 per removed floor |
| same building use, floors changed | + 5 per added floor, + 3 per removed floor |
| anything involving road or civic | undefined, so the edit is refused |

## Edit rules (validator)

| Rule | Refusal message (abridged) |
|---|---|
| Roads are fixed | "…is a road. Roads are fixed…" |
| Civic parcels are protected | "…protected…" |
| No park on a park | "already a park" |
| Residential-only zoning blocks commercial | "zoned residential-only" |
| Only buildings have floors | "only buildings have floors" |
| Height limit | "already at the maximum of 8 floors" |
| Minimum height is 1 floor | "Use Demolish…" |
| Undefined cost transition | explains the missing rule |

Converting a building keeps its floor count. A new building on vacant or park land gets 2 floors.
Refused attempts are logged separately and never become timeline edits.

## Hard constraints

| Id | Passes when |
|---|---|
| `budget` | spent credits ≤ 100 |
| `housing_floor` | housing ≥ ceil(0.9 × baseline) |
| `max_floors` | no building above 8 floors |
| `cost_rules_defined` | every changed parcel has a defined cost rule |

You may explore and save infeasible designs. They are clearly marked, and a comparison involving one
is stored with `training_eligible = false`.

## Feature vector v1

Saved with every alternative. Since Phase 3 it is also the input of the optional personal
preference model, which uses the difference between two designs' vectors (see
[ml-protocol.md](ml-protocol.md)). The signs only set the direction of each input; they are not
preference weights. No weights exist until you train a model on your own choices.

| Field | Formula |
|---|---|
| `delta_access_fraction` | (access% − baseline access%) / 100 |
| `delta_housing_fraction` | (units − baseline units) / baseline units |
| `delta_park_share_fraction` | (park% − baseline park%) / 100 |
| `negative_budget_fraction` | −(spent credits / 100) |
| `negative_lost_housing_fraction` | −(lost original units / baseline units) |
