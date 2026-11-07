# Disposition primary missions

Windows and Android show the five Chapter Approved 2026-27 matchups for the
army's selected Force Disposition in Army Rules, beside the disposition controls.
Each row identifies the opposing disposition, the army's primary, and the
opponent's primary. **View primary card** opens the army's primary scoring text
in a scrollable in-app dialog. The dialog fetches the published 2026-27 card
from Wahapedia asynchronously, identifies its source and retrieval time, and
offers retry and a browser source link if loading fails. Closing cancels pending
work. Successfully loaded cards are kept in memory for up to one hour.

The matchup names work offline. Card text requires internet on the first load
and after the in-memory cache expires. No downloaded card text is bundled into
the installer or stored in army data. `PrimaryMissionCards` reads only the primary
deck, preserves VP, timing, OR/cumulative conditions, and renders underlined
“one” as “exactly one”. A changed or incomplete source fails visibly rather than
showing guessed scoring. This feature does not select an opponent for the player.

The shared offline mapping is in `src/GrimList.Core/DispositionPrimaries.cs`.
It applies only to Warhammer 40,000, edition 11, and a disposition unlocked by
the army's currently selected detachments. Missing, unsupported, or unselected
dispositions show explanatory text without inventing a matchup.

## Source and maintenance

- Authority: [Games Workshop Warhammer Event Companion v1.2](https://assets.warhammer-community.com/eng_wh40k_event_companion-pl87i44rzn-a7ieny8i9x.pdf).
- Checked: 2026-10-14. Version confirmed on page 1 of the downloaded PDF.
- Mapping: the disposition and mission headings on layout pages 9–53.
  Each of the 15 unordered pairings records its Layout A page in the shared data;
  reversing a pairing swaps the two mission names.
- PDF SHA-256: `1f44d9fa0297f60be6c4367041a65d1a98710c68b221d8c8e22ecd1674e7525e`.
- Secondary reference: [Wahapedia's disposition cards](https://wahapedia.ru/wh40k11ed/the-rules/warhammer-event-companion/#Force-Disposition-Cards).

The UI includes the mission season and source version, with a button opening
the official PDF. Updating the army catalogue does not replace this mission
season. When GW changes the mission pack, review all 25 directional results
and update the mapping, provenance, and expected assignments together.

## Verification

`dotnet run --project tests/GrimList.Tests -- --primary-missions-only` checks all
25 assignments and both player perspectives, edition/game boundaries, missing
selections, persistence, undo/redo, and detachment removal, plus existing
multi-disposition import checks. It also tests card parsing, asynchronous loading,
failure/retry, concurrent caching and cancellation. Add
`--primary-card-source <downloaded-html-path>` to verify all 25 cards from an
actual downloaded source page without printing the rules.

The Windows smoke-test switch `--primary-missions-only` exercises the rendered
Army Rules view using an isolated fixture and captures screenshots, including
the card dialog and its retry/cancellation behavior. Android
uses the same mapping with stacked matchup cards for narrow screens.
