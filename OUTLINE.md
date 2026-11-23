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
- No local .NET SDK needed: `./build.sh` builds inside `mcr.microsoft.com/dotnet/sdk:10.0` → `artifacts/Ducks/`.
- Test server: `docker compose -f docker/compose.yml up -d` → http://localhost:8096 (image `jellyfin/jellyfin:12.4`; Docker tags are `12.4`, not `12.4.0`).
  - Dev login: `admin` / `ducks`. Put sample media in `docker/media/movies/` (gitignored).
  - Plugin folder must be mounted **read-write**: Jellyfin writes `meta.json` into it and fails startup otherwise.
  - After `./build.sh`: `docker restart ducks-jellyfin`.

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

**Decided: 1** (File Transformation plugin). Option 3 stays the fallback if that plugin stops being maintained.

Verified jellyfin-web 12.4 DOM hooks (headless Chrome, 2026-11-23):
- Header is a React MUI app bar: links live in `.MuiToolbar-root .MuiStack-root` (Favourites = `a[href="#/home?tab=1"]`). Narrow screens: `.MuiDrawer-paper` list. Legacy `.skinHeader .headerRight` kept as fallback.
- Detail page: `.itemDetailPage:not(.hide) .detailPagePrimaryContent` (the `.detailRibbon` has a fixed height, so the widget goes below it).
- Cards: `.card[data-id]` → `.cardScalable` (React and legacy cards). Indicators are top-right, badge goes top-left.
- Unknown routes (`#/ducks-reviews`) render `#fallbackPage` and keep the URL; the script takes that page over.
- `window.ApiClient.ajax` handles auth; errors reject with the fetch `Response`.

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
├─ tests/Jellyfin.Plugin.Ducks.Tests/  # xUnit + Moq, coverage gate 100% lines
├─ tests/web/                         # node:test + jsdom for ducks.js, coverage gate 100% lines
└─ docker/compose.yml             # local 12.4 test server
```

## 5. Phases

| Phase | Deliverable | Est. |
|-------|-------------|------|
| 0 | Repo + this outline + answers to open questions | ✅ done |
| 1 | Plugin skeleton builds against 12.4, loads in Docker test server | ✅ done |
| 2 | SQLite repository + migrations + unit tests | ✅ done |
| 3 | REST API + auth/access filtering + UserData mirror | ✅ done |
| 4 | Script injection + detail-page widget (F1–F4) | ✅ done |
| 5 | Card badges (F5) | ✅ done |
| 6 | Reviews tab + feed page (F6–F7) | ✅ done |
| 7 | Durability: backups, orphan relink, upgrade test (12.4 → next) (F9) | ~2 h |
| 8 | Packaging: `build.yaml`, repository manifest JSON for install via plugin catalog, README | ~1 h |

## 6. Open questions

- **Q2** Reviews feed visibility: assumed all users see everyone's reviews with usernames. Admin toggle to hide names later?
- **Q4** Assumed a rating of exactly 0 ducks is allowed and distinct from "not rated".

## 7. Decisions log

| Date | Decision |
|------|----------|
| 2026-11-23 | Target Jellyfin 12.4 / .NET 10 |
| 2026-11-23 | Plugin-owned SQLite in `DataPath` is source of truth; mirror to `UserItemData.Rating` |
| 2026-11-23 | Script injection via File Transformation plugin (declared dependency) |
| 2026-11-23 | Aggregates are per item; no roll-up from episodes/seasons into series |
| 2026-11-23 | Feed shows every rating, newest first; text shown when present; edits update the entry (marked "edited"), not new entries |
| 2026-11-23 | Full unit test coverage required (user request): `./test.sh` fails below 100% line coverage (C# and JS) |
| 2026-11-23 | Folders, people, genres, studios, playlists are not ratable (API returns 404, widget hidden) |
| 2026-11-23 | Distribution via GitHub-hosted plugin repository manifest (GitHub Releases + Pages) |
