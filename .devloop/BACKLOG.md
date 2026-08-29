# Backlog

Findings and ideas the loop does **not** work. One line each: `- <where> — <what> (source: T<n> review | T<n> iteration)`.
A human promotes an item into `tasks.md` between runs; the loop only adds here.

- `ShipsController` — `WatchedShip.NotificationsEnabled` has no writer: no PATCH on a subscription, and the field is read-only in `WatchedShipDto` and the frontend types. T16 made it load-bearing, so today the only way to stop being told about a ship is to unfollow it, which also stops its works reaching the library — the conflation the switch exists to prevent. Neither T16's nor T17's `delivers` covers it. (source: T16 review)
- `WorksController.cs:117`, and the shape T16 fixed in `NotificationsController` — `Skip((page - 1) * pageSize)` overflows int for a large `page`, so a malformed query string is a 500 rather than an empty page. (source: T16 review)
- Controllers write timestamps from `DateTime.UtcNow` (`WorksController`, `DownloadsController`, `SavedFiltersController`, and now `NotificationsController.MarkReadAsync`) while services take the injected `TimeProvider`. A notification's `CreatedAt` and `ReadAt` therefore come from two clocks, which under a test's fake clock lets a row be read before it was created. One clock or the other. (source: T16 review)
