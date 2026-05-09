# Self-hosted web fonts

These fonts ship with PinballWizard.Web and are loaded via `@font-face`
declarations in [`../app.css`](../app.css). They are self-hosted (rather
than loaded from `fonts.googleapis.com` / `fonts.gstatic.com`) so the
application does not leak visitor IP addresses to a third party on every
page load — see `CLAUDE.md` § "Showcase obligations" for the rationale.

## Provenance

The `.woff2` binaries here are taken verbatim from the
[`@fontsource`](https://fontsource.org) v5.2.5 npm packages on the
jsdelivr CDN. Fontsource re-packages the canonical Google Fonts /
upstream font files without modification. Each subdirectory contains
the latin-subset weights actually used by the application.

| Family | Weights | Source package | License |
| --- | --- | --- | --- |
| Inter | 400, 500, 600, 700 | `@fontsource/inter@5.2.5` | OFL 1.1 |
| Barlow Condensed | 500, 700 | `@fontsource/barlow-condensed@5.2.5` | OFL 1.1 |
| JetBrains Mono | 400, 500 | `@fontsource/jetbrains-mono@5.2.5` | OFL 1.1 |
| Roboto | 300, 400, 500, 700 | `@fontsource/roboto@5.2.5` | OFL 1.1 |

## License attribution

Each subdirectory contains the upstream `LICENSE.txt` for that font
family, unmodified, with the original copyright holder preserved:

- [`inter/LICENSE.txt`](inter/LICENSE.txt) — Copyright (c) 2016 The Inter Project Authors
- [`barlow-condensed/LICENSE.txt`](barlow-condensed/LICENSE.txt) — Copyright 2017 The Barlow Project Authors
- [`jetbrains-mono/LICENSE.txt`](jetbrains-mono/LICENSE.txt) — Copyright 2020 The JetBrains Mono Project Authors
- [`roboto/LICENSE.txt`](roboto/LICENSE.txt) — Copyright 2011 The Roboto Project Authors

All four families are licensed under the
[SIL Open Font License, Version 1.1](https://scripts.sil.org/OFL).
The OFL permits embedding the fonts in derivative works and requires
that the license text and copyright notice travel with the font files —
satisfied by these `LICENSE.txt` files shipping alongside the `.woff2`
binaries in the deployed `wwwroot/`.

## Adding a new weight or family

1. Pick the canonical fontsource package on
   [fontsource.org](https://fontsource.org).
2. Download the latin-subset `.woff2` from
   `https://cdn.jsdelivr.net/npm/<package>@<version>/files/<file>` —
   prefer the variable-axis-free static files (`<family>-latin-<weight>-normal.woff2`).
3. Place the file under `<family-slug>/`.
4. Add a matching `@font-face` block to [`../app.css`](../app.css).
5. If the family is new, also drop its upstream `LICENSE.txt` into
   `<family-slug>/LICENSE.txt` and add a row to the table above.
6. Verify in browser DevTools that no requests go to `fonts.googleapis.com`
   or `fonts.gstatic.com` after the change.
