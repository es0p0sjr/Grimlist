Chapter Approved 2026-27 mission collection
Collected: 2026-10-14

secondary-missions-2026-27.json contains 18 unique secondary mission cards.
The Attacker and Defender decks each reference those same 18 definitions.
All 18 support Tactical play. The four Fixed choices are A Grievous Blow,
Assassination, Bring It Down, and Engage on All Fronts.
primary-missions-2026-27.json adds all 25 primary cards with 100 scoring
conditions. Blocks with multiple WHEN headings are split into separate timing
groups; end-of-battle scoring is retained as a separate timing.

Contents
- Stable card and scoring-condition IDs, game edition and mission season.
- Fixed/Tactical eligibility and separate mode-specific VP values.
- Instructions, including draw-time choices and persistent nominations.
- Action start timing, eligible units, use limit, completion and effect.
- Scoring groups with round restrictions and scoring timing.
- Individual conditions, alternative/cumulative relationships and local VP caps.
- Underlined terms preserved; underlined 'one' rendered as 'exactly one'.
- Source URL, snapshot retrieval time/hash, official limits and FAQ provenance.

Sources
Card text (community transcription):
https://wahapedia.ru/wh40k11ed/the-rules/warhammer-event-companion/#Secondary-Mission-deck
Inventory and Fixed eligibility cross-check:
https://game-datamissions.com/11th/secondary-missions
Official authority for event procedure, scoring limits and clarifications:
https://assets.warhammer-community.com/eng_wh40k_event_companion-pl87i44rzn-a7ieny8i9x.pdf
The downloaded official PDF identifies itself as version 1.2.

Verification scope
The collection contains 19 scoring groups, 27 conditions and 31 VP variants,
plus 11 instruction sections, two actions and one designer's note. Each action
has all five labelled fields. Inventory and Fixed eligibility were checked
against GDM. GW Event Companion pages 2 and 4 supply overall scoring limits
and the Plunder/Beacon clarifications. Card text has not been independently
audited against every physical card; it remains a sourced community transcription.

Use in the battle UI
Both JSON files are embedded into GrimList.Application for offline use on both
platforms. BattleMissions reads the mode-specific scoring definitions;
BattleRecord calculates awards from player-entered counts/conditions. It enforces
round restrictions, scoring turns, OR choices, bonus dependencies, card caps,
Tactical one-time scoring and global caps. Players still judge battlefield
eligibility. Nomination/action state and automated deck management remain future
work; see docs/battle-tracker.txt. The JSON itself remains reference data.
The 15 VP round cap, 45 VP battle cap and 20 VP per Fixed card battle cap are
separate from a condition's local cap. A null local cap does not waive those
overall limits. Keep role (Attacker/Defender) separate from who takes first turn.

Refresh from a saved UTF-8 copy of the source page:
python scripts/Collect-SecondaryMissions.py --source-html <snapshot.html> --output data/missions/11e/secondary-missions-2026-27.json
python scripts/Collect-PrimaryMissions.py <snapshot.html> data/missions/11e/primary-missions-2026-27.json

The collector validates inventory, eligibility, deck roles and source structure
before replacing the output. It rejects unknown card sections and ambiguous
scoring values. Source HTML is research evidence, not an application resource.
Refreshes require review and the battle tracker checks before release. The primary
source is the same page's Primary-Mission-deck section. Full primary card wording
also remains a community transcription; do not describe it as an independent
physical-card audit.
