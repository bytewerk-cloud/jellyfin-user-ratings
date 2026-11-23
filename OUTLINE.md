# Jellyfin Ratings ("Ducks") — Project Outline

Working document for building this plugin. Keep it current: tick off phases, record decisions, park open questions.

## 1. Goal

A third-party Jellyfin server plugin that adds user ratings and short reviews to every media item, plus the web UI to use them.

| # | Feature | Where |
|---|---------|-------|
| F1 | Rate any item 0–4 ducks in half-duck steps (0, 0.5 … 4.0) | Item detail page |
| F2 | Optional review: title + short body. Rating can be saved without it | Item detail page |
| F3 | User can edit or replace their rating/review at any time | Item detail page |
| F4 | Show own rating + aggregate (average + count) of all users | Item detail page |
| F5 | Aggregate duck badge, top-left of the poster, only if ratings exist | Every card/thumbnail grid |
| F6 | "Reviews" tab in the top nav bar | Web header |
| F7 | Reviews page: chronological feed of all reviews by all users | New page |
| F8 | No social features (no likes, comments, follows) | — |
| F9 | Data survives Jellyfin updates and plugin updates | Storage design |

Item types: Movie, Series, Season, Episode, plus anything else with a detail page (music album, book, etc.) unless we decide to restrict it.

## 2. Target platform

- **Jellyfin 12.4** (Oct 2026). Versioning went 10.11.x → 12.0; 12.x requires plugins rebuilt against the 12.x packages.
- **.NET 10** (12.4 ships on .NET 10.0.14).
- NuGet: `Jellyfin.Controller` / `Jellyfin.Model` pinned to `12.4.*`, `targetAbi` `12.4.0.0` in `meta.json`/`build.yaml`.
- Local dev box currently has **no `dotnet` SDK** → install .NET 10 SDK (`brew install --cask dotnet-sdk`) before Phase 1.
- Test server: Docker `jellyfin/jellyfin:12.4` with a throwaway config volume and a few sample media files.

## 3. Architecture

```
┌──────────────── Jellyfin server 12.4 ────────────────┐
│  Plugin DLL (Jellyfin.Plugin.Ducks)                  │
│   ├─ RatingsController   /Ducks/...  (REST, auth'd)  │
│   ├─ RatingsRepository   SQLite in DataPath          │
│   ├─ UserData sync       mirrors rating → Rating fld │
│   └─ Web assets          ducks.js / ducks.css (embedded) │
└──────────────────────────────────────────────────────┘
           ▲ fetch (ApiClient token)
┌──────────────── jellyfin-web (unmodified) ───────────┐
│  ducks.js injected into index.html at serve time     │
│   ├─ detail page widget  (rate + review form)        │
│   ├─ card badge          (MutationObserver)          │
│   ├─ header "Reviews" button                          │
│   └─ #/ducks/reviews feed view                        │
└──────────────────────────────────────────────────────┘
```

### 3.1 Storage (F9 — the critical part)

- **Source of truth:** plugin-owned SQLite file at `{IApplicationPaths.DataPath}/ducks/ducks.db`.
  - `DataPath` (`/config/data` in Docker) is *not* touched by server updates or plugin updates. Plugin updates only replace `plugins/Ducks_x.y.z.0/`.
  - Do **not** store data inside the plugin folder or in the plugin's XML configuration.
- **Schema (v1):**
  - `Reviews(Id PK, UserId, ItemId, Rating REAL CHECK 0..4 step .5, Title NULL, Body NULL, CreatedUtc, UpdatedUtc, UNIQUE(UserId, ItemId))`
  - `ReviewHistory(Id PK, ReviewId, Rating, Title, Body, ChangedUtc)` — keeps edits so the feed is a real history and nothing is ever overwritten silently.
  - `SchemaVersion(Version)` — forward-only migrations run on plugin start.
- **Mirror to Jellyfin:** on save, also set `UserItemData.Rating` = ducks × 2.5 (Jellyfin uses 0–10). Gives other clients/API consumers the value for free and is a second copy of the number. Mirror is write-only; plugin DB wins on conflict.
- **Item identity across rescans:** Jellyfin item IDs can change if a library is removed/re-added. Store provider IDs (IMDb/TMDb/TVDb) + path alongside `ItemId` so orphaned rows can be re-linked by a scheduled task (Phase 6).
- **Backups:** daily scheduled task writes `ducks-YYYYMMDD.db` copies (keep last 7) next to the DB. Export/import JSON endpoint for admins.

