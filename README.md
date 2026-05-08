# Impatient Commuters

A Cities: Skylines mod that makes waiting passengers more likely to leave an overcrowded stop and find another route. Probability is highest the moment they arrive at a crowded stop, then fades as sunk wait time builds, then rises again with accumulated frustration. All shaped by age, trip purpose, line frequency, and nearby alternatives.

## How it works

Vanilla CS1 has a time-only boredom system: every citizen waiting at a stop has a `m_waitCounter` that increments each simulation step. When it saturates to 255 the game flags them as `BoredOfWaiting` and they abandon the stop. There is no crowd awareness — a passenger at an empty stop and one at a packed stop behave identically.

This mod adds a complementary layer on top of the vanilla system:

> When a stop has **at least as many waiting passengers as the average vehicle capacity** of its serving line, each waiting citizen gains a per-step probability of leaving that combines two real-world behavioural effects, modified by their **age group**, **trip purpose**, **line frequency**, and **access to alternatives**.

The formula per simulation frame:

```text
t = waitCounter / 255

p = (balkComponent + frustrationComponent) × freqFactor × multiLineFactor

  balkComponent        = 0.12 × crowdRatio × e^(−8t) × ageBalkFactor × destBalkFactor
  frustrationComponent = 0.08 × t²          × ageFrustFactor × destFrustFactor
  crowdRatio           = clamp((passengers − threshold) / threshold, 0, 1)
  freqFactor           = clamp(√(2 / vehicleCount), 0.3, ∞)   [1.0 at 2 vehicles, ≈0.71 at 4]
  multiLineFactor      = 1.15 if another line stops within 150 m, else 1.0
```

**Balking** (backed by queuing-theory research — Haight 1959, empirically confirmed for transit by Liu et al. 2022): a citizen who *just arrived* at a heavily overcrowded stop has a high immediate chance of turning around. The probability decays exponentially as sunk waiting time accumulates — the longer they've already waited, the less likely they are to abandon.

**Frustration** builds quadratically with wait time and kicks in when balking has faded, representing the classical boredom curve.

| Scenario | Balking | Frustration | Total |
| --- | --- | --- | --- |
| Just arrived, stop at 2× capacity | ≈ 12% | ≈ 0% | ≈ 12% |
| Just arrived, stop barely over threshold | ≈ 0% | ≈ 0% | ≈ 0% |
| Half wait (t = 0.5), stop at 2× capacity | ≈ 0.2% | ≈ 2% | ≈ 2.2% |
| Max wait (t = 1.0) | ≈ 0% | ≈ 8% | ≈ 8% |

### Age factors

Age affects balking and frustration differently. Balk factors are fixed (research-backed); frustration factors are configurable per age group via the settings UI.

**Balk factors (fixed)** — driven by crowding sensitivity and knowledge of alternatives:

| Age group | Balk factor | Rationale |
| --- | --- | --- |
| Children | ×0.5 | Low agency; accompany an adult; low initiative to reroute |
| Teenagers | ×1.2 | Impulsive; most likely to check phone and reroute immediately |
| Young adults | ×1.0 | Knows alternatives, but more deliberate than teenagers |
| Adults | ×0.9 | Experienced commuter; controlled immediate response |
| Seniors | ×1.4 | Highest crowding sensitivity (physical discomfort); low load tolerance |

Sources: Lu et al. 2024 (crowding sensitivity by demographic); Fan et al. 2016 (age × perceived wait).

**Frustration factors (configurable, default):**

| Age group | Level | Multiplier |
| --- | --- | --- |
| Children | Patient | ×0.7 |
| Teenagers | Normal | ×1.0 |
| Young adults | Normal | ×1.0 |
| Adults | Normal | ×1.0 |
| Seniors | Impatient | ×1.3 |

### Destination factors

Both balk and frustration are scaled by trip purpose, but in opposite directions. Work commuters balk more (they plan alternatives in advance — Kim et al. 2009) but abandon less from frustration (strong obligation to arrive). Tourists are the reverse: they don't know the network so balk less, but have no fixed schedule so grow frustrated sooner.

