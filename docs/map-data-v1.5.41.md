# Map data review — v1.5.41

Reviewed 2026-10-26 using the live English Fandom MediaWiki API, not search-result snippets.

## Extractions

Membership, names, factions and requirements follow [Customs](https://escapefromtarkov.fandom.com/wiki/Customs) revision 363971 and [Lighthouse](https://escapefromtarkov.fandom.com/wiki/Lighthouse) revision 362692. Interactive map revisions are 363978 / 360408. World coordinates supplement these lists from the game scene feed `https://json.tarkov.dev/pve/maps`; that feed does not determine Wiki membership. The regular/PvE scene feeds were compared during review.

- Customs: 16 Wiki extraction locations, 4 transits. Adds Boiler Room Basement (Co-op), Railroad Passage (Flare), Gate Near Medblock, ZB-1013; normalizes renamed Reserve exits, factions, basement floors and current conditions. Old Wiki-absent scene markers are removed from the two-map source overlay, not from user-created markers.
- Lighthouse: 12 extraction locations and 4 transits including Transit to Icebreaker. Corrects shifted world positions and preserves distinct Southern Road / Southern Road Landslide exits.
- RUAF Roadblock is absent from the current Customs Wiki table but remains in the PvE scene. The user could not recently confirm its availability. It remains **PvE-only, review required**, not a verified active extraction.
- Road to Military Base V-Ex has no matching scene extraction zone. Its coordinate is derived from the English Wiki interactive map by an affine fit using 13 shared reference points. Maximum reference-point residual is 16.69 m; this is not an accuracy guarantee. The tooltip explicitly distinguishes this estimated position. All other snapshot positions are scene coordinates.
- A map-label hover shows requirements, source, reviewed revision/date and uncertainty. A Wiki revision check marks changed sources for review; it does **not** claim to automatically verify or relocate every exit. A new reviewed catalog version is required to replace that snapshot safely.

The generator `tools/build_extraction_catalog.py` accepts a folder containing saved `wiki-maps.json`, `wiki-policies.json`, `maps-pve.json`, and `maps-en.json`. It requires NumPy and refuses a fit with fewer than six reference points or a maximum residual of 20 m or more. Generated IDs are deterministic; existing matching DB marker IDs are retained on import. Imported markers are versioned and transactional.

## Quest reliability

- Coordinate-less map-specific visit, mark, stash and pickup goals remain in the map drawer as “위치 미확인”; no artificial pins are created. Kill goals retain “목록 전용”. Delivery, build and extraction-only goals are not given invented locations.
- Ambiguous goal matching no longer reuses metadata merely because token similarity exceeds 0.55. Conservative fallback matching checks goal action, ordinal, map, named item, uniqueness and a higher similarity threshold. Uncertain matches are recorded for review. Exact unique text matches retain stable identities; explicit Wiki map changes discard inherited coordinates from a different map.
- Curated corrections record their verified Wiki revision. A newer observed page revision stops the old correction and clears its unverified pins without deleting quest IDs or user progress. This is intentionally conservative: a non-location page edit can also request review.
- Options → Data update → “지도 데이터 점검” is read-only. Missing coordinates are review candidates, **not a confirmed error count**. The existing CI regression step checks the packaged database for malformed coordinates. A detailed report is available in the app or via `--audit-map-data database.db report.txt`; changing workflow permissions is not required.

This release does not claim to have manually located every unmapped objective or performed an in-game traversal of every exit.
