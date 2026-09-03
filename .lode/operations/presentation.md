# Presentation Deliverables

The competition submission material lives in `presentation/`. It is not part of the host and no
code reads it. This file records what each artifact claims, where each number comes from, and
the framing decision behind the result slides.

```mermaid
flowchart LR
    L[.lode evidence] --> D[index.html]
    L --> V[video-script.md]
    S[third-day-console.png] --> D
    S --> V
    D --> P[Published artifact]
```

## Files

| File | Purpose |
|---|---|
| `index.html` | Twelve-slide deck. One self-contained file, no build step and no local assets. |
| `video-script.md` | Two voiceover scripts with clean transcripts. Script A is the 60-second cut, half of it one real sitting ending in NO_TRADE. Script B is a 3:40 deck walkthrough, one block per slide. A merge section splices them into about 4:09. |
| `submission.md` | LabLab submission text: a 426-word long description, the short description, and the links. |
| `export-deck.sh` | Renders `export/slide-01..12.png` (3840x2160) and `export/deck.pdf` (12 pages, 13.333in x 7.5in) with headless Chrome. Re-run after editing the deck. |
| `room-sitting.png` | One full sitting in the operator console, 2026-09-03 dry run, cropped. Embedded in slide 6 and copied to `docs/console.png` for the README. |
| `third-day-console.png` | Operator console at run complete, 2026-09-02. Embedded in the result slide. |
| `third-day.png` | The original uncropped capture. Kept as the source. |
| `Pavel Osadchuk, resume 2026.pdf` | Source for the author and ask slides. Not embedded. |

The public repository is `https://github.com/xakpc/Alpaca.NoIdea`, linked from the title and
closing slides. `presentation/` is gitignored, so the deck and the script are not in it.

The published copy is at `https://claude.ai/code/artifact/3981f3c5-75e0-4e79-8923-6a02f5720da5`.
It is generated from `index.html` by removing the standalone document wrapper, because the host
supplies its own. Change `index.html` first, then regenerate:

```bash
sed -E '/^<!doctype html>$/d; /^<html lang="en">$/d; /^<\/?head>$/d; /^<meta /d; /^<\/?body>$/d; /^<\/html>$/d' index.html > deck-artifact.html
```

## Deck contract

- Two readers: competition judges, and Alpaca as an employer. The author applied for Staff
  Software Engineer, New Markets (Remote), and the deck is the work sample.
- **The deck answers the published rubric; it does not argue with it.** The four criteria are
  P&L performance, technology implementation, creativity and originality, and presentation and
  execution. The Alpaca surface slide exists for the technology criterion. No slide tells a
  judge how to weight a criterion.
- **Every figure must have a lode source.** The deck states no number that
  [session baselines](../plans/session-baselines.md), [risk guardrails](../trading/risk-guardrails.md),
  [war-room summary](../war-room/summary.md), or [research summary](../research/summary.md)
  does not support.
- **The P&L is stated, not led with.** Two days of paper trading is not a track record and the
  deck says so in its own voice. The claim it does make is which part of the system produced
  the result: the deterministic exit, the fresh quote at the gate, and the cost of a delayed
  close.
- **The database is not in the repo.** `data/` is gitignored, so no `.db` file is tracked.
  `data/trader.db` is written by the run and read by `--audit`. Do not write that the audit
  database ships with the repository.
- **The defect list is shown, not hidden.** The history slide carries the refused universe,
  the buying silence, and the rejected hold that did nothing.
- The premise slide is the design rationale: the author cannot judge an options trade, so
  domain judgement is delegated to models and permission stays in deterministic code.

## Deck mechanics

One `<section>` per slide inside a scroll-snap container. Keys: arrows, Space, PgUp, PgDn,
Home, End, digits `1`-`9` and `0`, `P` to print, `?` for help. The location hash tracks the
current slide, so a single slide can be linked. `?export=N` renders slide N alone, full-bleed
and without chrome, for per-slide capture. The print stylesheet gives one landscape page per
slide, so `Ctrl+P` produces the PDF; there is no export script.

Colors are the Alpaca palette: `#FFD928` accent, `#0E0E0E` ink, `#F7F7F3` ground, `#EEEEEA`
rules, `#6B6B68` secondary. Slides 1, 5, 7, and 10 invert to the near-black ground. The deck
commits to one visual world and does not follow the viewer theme. Type is Archivo with IBM Plex
Mono for every measured figure.

## Related lodes

- [Operations summary](summary.md)
- [Competition constraints](competition-constraints.md)
- [Session baselines](../plans/session-baselines.md)
- [Console rendering](console-rendering.md)
