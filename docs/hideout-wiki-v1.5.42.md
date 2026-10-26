# Hideout Wiki refresh — v1.5.42

Verified 2026-10-26 against the live English Wiki, using the MediaWiki revision API rather than search-result previews.

- [Hideout](https://escapefromtarkov.fandom.com/wiki/Hideout?oldid=364437): revision 364437. All 26 normal stations, 68 levels and 317 mandatory material/currency requirements were compared with the actual installation. No quantities differed. 29 station/level/material entries (20 distinct items) incorrectly required FIR in the old helper. All 29 differences were also checked against the 20 individual item pages during the preceding audit.
- [Seasons](https://escapefromtarkov.fandom.com/wiki/Seasons?oldid=363245): revision 363245; [Seasonal change template](https://escapefromtarkov.fandom.com/wiki/Template:Seasonal_change?oldid=355356): revision 355356. Season 1 KORD BREACH has a separate hideout FIR exemption. The loader applies it only to SeasonalPvp, within the verified date interval starting 2026-08-03 and ending at the start of the published end date, 2026-12-07 (UTC date, conservatively exclusive). Permanent PVP/PVE always retain the base requirements. An unknown future season is never granted a permanent, hard-coded exemption.

The bundled snapshot contains structured requirement facts, not the full Wiki article. It provides offline bootstrap without overwriting a newer successful Wiki refresh. To reproduce it:

`TarkovHelper.RegressionTests --build-hideout-snapshot saved-mediawiki-response.json hideout_wiki_snapshot.json`

The response must contain Hideout, Seasons and Template:Seasonal change with revision IDs, timestamps and main-slot contents. The normal live path downloads these three pages in one request.

## Safety and scope

- Existing station IDs, levels, requirement IDs and stored item IDs remain stable. Existing rows update only Count, FoundInRaid and UpdatedAt when their material requirements actually change. New materials resolve to existing catalog identities; ambiguous/unresolved items abort the refresh before any write.
- A transaction applies each validated source, preceded by a consistent SQLite backup and quick_check. Missing facilities/levels, empty replacements, unknown numeric syntax, unknown condition templates, duplicate requirements and suspicious bulk removals fail closed. New facilities lacking a verified existing identity are reported instead of invented.
- Optional Defective Wall mopping material and the Christmas-only tree are not added to mandatory construction totals. Crafts, construction durations and trader/skill/station prerequisites are not silently rewritten by this material refresh.
- The main data-update button now includes hideout materials. Each source is isolated: a quest outage does not discard a successful hideout refresh, and partial failures are reported as warnings. Manual and existing background refresh paths use the same coordinator. Live hideout/item views are rebound to the refreshed module objects without saving/resetting construction progress.
- User progress, inventory and custom map markers live in user_data.db and are not modified by this service.

Regression coverage includes the full bundled dataset, generated Wiki parsing fixtures, optional/holiday exclusion, monetary counts, base/seasonal/expired FIR rules, production-loader profile switching, live requirements rebinding with built levels preserved, source-outage isolation, backups, idempotence, unresolved/partial/malformed input rejection, future quantity updates, and refusal to downgrade from newer data.
