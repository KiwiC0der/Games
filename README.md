# Games

The published build of **The Arcade** — a small library of browser games,
live at **[games.anteneh.tech](https://games.anteneh.tech)**.

Pick a card, press Play, and the game opens fullscreen in the page. Quit
returns you to the shelf. Nothing to install and no accounts.

## Now playing

- **Cleo — Celestial Survivor** (`/bonk`) — survivor roguelite. A winged kitten
  against the void: 720 relics, 16 legendary uniques, 336 monster variants and
  Nuke / Insta-Kill power-ups.

Everything else on the shelf is a placeholder while it gets built.

## About this repository

These are static build artifacts, not source. The site is a Next.js app
(App Router, TypeScript, Tailwind, shadcn component layout) exported to plain
files, and each game is a single self-contained HTML page under its own slug.

    index.html        the library
    _next/            compiled CSS and JS
    covers/           cover art, one per game
    bonk/             a game, self-contained
    CNAME             binds games.anteneh.tech
    .nojekyll         stops Pages filtering the _next directory

## License

(c) Anteneh Demissie. All rights reserved.
