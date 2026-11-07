Real-source regression fixtures
==============================

Death Guard:
  The unchanged historical inputs remain at:
  tests/fixtures/references/death-guard-reference-text.txt
  tests/fixtures/references/death-guard-rules-11.json
  Raw text SHA256: bbc55fab1d665194bc2a161f36f792d37d55c7809b95358251000746b1cdde4a
  Source: https://wahapedia.ru/wh40k11ed/factions/death-guard/
  Regression compares all 45 stratagems, names, scopes, CP and operative fields.
  The old pack also contains non-stratagem data, outside this parser's first scope.

Sisters browser excerpt:
  Source: https://wahapedia.ru/wh40k11ed/factions/adepta-sororitas/
  Browser-extracted page lines 350-359, accessed 2026-10-05.
  One complete card: Suffering and Sacrifice / Hallowed Martyrs.

World Eaters browser excerpt:
  Source: https://wahapedia.ru/wh40k11ed/factions/world-eaters/
  Browser-extracted page lines 356-365, accessed 2026-10-05.
  One complete card: Hack and Slash / Berzerker Warband.

The two excerpts preserve the returned text with browser citation wrappers and
line-number prefixes removed. They are not original HTML or complete faction
snapshots. Their imported Origin is browser-excerpt; coverage remains Partial.
They prove the SAME text parser accepts other real factions. They do not prove
full-page acquisition, full-faction coverage, or live HTML-format acceptance.

HTML and HTTP tests use explicitly synthetic inline fixtures, not invented game
content. Synthetic records never enter artifacts/references/ production output.
