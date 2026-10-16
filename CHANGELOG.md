# Version 0.5.28

Includes all updates from versions 0.5.25 through 0.5.28.

## Junction and roundabout improvements

- Combines compatible surface pieces into larger areas, reducing unnecessary fragmentation at junctions and roundabouts.
- Reduces repeated clipping and geometry processing when generating and rebuilding road surfaces.
- Improves the joining of curved junction pieces and the handling of small precision gaps and complex outlines, reducing cases that produce large numbers of tiny surfaces.
- Preserves roundabout islands, protected bike and bus lanes, sidewalks and planted areas when combining surfaces. Different finishes and road levels remain separate.
- Fixes highway-only roundabouts using street rules, which could leave them without the intended surfaces. Roundabouts with street approaches retain their street-rule selection.

## Manual surface editing

- Adds **Make generated surfaces editable** under Tools. Select roads and press **Apply changes** to convert their generated surfaces into independent areas.
- Adds **Open surface editor** to reshape areas using the game's surface tool. Choose an **Editor surface** first; editing applies that finish.
- Converted areas keep manual edits through rebuilds, rule changes and road changes. They remain after road removal and are kept by the vanilla restoration tools.
- Saves the road's manual setting with the city and carries it through road splits and replacements.
- Adds **Resume automatic surfaces** to reactivate the selected roads' saved rules. Independent areas are kept, so remove unwanted overlaps manually.

## After updating

- Use **Rebuild applied surfaces** to replace older fragmented overlays while keeping saved road finishes. **Refresh surfaces** only refreshes the surface list.
- If a roundabout has no surface after rebuilding, select a road near it and use **Cycle junction surface** under Tools to apply a junction finish.
- To manually edit an entire junction or roundabout, convert all its approaches because its surfaces can belong to several roads.
- Keep Road Surface Filler and the relevant surface assets enabled for converted areas to render.
