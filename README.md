# Impatient Commuters

A Cities: Skylines mod that makes waiting passengers more likely to leave an overcrowded stop and find another route, with probability growing over time based on their age and trip purpose.

## How it works

Vanilla CS1 has a time-only boredom system: every citizen waiting at a stop has a `m_waitCounter` that increments each simulation step. When it saturates to 255 the game flags them as `BoredOfWaiting` and they abandon the stop. There is no crowd awareness — a passenger at an empty stop and one at a packed stop behave identically.

This mod adds a third layer on top of the vanilla system:

> When a stop has **at least as many waiting passengers as the average vehicle capacity** of its serving line, each waiting citizen gains a per-step probability of leaving that **grows quadratically with their wait time**, modified by their **age group** and **trip purpose**.

The formula per simulation frame:

```
p = 0.08 × (waitCounter / 255)² × ageFactor × destinationFactor
```

| Factor | Value |
|---|---|
| Low wait (counter near 0) | ≈ 0% per frame |
| Half wait (counter = 128) | ≈ 2% per frame |
| Max wait (counter = 255) | ≈ 8% per frame |

### Age factors (default, configurable)

| Age group | Level | Multiplier |
|---|---|---|
| Children | Patient | ×0.7 |
| Teenagers | Normal | ×1.0 |
| Young adults | Normal | ×1.0 |
| Adults | Normal | ×1.0 |
| Seniors | Impatient | ×1.3 |

### Destination factors

When **Scale by trip purpose** is enabled:

| Destination | Multiplier | Reason |
|---|---|---|
| Work | ×0.4 | Strong obligation — unlikely to give up |
| School (students) | ×0.5 | Obligation — reluctant to give up |
| Home | ×0.7 | Want to get home, but flexible |
| Leisure / shopping | ×1.1 | Optional trip — easier to give up |
| Tourist | ×1.3 | No fixed schedule |

### Threshold

The capacity threshold is computed as the **average `GetPassengerCapacity` of all active vehicles currently assigned to the line** × the **Threshold Multiplier** setting (default 1.0×). If the line has no active vehicles yet, the threshold is effectively infinite (no effect until vehicles are deployed).

The threshold cache refreshes every ~2 seconds (64 simulation ticks) so that vehicle additions and removals are reflected without walking every vehicle linked list on every tick.

## Compatibility

- Works standalone or alongside [Stops and Stations](https://steamcommunity.com/sharedfiles/filedetails/?id=1776052533) by dymanoid. Both mods check the `BoredOfWaiting` flag before acting, so there is no double-firing.
- No dependency on [Improved Public Transport Essentials](https://github.com/roberto-naharro/ImprovedPublicTransport).
- Requires [Harmony (Mod Dependency)](https://steamcommunity.com/sharedfiles/filedetails/?id=2040656402).

## Settings

Open **Options → Impatient Commuters** in-game.

| Setting | Default | Description |
|---|---|---|
| Enable mod | On | Master on/off switch |
| Capacity threshold | ×1.0 | Multiplier on average vehicle capacity. Raise to make the effect fire only at very full stops; lower to make it fire sooner. |
| Scale by trip purpose | On | Reduces impatience for work/school trips; raises it for tourists. |
| Patience per age group | See above | Per-age dropdown: Very Patient (×0.5) → Very Impatient (×1.6) |
| Debug logging | Off | Logs each forced departure to the game log with stop ID, crowd count, and probability. |

> **Note:** Settings are in-memory only and reset to defaults on each game launch. Persistence will be added in a future release.

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
