# Ship Watcharr

**A self-hosted library for the ships you follow on Archive of Our Own.**

Name the relationship tags you care about. Ship Watcharr keeps an eye on them, tells you when a
new work lands, and gives you one calm place to track what you have read, what you thought of it,
and what you want to read next.

## Why it exists

Following a ship on AO3 means going back to the same tag page, again and again, and re-reading it
for anything new. Then doing that for every other ship you follow. Bookmarks help you remember
what you liked, but nothing on the archive remembers what you have *read*, what you dropped, or
what you rated three and a half stars and never want to lose track of.

Ship Watcharr runs on your own hardware, checks your tags on a schedule, and builds a library
you can sort, filter and annotate. Your reading history stays yours, on your machine.

## What you get

- **A living library.** Follow a relationship tag and every work under it shows up, with the
  full blurb: authors, fandoms, tags, warnings, rating, word count, chapters, kudos, hits, and
  when it was last updated. New works arrive on their own, and a work that leaves a tag quietly
  drops out of your feed without taking your notes with it.
- **Your reading, tracked.** Mark each work To read, Reading, Read or Dropped. Rate it in half
  stars. Keep a private note. Star it as a favorite. None of this ever gets overwritten by a
  library update.
- **Saved filters.** AO3's filter sidebar, kept and reusable, with your own reading status and
  rating added as criteria. Name a set like *Long finished fics* or *Unread, no major deaths*,
  make one the default, and the Works view opens straight into it.
- **Notifications.** A badge and a list of what your followed ships gained since you last looked.
- **Downloads and an in-app reader.** Ask for a work as EPUB, MOBI, PDF or HTML and it lands in
  your Downloads. Open the EPUB inside the app, page by page, in your own text settings, and pick
  up where you left off. Optionally, every work you favorite fetches its own EPUB automatically.
- **Statistics.** A ship's whole corpus with your own reading laid over it: how much you have
  read, how you rate it, where the gaps are.
- **Shared with friends.** Several people can have accounts on one instance. A work is stored
  once and shared, while reading status, ratings, notes and filters belong to each person.
- **Your colours.** Nine built-in palettes, each with a light and a dark half, and drop-in
  support for Obsidian community themes.


## A good neighbour to the archive

AO3 is run by volunteers on donated money, so Ship Watcharr is built to be light on it and
honest with it. This is not a setting you can turn off. It is how the app works.

- **It says who it is.** Every request carries a User-Agent naming the software, a contact for
  the person running that copy, and a random id for the instance. AO3 can tell the tool apart
  from a browser and can reach *you* about *your* copy. Until a contact is set, the app will
  not talk to the archive at all.
- **It reads, and only reads.** It never leaves kudos, comments or bookmarks, never subscribes,
  never posts. The AO3 account it signs in with is used only to view pages, so that works marked
  for registered users show up in your library.
- **It paces itself.** One request at a time, instance-wide, with a random five to eight second
  gap between them no matter how many people or ships are involved. Redirects count as requests.
  Compressed responses are accepted. Pages are cached, so nothing is fetched twice within a
  quarter hour.
- **It backs off when asked.** A rate-limit response pauses the whole instance until the time
  AO3 names, and a busy or failing archive is retried with exponential backoff, never hammered.
- **It checks less when there is less to see.** Each ship is checked every six hours by default.
  A ship that has gone quiet is checked half as often, then a quarter, up to once a day, and
  snaps back the moment something new appears. Each ship's whole listing is read once while
  logged in, which is what finds the works AO3 shows only to registered users. After that, once a
  month, it re-reads only the works revised in the last three months, and the whole listing is
  read again only when an admin queues it. The cost is stated plainly: kudos, hits and bookmarks
  on older works nobody has revised stay as they were at the last whole read, and an older work
  leaving a tag goes unnoticed until the next one. Either walk stops on page one when AO3's count
  already matches the library.

One AO3 login per instance, entered by an admin and encrypted at rest. Because the library is
shared by everyone on the instance, there is exactly one set of requests per ship, however many
people follow it.

## Quick start

You need Docker with Compose. Nothing else: the default database is a single SQLite file.

```
git clone https://github.com/elreichle/ship-watcharr.git
cd ship-watcharr
cp .env.example .env
# edit .env: set AO3_OPERATOR_CONTACT to an email or URL AO3 can reach you at
docker compose up --build -d
```

Open `http://localhost:8080` (or whatever `APP_PORT` you set).

### First-run checklist

1. **Register.** The first account on a fresh instance becomes the admin. Sign-up asks only for
   a username and password.
