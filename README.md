# Ducks 🦆 — ratings and reviews for Jellyfin

Ducks is a Jellyfin plugin. It lets every user rate anything from 0 to 4 ducks, in half steps, and write a short review if they want to.

- **Details page:** pick your ducks with the mouse or keyboard. You can add a review title and text, and change or remove it later. Your rating and the average of all users appear next to each other.
- **Posters:** every item that has ratings shows its average in the top-left corner of its thumbnail.
- **Reviews tab:** a new link in the top bar (in the menu on phones) opens a feed of every rating and review from every user, newest first.
- **Durable:** ratings live in Jellyfin's data folder with daily backups, so Jellyfin and plugin updates don't touch them. If a file moves and its item ID changes, its ratings follow it.

It requires **Jellyfin 12.4** and the **File Transformation** plugin, which loads the Ducks script into the web UI without changing Jellyfin's files.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add the File Transformation repository: `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
3. Add the Ducks repository: `https://raw.githubusercontent.com/bytewerk-cloud/jellyfin-user-ratings/master/manifest.json`
4. Under **Catalog**, install **File Transformation** and then **Ducks**.
5. Restart Jellyfin, then reload the browser tab.

To confirm it worked, open **Dashboard → Plugins → Ducks**. The Status section should say "✅ Web UI active".

## Where data lives

| What | Path (Docker: under `/config`) |
|------|------|
| Ratings database | `data/ducks/ducks.db` |
| Daily backups (the last 7 are kept, configurable) | `data/ducks/backups/ducks-YYYYMMDD.db` |
| Copy made before any database upgrade | `data/ducks/backups/ducks.pre-vN.db` |

- Updating or uninstalling the plugin never deletes this folder.
- Each rating is also copied into Jellyfin's own user rating field (0–10 = ducks × 2.5), so other Jellyfin clients can read it.
- The Ducks database is the source of truth.

**Restore a backup:**

1. Stop Jellyfin.
2. Copy a backup over `data/ducks/ducks.db`.
3. Delete any `ducks.db-wal` and `ducks.db-shm` files next to it.
4. Start Jellyfin.

**JSON export and import (admins):** `GET /Ducks/Export` and `POST /Ducks/Import`.

## Scheduled tasks

Both appear under Dashboard → Scheduled Tasks → Ducks.

| Task | Default schedule | What it does |
|------|------------------|--------------|
| Back up ratings | daily 03:00 | Copies the database and removes old copies |
| Re-link orphaned ratings | at startup, daily 04:00, and 30 s after a library scan adds items | Moves ratings whose item disappeared to the matching item, found by file path or IMDb/TMDb/TVDb ID |

## API

Every call needs a logged-in user. A user can only change their own rating.

| Method | Route | Body / query | Notes |
|--------|-------|--------------|-------|
| GET | `/Ducks/Items/{itemId}` | | Your rating, plus the average and count for the item |
| PUT | `/Ducks/Items/{itemId}` | `{"Rating": 3.5, "Title": "…", "Body": "…"}` | Title and body are optional |
| DELETE | `/Ducks/Items/{itemId}` | | Removes your rating. A copy stays in the history table |
| POST | `/Ducks/Aggregates` | `{"ItemIds": [...]}` | Averages for many items, used for the poster badges |
| GET | `/Ducks/Feed` | `?limit=20&cursor=…` | All ratings, newest first, limited to items the caller can see |
| GET | `/Ducks/Status` | | Admin only |
| GET / POST | `/Ducks/Export`, `/Ducks/Import` | | Admin only |

Folders, people, genres, studios and playlists can't be rated, and these routes return 404 for them.

## Development

You need Docker. A local .NET 10 SDK or Node is used if it's installed, and the build falls back to containers if not.

```sh
./build.sh                                  # → artifacts/Ducks/Jellyfin.Plugin.Ducks.dll
./test.sh                                   # C# + JS unit tests; fails below 100% line coverage
docker compose -f docker/compose.yml up -d  # Jellyfin 12.4 on http://localhost:8096 with the plugin mounted
docker restart ducks-jellyfin                # pick up a new build
docker/jf.sh admin ducks GET /Ducks/Status  # authenticated API call to the test server
```

- **Browser smoke test** against a running server, using your installed Chrome: `cd tests/e2e && npm install && JF_USER=admin JF_PASS=ducks npm run smoke`. Run it after a Jellyfin update to check that the web UI still has the elements Ducks attaches to.
- **Release:** push a tag such as `v0.3.0.0`. The tag message becomes the changelog. GitHub Actions then runs the tests, builds `ducks_<version>.zip`, creates a GitHub Release and adds the version to `manifest.json` on `master`.
- **Design notes, decisions and how the web UI is hooked in:** see [OUTLINE.md](OUTLINE.md).
