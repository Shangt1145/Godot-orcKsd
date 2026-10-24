# Saved mechanics demo fixture

Extracted from the read-only workshop backup `artifacts/mechanics-investigation-20261023-231859/`.
Contains only `pack/diy-demo-mechanics` and the eleven `diy/demo-*` seed cards; excludes other packs and player-created cards.

Keep the saved definitions unchanged when fixing the runtime: launch declares keyword value 2 even though the saved program default is 1. This distinction reproduces the missing-instance-record bug. Tests load copies in a temporary directory and never run the seed tool against the user's workshop.
