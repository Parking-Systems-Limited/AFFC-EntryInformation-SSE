# FIX-notes — AFFC SSE diagrams v2

**Date:** 2026-09-24 (HKT)  
**Scope:** Rewrite all four mechanism diagrams under `/workspace/affc-sse-mechanism/`  
**GitHub:** not pushed (Leon review first)

## What was fixed

### Layout / grid
- Unified margins **40px**, column gutters **24px**, row gaps **24px**
- Consistent corner radius **10–12** and stroke widths across peer boxes
- Titles left-aligned at **x=40**; legends top-right without overlapping titles
- Architecture: three-column outer grid (Postman / Backend / LPRS) + Frontend aligned under Backend; Backend 2×2 inner grid; Publish arrow confined to gutter (no overlap over REST list)
- Sequence: five evenly spaced actors; lifelines centered under actor boxes; even vertical step spacing (~40px); dual-publish note tied under step ⑥; footer keepalive + legend in one bar (no orphan floating labels)
- Reconnect: three equal-width columns in row 1 and row 3; two equal halves in row 2; state-machine arrows cleaned; no loose footer words
- Failure paths: **five equal-width** outcome cards on one row; bottom notes as clean bullets in one panel

### Overlap / stray text
- Removed Publish-over-REST and SSE-title collisions by re-placing Hub Publish / Subscribe arrows on gutters only
- Arrow labels placed above paths with clearance
- No decorative leftover text at canvas bottom (footers are intentional single-line notes)

### CJK tofu (□) in PNG
- Root cause: SVG used `"Noto Sans TC"` / generic stacks; cairosvg did not reliably map CJK; `.mono` stack had no CJK face so Chinese in mono lines became □
- Fix:
  - Primary `font-family`: **`Noto Sans CJK TC`**
  - Extracted `fonts/NotoSansCJKtc-Regular.otf` + `Bold.otf` from system TTC
  - `fonts/fonts.conf` + `FONTCONFIG_FILE` when running cairosvg
  - Mono stack leads with Noto Sans CJK TC; Chinese footers use `.t` not `.mono`
- Verified: regenerated PNGs render Traditional Chinese (titles, body, notes) without □

### Content preserved (unchanged facts)
- Architecture: External/Postman, Backend :5100, MessageHub, SSE `/sse/{clientKey}`, LPRS `192.168.103.59:8012`, Frontend EventSource, clientKey EN21, REST/SSE/Hub colors, ping 20s
- Sequence: SSE subscribe → vehicle happy path → clear; dual-publish vehicle+info
- Reconnect: before/after, ping 20s, backoff 1→2→5→10→30s, watchdog 90s, visibility, UI connecting/open/reconnecting/offline
- Failure: timeout 504, unreachable 502, biz e.g. 997, success contrast, SSE closed; `event:error` vs `source.onerror`

## Deliverables overwritten
- `architecture.svg` / `.html` / `.png`
- `sequence-happy.svg` / `.html` / `.png`
- `reconnect.svg` / `.html` / `.png`
- `failure-paths.svg` / `.html` / `.png`
- `README.md` (v2 note)
- `FIX-notes.md` (this file)
- Helper: `generate_v2.py`, `fonts/`

## PNG sizes (post-v2)
| File | Approx size |
|------|-------------|
| architecture.png | ~164 KB (1280×920) |
| sequence-happy.png | ~102 KB (1280×960) |
| reconnect.png | ~215 KB (1280×1000) |
| failure-paths.png | ~134 KB (1280×820) |
