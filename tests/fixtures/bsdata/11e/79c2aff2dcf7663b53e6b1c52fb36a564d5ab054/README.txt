BSData 11e nested-detachment regression fixture

Source: https://github.com/BSData/wh40k-11e/tree/79c2aff2dcf7663b53e6b1c52fb36a564d5ab054
Source date: 2026-10-26
Archive SHA-256: ef02733b5aeda3f4fbf00b6d37efa218dc6910b3db2aaa75424056f7a0da5549
Archive size: 4,820,206 bytes

Unmodified repository.zip and source.json retained from the user's completed
BSData download. No user database or army data is included. The test verifies
the archive hash before parsing and selects the Custodes catalogue's dependency
closure from the archive, using the production ImportAll path.

The source contains thirteen Custodes detachments: seven direct choices and
six under a nested selection group with a maximum-one constraint. This catches
the original partial-import bug and verifies current rule/enhancement scopes,
complete source authority, generic group-limit enforcement, five retired
definitions and saved-army preservation through persistence/recovery.

Keep the pinned archive for offline, repeatable checks. Its observation date
does not claim it is the newest upstream revision when tests are run later.
