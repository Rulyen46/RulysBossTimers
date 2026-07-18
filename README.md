# Ruly's Boss Timers (RBT)

**WeakAuras-style boss encounter timers for Erenshor.**
A Lunaris plugin that puts a clean on-screen countdown on the mechanics that
matter — a moment's warning instead of a surprise.

By **Ruly** · Requires **Lunaris** · Commands under `/rbt`

---

## Timer bars

Three kinds:

- **Countdown** — fires on a boss's telegraph and empties as the effect lands.
- **Persistent** — stays up while something's active (an add, a phase) and clears
  itself when it's over.
- **Health pre-warning** — fills as a boss nears an HP threshold, warning you
  *before* a mechanic the game wouldn't otherwise telegraph until it hits. Fires
  once per approach.

Bars are opaque and readable over any background, color-themed, and scale to your
resolution. Auras are zone-scoped, so one boss's telegraph never lights up
another zone's timer.

## It times, it doesn't spoil

RBT adds timing and attention, not knowledge the game withholds. Choose how much
each alert says with `/rbt detail`:

- **Game-faithful** *(default)* — shows the game's own broadcast line, word for
  word; health pre-warnings show only the boss's name.
- **Minimal** — bars and numbers only.
- **Descriptive** — plain coaching labels ("STOP DPS", "adds incoming"). Fully
  opt-in.

## Included encounters

Tuned timers for encounters across the game's zones — among them Tojokom, Arbor,
Fernallan High Priest, Animation of Grace, Soluna, Fernalla, Gruhglor, Granitus,
Druo the Reborn, Kio the Darkbringer, Monarch of the Flame, the Warders of Flame
and Ice, Xjeris, and the Acolytes of Azynthi.

## Make your own

Timers live in a plain `auras.json` next to the plugin — edit or share without
touching code. Two triggers:

- **Log match** — a pattern tested against combat-log lines, with a countdown
  duration and/or a "clear when this matches" pattern.
- **Health watch** — a boss name, a threshold percent, and a warning band, with
  an optional charge limit for mechanics that fire a set number of times.

Every aura also takes an optional zone and an on/off flag. Edit the file and run
`/rbt reload` — no restart. Packs are just JSON, easy to trade with other players.

## Options (Lunaris menu)

- **Alert detail** — Minimal / Game-faithful / Descriptive
- **Overlay scale** — size the bars and text to your resolution
- **Lock overlay** — stop the window from being dragged once placed
- **Respect aura zones** — on by default; off runs every aura everywhere
- **Colors** — a native picker for the three bar themes (Warning / Danger / Info)

## Commands

| Command | Does |
|---------|------|
| `/rbt toggle` | Show or hide the overlay |
| `/rbt clear` | Remove all active timer bars |
| `/rbt reload` | Reload `auras.json` after editing it |
| `/rbt test` | Show a 5-second test bar |
| `/rbt detail` | Cycle alert detail |
| `/rbt zone` | Print the current zone's name |
| `/rbt help` | List all commands |

## Installation

1. Install **Lunaris**.
2. Drop the plugin `.dll` into your Erenshor `plugins` folder.
3. Launch the game — RBT creates its `auras.json` on first run and the included
   timers work immediately.

Everything runs locally off the combat log and enemy health; nothing is
transmitted anywhere.

## Credits

Made by **Ruly** for **Erenshor** and the **Lunaris** plugin manager.
