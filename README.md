# Games

The source and published build of **The Game Library** — a small shelf of
browser games, live at **[games.anteneh.tech](https://games.anteneh.tech)**.

Pick a card, press Play, and the game opens in its own page. Nothing to
install and no accounts.

## Now playing

- **Cleo — Celestial Survivor** (`/bonk/`) — Cleo. A winged
  kitten against the void: 770 abilities and relics, 340 foes, 16 legendary
  uniques and Nuke / Insta-Kill power-ups.

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
covers/*.jpg          cover art, one per game
fonts/*.woff2         Fredoka 500/600/700, Space Mono 400/700
CNAME                 binds games.anteneh.tech
.nojekyll             stops Pages filtering the output
```

`bonk/index.html` is a single self-contained file — engine, sprites, audio and
save handling all inline. It has no dependency on the landing page and can be
opened on its own.

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