### 3.2 Server API (all require an authenticated user)

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/Ducks/Items/{itemId}` | My rating/review + aggregate (avg, count) |
| PUT | `/Ducks/Items/{itemId}` | Upsert my rating (+ optional title/body) |
| DELETE | `/Ducks/Items/{itemId}` | Remove my rating |
| POST | `/Ducks/Aggregates` | Batch: `[itemIds]` → `{itemId: {avg, count}}` (for card badges) |
| GET | `/Ducks/Feed?startIndex&limit` | Review feed, newest first, paginated |
| GET | `/Ducks/ducks.js`, `/Ducks/ducks.css` | Embedded client assets (anonymous) |

Rules: user can only write their own row. Feed and aggregates only include items the requesting user is allowed to see (library access / parental rating). Title ≤ 100 chars, body ≤ 2000 chars (configurable).

### 3.3 Web UI injection

jellyfin-web has no official extension point. Options:

1. **Depend on the "File Transformation" plugin** (community) to inject `<script src="/Ducks/ducks.js">` into `index.html` in memory. Survives web/server updates. Extra plugin to install.
2. **Rewrite `index.html` on disk at plugin startup.** No dependency, but needs write access to the web dir (fails in read-only Docker images) and is undone by every server update until next restart.
3. **Middleware in our plugin** that rewrites `index.html` responses. No dependency, survives updates, but relies on internal ASP.NET pipeline hooks that may break between Jellyfin versions.

Leaning: **1**, with 3 as fallback. → open question Q1.

Client script responsibilities:
- Detect route changes (`viewshow` event / hash change) and act on the item detail page.
- Detail page: duck widget (click/tap halves, keyboard accessible), "Write a review" expander with title + body, own rating, aggregate "★ 3.5 ducks · 12 ratings".
- Cards: `MutationObserver` on card containers, collect `data-id`s, batch-call `/Ducks/Aggregates` (debounced, cached in memory with short TTL), render badge in `.cardImageContainer` top-left. Must not collide with existing indicators (played check / unplayed count are top-right).
- Header: add "Reviews" button next to existing header buttons; navigates to `#/ducks/reviews`.
- Reviews page: rendered by our script into the main view container; infinite scroll; each entry = user avatar+name, item poster+title (link), ducks, review title/body, date, "edited" marker.
- Theme: use Jellyfin CSS variables so it follows the active theme. Duck icon as inline SVG (full + half).

## 4. Repository layout (planned)

```
jellyfin-ratings/
├─ OUTLINE.md
├─ README.md
├─ build.yaml                     # jprm manifest
├─ Jellyfin.Plugin.Ducks.sln
├─ src/Jellyfin.Plugin.Ducks/
│  ├─ Plugin.cs, PluginServiceRegistrator.cs
│  ├─ Configuration/              # limits, enable/disable features
│  ├─ Data/                       # repository, migrations
│  ├─ Api/                        # controller + DTOs
│  ├─ Tasks/                      # backup, relink orphans
│  └─ Web/ducks.js, ducks.css     # embedded resources
├─ tests/Jellyfin.Plugin.Ducks.Tests/
└─ docker/compose.yml             # local 12.4 test server
```

## 5. Phases

| Phase | Deliverable | Est. |
|-------|-------------|------|
| 0 | Repo + this outline + answers to open questions | done / waiting on Q's |
| 1 | Plugin skeleton builds against 12.4, loads in Docker test server | ~1 h |
| 2 | SQLite repository + migrations + unit tests | ~2 h |
| 3 | REST API + auth/access filtering + UserData mirror | ~2 h |
| 4 | Script injection + detail-page widget (F1–F4) | ~3 h |
| 5 | Card badges (F5) | ~2 h |
| 6 | Reviews tab + feed page (F6–F7) | ~2 h |
| 7 | Durability: backups, orphan relink, upgrade test (12.4 → next) (F9) | ~2 h |
| 8 | Packaging: `build.yaml`, repository manifest JSON for install via plugin catalog, README | ~1 h |

## 6. Open questions

- **Q1** Web injection: OK to require the File Transformation plugin? (see 3.3)
- **Q2** Reviews feed visibility: all users see everyone's reviews, with usernames? Or option to hide names?
- **Q3** Should ratings on episodes/seasons roll up into the series aggregate, or stay separate per item?
- **Q4** Can a rating of exactly 0 ducks be given (distinct from "not rated")? Assumed yes.
- **Q5** Feed: show only reviews with text, or every rating too? Show edit history or only latest version?
- **Q6** Install method: plugin repository URL (needs hosting, e.g. GitHub Pages) or manual zip drop-in?

## 7. Decisions log

| Date | Decision |
|------|----------|
| 2026-11-23 | Target Jellyfin 12.4 / .NET 10 |
| 2026-11-23 | Plugin-owned SQLite in `DataPath` is source of truth; mirror to `UserItemData.Rating` |