| Destination | Balk factor | Frustration factor | Rationale |
| --- | --- | --- | --- |
| Work | ×1.2 | ×0.4 | Plans alternatives; must arrive |
| School | ×0.8 | ×0.5 | Some obligation; limited alternatives |
| Home | ×0.9 | ×0.7 | Knows the route; flexible |
| Leisure / shopping | ×1.0 | ×1.1 | Optional trip |
| Tourist | ×0.7 | ×1.3 | Doesn't know alternatives; no schedule |

Destination factors are optional and gated by the **Scale by trip purpose** setting (default: on).

### Frequency factor

Headway depends on both vehicle count **and** line length. A single bus covering 3 stops completes its circuit quickly; the same bus on a 100-stop line barely comes back. The factor uses stop count as a proxy for circuit length:

```text
freqFactor = clamp(√((stopCount / vehicleCount) / 5), 0.30, 3.0)
```

Baseline: 5 stops per vehicle (e.g. 10 stops / 2 vehicles) → ×1.0 neutral.

| Example | stops / vehicles | Frequency factor |
| --- | --- | --- |
| Metro: 6 stops / 4 trains | 1.5 | ×0.55 |
| Short bus: 4 stops / 1 bus | 4.0 | ×0.89 |
| Typical bus: 10 stops / 2 buses | 5.0 | ×1.00 (baseline) |
| Long bus: 20 stops / 2 buses | 10.0 | ×1.41 |
| Infrequent: 20 stops / 1 bus | 20.0 | ×2.00 |
| Extreme: 100 stops / 1 bus | 100.0 | ×3.00 (cap) |

### Alternative lines

If another transport line has a stop within 150 m, each citizen at the overcrowded stop receives a **×1.15** multiplier on their total probability. Knowing a viable alternative is nearby makes leaving more appealing. The nearby-stop check is performed once per active stop per ~2-second cache cycle.

### Threshold

The capacity threshold is computed as the **average `GetPassengerCapacity` of all active vehicles currently assigned to the line** × the **Threshold Multiplier** setting (default 1.0×). If the line has no active vehicles yet, the threshold is effectively infinite (no effect until vehicles are deployed).

The threshold cache refreshes every ~2 seconds (64 simulation ticks) so that vehicle additions and removals are reflected without walking every vehicle linked list on every tick.

## Compatibility

- Works standalone or alongside [Stops and Stations](https://steamcommunity.com/sharedfiles/filedetails/?id=1776052533) by dymanoid. Both mods check the `BoredOfWaiting` flag before acting, so there is no double-firing.
- Pairs well with [Better Train Boarding](https://steamcommunity.com/sharedfiles/filedetails/?id=2773460744). BTB mod ensures passengers board the closest carriage and trains don't get jammed; this mod handles the platform side by making passengers at overcrowded stops decide to reroute. Together they produce a much more realistic rail flow.
- Requires [Harmony (Mod Dependency)](https://steamcommunity.com/sharedfiles/filedetails/?id=2040656402).

## Settings

Open **Options → Impatient Commuters** in-game.

| Setting | Default | Description |
| --- | --- | --- |
| Enable mod | On | Master on/off switch |
| Capacity threshold | ×1.0 | Multiplier on average vehicle capacity. Raise to make the effect fire only at very full stops; lower to make it fire sooner. |
| Scale by trip purpose | On | Applies destination balk and frustration factors. |
| Patience per age group | See above | Per-age dropdown: Very Patient (×0.5) → Very Impatient (×1.6) — controls frustration only. |
| Debug logging | Off | Logs each forced departure to the game log with stop ID, crowd count, and probability. |

## Building

```bash
# Prerequisites: Mono, xbuild
# References point to ../ImprovedPublicTransportEssentials/GameReferences/
# Copy .env from ImprovedPublicTransportEssentials or create your own.

xbuild ImpatientCommuters.csproj /p:Configuration=Release /nologo /verbosity:quiet

# Build + deploy to mounted game folder:
./deploy.sh           # Debug
./deploy.sh --release # Release
```

## Credits

- **dymanoid** — [Stops and Stations](https://steamcommunity.com/sharedfiles/filedetails/?id=1776052533): the two-phase `ThreadingExtensionBase` pattern and citizen iteration approach used here is inspired by their implementation.
- **BloodyPenguin** — [Improved Public Transport 2](https://steamcommunity.com/sharedfiles/filedetails/?id=424106600): original mod architecture that informed this project's structure.
