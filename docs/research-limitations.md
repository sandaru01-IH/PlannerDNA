# Research limitations

PlannerDNA is a single-user, local, synthetic research sandbox. This page lists what its numbers
**cannot** support. Keep it next to any result you report.

## What the model is, and is not

- **It is** a five-weight logistic summary of which measured outcome differences went with which
  of your explicit A/B choices.
- **It is not** a model of your reasoning, values, expertise or "planning DNA". Two people with
  entirely different reasons can produce the same weights.
- The weights are **associations**, not causes. If park access and spending always moved together
  in the designs you compared, the model cannot separate them. Its weights may then attribute one
  to the other. The rank check refuses only the extreme case, where everything varies along one
  direction.
- The model sees only five synthetic proxies. Everything else you noticed is invisible to it:
  layout, shape, distance to a particular street, aesthetics, fairness between blocks. Choices
  driven by those features look like noise.

## Data limitations

- **Small samples.** The minimum (10 comparisons, 2 scenarios) and the suggested level (30
  comparisons, 5 scenarios) are heuristics. With a few dozen comparisons:
  - weights can change sign when you add a handful of choices;
  - bootstrap intervals over 3–5 scenarios are rough;
  - perfectly separable data make weight sizes depend on the regularisation strength.
- **Selection effects.** You choose which designs to build and compare, so the comparisons are not
  a random sample of the design space. The model describes the pairs you made, not your
  preferences over all possible designs.
- **Order and fatigue.** Choices made late in a session, or after many similar pairs, may differ
  from early ones. No phase models time, learning or fatigue.
- **Single person, single room.** All data come from one person on one computer. Nothing
  generalises to other people, groups or professional planners.

## Synthetic world

- Cities are generated from a seed. Metrics are deliberately simple proxies: housing capacity
  units, park share, road-network walking access within 400 m, and fictional credits. They are
  not traffic, prices, population, health, ecology, climate or equity models.
- Results say nothing about any real neighbourhood. Suggestions are **never optimal plans** and
  never professional advice. Every suggestion is labelled "synthetic scenario" and carries the
  model version that ranked it.

## Evaluation limitations

- **Leave-one-scenario-out cross-validation** (Learning Lab) is a diagnostic inside the training
  data, not a test: the model is not locked, the metric is not pre-registered and the comparisons
  were not collected blind to the model.
- The **blinded test** (Phase 4) fixes those three points: the model is frozen by checksum, the
  areas, sizes and metrics are pre-registered, and each prediction is sealed before the choice and
  opened only at the end. Its limits remain:
  - **Small n.** A short test scores at most 12 choices, a full one at most 20. Scenario-clustered
    95 % intervals are wide (often 30–40 percentage points), and with fewer than 3 scored areas no
    interval is given.
  - **One person, synthetic areas.** The result describes how well a summary of your past choices
    predicts your next choices in this sandbox. It is not evidence about planning quality, other
    people or real places.
  - **Generated pairs.** Test pairs come from the rule-based generator (trade-off pairs first). Your
    practice pairs were designs you built yourself, so the two sets of pairs differ in kind; a model
    may do worse (or better) on generated pairs for that reason alone.
  - **Repeats bound accuracy.** If you repeat only 3 of 4 choices, no model can be reliably above
    about 75 %. Few repeats make this bound itself uncertain.
  - **Knowing you are tested.** Being in a test can change how carefully you choose.
- A model can lose to a one-line baseline such as "always the design with more park access". This
  is reported, not hidden. Beating chance is not evidence of a meaningful personal model.
- Predictions shown on the Compare screen are revealed only after you choose, which avoids anchoring
  that choice. If the comparison is in the training data, the screen says so: such a prediction
  is not an independent check.
- Training accuracy is optimistic by construction and is labelled as such.

## Interaction limitations

- Seeing suggestions and predictions can change later choices. Labels recorded after you have used
  the Learning Lab are no longer independent of the model. The blinded test keeps every prediction
  hidden until you finish so that it cannot steer the test itself, but it cannot undo what you saw
  before the test.
- Applying a suggestion is logged with input source `assistant_generated`. Analyses should
  separate designs you built from designs the system proposed.

## Engineering limitations

- Results depend on exact versions: generator, metrics, cost rules, features, ML code,
  scikit-learn/scipy/numpy. Each model file records them. Results from different versions should
  not be pooled.
- Identical data give an identical model (same fingerprint, so no retraining). Last-digit
  floating-point differences across machines or library versions are possible.
- The backend accepts only loopback connections and stores everything locally. There is no
  multi-user support, no authentication, and no remote backup.

## What may be claimed

From the Learning Lab:

> "On N explicit comparisons across S synthetic scenarios, my choices were associated with
> [direction] in [outcome] (standardised weight w, 90 % scenario-bootstrap interval [a, b]). In
> leave-one-scenario-out cross-validation within these data, the model's accuracy was p versus q
> for the best simple baseline."

From a closed blinded test (quote the report's counts, not rounded impressions):

> "A model frozen before the test (version v, file SHA-256 h) predicted k of n pre-registered,
> held-out choices in S unseen synthetic scenarios (accuracy p, 95 % scenario-clustered bootstrap
> interval [a, b]; Brier B; log loss L). The best simple rule, [rule], scored q. My own choices
> agreed on r of m repeated pairs."

Nothing stronger. In particular, not "the model learned my planning values" or "the model is
accurate".
