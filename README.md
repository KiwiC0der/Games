# Games

The source and published build of **The Game Library** — a small shelf of
browser games, live at **[games.anteneh.tech](https://games.anteneh.tech)**.

Pick a card, press Play, and the game opens in its own page. Nothing to
install and no accounts.

## Now playing

- **Cleo — Celestial Survivor** (`/bonk/`) — Cleo. A winged
  kitten against the void: 770 abilities and relics, 340 foes, 16 legendary
  uniques and Nuke / Insta-Kill power-ups.
- **Terraheim** (`/terraheim/`) — building platformer. You move with
  Terraria's player physics and build with Valheim's structural integrity, both
  ported from the games' decompiled code. Five bridge-and-tower levels plus a
  freebuild sandbox.

The other eight cards on the shelf are placeholders while they get built.

## The landing page

A single static page. No build step, no framework, no `node_modules` — what
is in the repo is exactly what ships.

- **Scroll-stacking deck.** Every game is a `position: sticky` scene pinned to
  the top of the viewport. Because the scenes share a parent, each one paints
  over the last, and that layering *is* the stacking effect. JavaScript only
  nudges the outgoing card back a few percent so a sliver peeks out and it
  reads as a deck rather than a hard cut.
- **Magnetize Play button.** A vanilla port of the framer-motion original:
  particles rest scattered around the control and spring to its centre on
  hover, focus or touch.
- **Neo-brutalist shelf.** White paper with a faint engineering grid, 3px
  black rules, hard offset shadows, Fredoka display type over Space Mono.
  Each game carries its own pastel accent.

### Layout notes

The card is two columns on desktop and stacks to a portrait card on phones.
That portrait breakpoint is gated on viewport *height* as well as width — a
landscape phone is short and wide, and the two-column card is the layout that
actually fits there.

The root uses `overflow-x: clip` rather than `hidden`. `hidden` would make the
root a scroll container, which silently re-anchors every sticky scene to it and
kills the entire stacking effect.

### Accessibility

Semantic landmarks and one `h1`, a skip link, visible focus rings, real
`aria-current` state on the dot rail, alt text on every cover, and a full
`prefers-reduced-motion` path that drops the particle field and the deck
transform. The page reads and navigates fine with JavaScript blocked.

## Layout of this repository

```
index.html            the landing page
assets/css/site.css   all styling
assets/js/site.js     deck transforms, dot nav, magnetize particles
bonk/index.html       Cleo — Celestial Survivor (self-contained, ~530 KB)
terraheim/index.html  Terraheim (self-contained apart from the shared /fonts)
covers/*.jpg          cover art, one per game
fonts/*.woff2         Fredoka 500/600/700, Space Mono 400/700
CNAME                 binds games.anteneh.tech
.nojekyll             stops Pages filtering the output
```

`bonk/index.html` is a single self-contained file — engine, sprites, audio and
save handling all inline. It has no dependency on the landing page and can be
opened on its own.

## How Terraheim was made

Terraheim is a *mechanics port*: only rules and numbers were carried over. No
original code, art or audio ships in this repo.

- Terraria 1.4.5.6 (`Terraria.exe`, .NET/XNA) and Valheim
  (`assembly_valheim.dll`, Unity Mono) were decompiled locally with ILSpy.
- The vanilla path of `Player.Update` (movement, jump, gravity, step-up/down,
  tile collision, fall damage) and `WearNTear.UpdateSupport` plus
  `WearNTearUpdater` were re-expressed in JavaScript. Every float operation is
  rounded with `Math.fround`, matching both games' single-precision maths.
- A differential harness copied the relevant decompiled methods verbatim into
  a C# program with small engine stubs, and ran them side by side with the JS
  port over 280 generated scenarios. All 286,017 frames matched bit for bit:
  positions, velocities, jump state, fall damage, support values, collapses.
- Every level ships with a reference solution that the simulator proves stands
  within budget. Sequential beam-by-beam builds prove that each level's lesson
  is actually required.

The simulator sits between the `/*SIM-BEGIN*/` and `/*SIM-END*/` markers in
`terraheim/index.html`, and the level data between `/*LEVELS-BEGIN*/` and
`/*LEVELS-END*/`, so tests can load exactly the code that ships.
`?debug` exposes the game state on `window.__terraheim` for browser tests and
changes nothing else.

Not affiliated with Re-Logic or Iron Gate.

## Deploying

GitHub Pages serves the repository root on push to `main`.

## Adding a game

1. Drop the self-contained build at `/<slug>/index.html`.
2. Add an 800×1200 cover at `covers/<slug>.jpg`.
3. Copy a `<section class="scene">` block in `index.html`, set `--accent`, and
   swap the disabled `<span class="magnetize">` for an `<a class="magnetize">`.
4. Add a dot to the `.dotnav` list and a `<url>` entry to `sitemap.xml`.

## License

(c) Anteneh Demissie. All rights reserved.