2. **Confirm a contact.** If you set `AO3_OPERATOR_CONTACT` in `.env`, you are done. Otherwise
   save an email under **Settings → Account**, or set a contact under **System → AO3**.
   The app does nothing on the archive until one exists.
3. **Add the instance's AO3 login.** Under **System → AO3**, enter the AO3 username
   and password the instance should sign in with. It is stored encrypted and never shown again.
   Until it is saved, followed ships wait rather than run, and the Ships page tells you why.
4. **Follow a ship.** Go to **Ships**, type a relationship tag exactly as AO3 spells it, such as
   `Clarke Griffin/Lexa`, and follow it. The tag is confirmed against the archive a moment
   later, synonyms are folded into their canonical tag, and the first check starts right away.
5. **Invite people.** Send them the address. Each person registers their own account and follows
   their own ships. Anything already in the library is theirs to browse immediately.

Everything the instance needs to keep lives in one Docker volume, `app-data`: the database, the
encryption keys, the instance id and the admin settings. Back it up, and do not delete it. The
stored AO3 login is encrypted with the keys inside it.

## Around the app

| Where | What it is for |
|---|---|
| **Works** | Your library. Sort by last updated, kudos, hits, bookmarks, comments or word count. Search by title or author, filter by ship or by a saved filter. Set reading status, rating and notes inline, or open a work for the full tag list, publication date and summary. |
| **Favorites** | Everything you have starred, in one list. |
| **Notifications** | New works under your ships since you last looked. Mark one read, or all at once. |
| **Filters** | Named, reusable criteria, with a live count of how many works each one matches. |
| **Ships** | Follow and unfollow tags, see when each ship was last checked and when it is next due, whether its whole listing has been read while logged in, and when its recent works were last re-read. An admin can queue a full re-read of a ship from here. |
| **Downloads** | Files you have asked for, and the in-app reader for EPUBs. |
| **Statistics** | A ship's corpus against your own reading. |
| **Schedules** | A read-only view of the checking schedule and each run's outcome. |
| **Settings** | Your account, appearance, and download preferences. |
| **System** | Admin only: the AO3 connection and contact, and the database provider. |

Unfollowing a ship removes only your subscription. The works stay for anyone else who follows it,
and the schedule switches off only when the last person leaves.

### Themes

**Settings → Appearance** offers Tokyo Night (the default), Catppuccin, Solarized, Gruvbox, Nord,
Rosé Pine, Everforest, Dracula and One Dark, each with a light and a dark half that your colour
scheme setting picks between. Every text and background pair has been checked for readable
contrast.

Underneath, the whole interface is painted from Obsidian's CSS variables. Paste any Obsidian
community theme's `theme.css` into the same page and the app takes on its palette and typography.
Themes live in your browser only, never on the server. If one ever leaves the interface
unusable, open any page with `?safemode` and clear it.


## PostgreSQL, if you want it

SQLite is the default and needs no setup. To use PostgreSQL instead, either:

- log in as admin, open **System → Database**, choose PostgreSQL and paste a connection string.
  The app checks it can connect before saving anything, then restarts; or
- start with both compose files and set the `POSTGRES_*` values in `.env`:

  ```
  docker compose -f docker-compose.yml -f docker-compose.postgres.yml up --build -d
  ```

Switching providers starts the new database empty. There is no live copy between the two, so
choose before you have a library you care about.

## Configuration

Set these in `.env` for Docker Compose.

| Variable | Purpose |
|---|---|
| `AO3_OPERATOR_CONTACT` | Email or project URL AO3 can reach you at. Required before the app will talk to the archive. Can be changed later in the app. |
| `AO3_MIN_DELAY` | Smallest gap between requests (`HH:MM:SS`). Default `00:00:05`. |
| `AO3_MAX_DELAY` | Largest gap; each wait is drawn at random from the range. Default `00:00:08`. |
| `APP_PORT` | Host port the app is published on. Default `8080`. |
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Only used with `docker-compose.postgres.yml`. |

A choice saved in the app under **System** wins over the environment from then on, so a contact
saved there is not overridden by `.env` on the next restart.

## Roadmap

- A second source beyond AO3. The plumbing is per-site rather than AO3-shaped, so this is an
  addition, not a rewrite.
- Ideas and requests are welcome in the issue tracker.

## For developers

The stack is ASP.NET Core (.NET 10) serving a React + TypeScript frontend from one container,
with SQLite or PostgreSQL through EF Core. Local setup, architecture, the data model and how the
archive-facing pieces fit together are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

<!--
## Support the project

If Ship Watcharr is part of your reading life, you can keep it going here:
[Ko-fi](https://ko-fi.com/YOUR-NAME)
-->

## License

[MIT](LICENSE).
