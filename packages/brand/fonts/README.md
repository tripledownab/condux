# Brand webfonts

The two brand families, vendored as files rather than fetched from Google at build time.

## Why these are committed

`next/font/google` downloads the font files from Google **during the build**. That made publishing
condux.ai depend on fonts.googleapis.com being reachable from a GitHub runner, and on 2026-08-13 it
was not: the landing deploy failed with `Error while requesting resource` and Turbopack could not
resolve the generated font module. Nothing was wrong with the commit, and a plain re-run passed.

A third party being up is not a reasonable precondition for shipping our own marketing site, so the
files live here and both apps load them with `next/font/local`. Runtime behaviour is unchanged:
`next/font/google` self-hosts and preloads the files too, so visitors were never contacting Google
either way. This only removes the **build-time** dependency.

## What is here

| File | Family | Weight |
|---|---|---|
| `chakra-petch-500.woff2` | Chakra Petch | 500 |
| `chakra-petch-600.woff2` | Chakra Petch | 600 |
| `chakra-petch-700.woff2` | Chakra Petch | 700 |
| `fira-code-variable.woff2` | Fira Code | 400 to 500 (variable) |

**Fira Code is one file, not two.** Google serves it as a variable font, so the URLs for weight 400
and weight 500 are the same file: downloading "both" gives two byte-identical copies. It is declared
with a weight range in the loaders, which is what a variable font wants. Chakra Petch is the other
case, three genuinely distinct static instances.

All five are the **latin** subset only, matching the `subsets: ["latin"]` the previous
`next/font/google` calls requested. Adding a language means adding the matching subset file here.

## Licensing

Both families are licensed under the **SIL Open Font License 1.1**, whose terms require the license
to travel with the files. `OFL-chakra-petch.txt` and `OFL-fira-code.txt` are the upstream texts and
must stay beside the fonts.

OFL is not the repository's own license, and that is fine: it permits redistribution and embedding,
including in a commercial product, provided the fonts are not sold on their own and the copyright
notice and license are included. Both conditions hold here.

## Refreshing them

These do not need routine updates; a font is not a dependency that rots. If a family does need
refreshing, take the **latin** `@font-face` blocks from the Google Fonts CSS API for the exact
weights the loaders declare, download those `.woff2` files, and re-check whether a family is variable
before assuming one file per weight.
