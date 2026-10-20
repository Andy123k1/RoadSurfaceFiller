# Version 0.5.39

Combines all changes from versions 0.5.36 through 0.5.39 into one update.

## Default and city styling profiles

- Saves General rules, Segment rules and named styling profiles with each city. Adds a Styling profile selector and a Profiles tab to create, rename, duplicate and delete profiles. Each city remembers its selected profile.
- Makes the user's shared library from before the update the starting **Default**, including all General rules, Segment rules and saved segment update preferences. Captures this setup once in settings, including when it is empty.
- Starts cities without profiles with an independent Default. Create an empty profile or duplicate Default for a different city's style. Changes in one city do not alter another city's profiles or the original Default setup.
- Loading a city that already has profiles uses its saved library without adding an imported profile. Renaming or deleting a city's Default does not recreate it. The original Default remains available for deliberate copying.
- Copies complete profiles or individual General/Segment rules between profiles and cities. Rule cards offer **Copy to profile**. Copies receive independent rule identities; copying a rule appends it without replacing the destination's existing rules.
- Keeps matching saved segment links, update preferences and differing saved road definitions. Presets in inactive profiles remain available to their linked roads, and missing surface names remain identified.
- Switching profiles keeps applied road finishes. General rules still require **Apply city rules**; Segment rules still use the road tool and each preset's update preference.

## Profile deletion fix

- Fixes deleting the active profile disabling every tab and leaving the dropdown with a missing profile ID.
- Selects a remaining profile before removing the old entry and automatically recovers an invalid saved selection. At least one profile must remain.
- Keeps applied road finishes, junction choices and independent editable areas when removing a profile.

## UI overhaul and more intuitive tools

- Simplifies **General rules**, **Segment rules** and the tools panel, removing redundant switches, lane-handling menus and old selected-road junction controls.
- Adds thumbnail previews beside surface choices and selected finishes. Previews load as choices come into view; unavailable thumbnails show **No image**.
- Filters surface pickers to installed assets that support rendering over roads. Existing saved choices are retained, with a warning when a finish cannot render over roads.
- Includes **Original finish** for every surface target. Sidewalks, bike lanes and bus lanes also offer **Use road pavement** alongside custom surfaces in both rule editors.
- **Use road pavement** follows the pavement chosen for that specific segment, including its segment-rule override. Changing the road finish also updates linked targets. Links survive saving, preset updates and road splits or replacements.
- Checking a target reveals its surface picker. Unchecking a target in a segment preset removes its local override so existing settings can apply. Older presets retain their saved behavior, and explicit Original finish choices are respected when rules overlap.
- Moves **Fill paved shoulders with bus-lane surface** beneath the Bus lanes picker, visible only when Bus lanes is checked. It requires a custom bus finish; Original finish and Use road pavement turn separate shoulder filling off.

## Click junctions directly

- **Cycle junction surface** now activates a tool for clicking junctions and roundabouts directly. Hover to highlight the target and see its current finish; each left-click cycles once.
- The tool stays active across repeated clicks and different junctions. Right-click or choose **Exit junction tool** to leave.
- **Restore automatic** uses the same direct targeting, with modes switchable from the active tool panel. Selecting an approach road or choosing its nearest/other end is no longer required.
- Retains saved junction choices, bus-lane cycling options and protected bike surfaces.

## Easier editing of generated surfaces

- Choose **Make generated surfaces editable**, then click a completed road segment. Its surfaces become independent areas and the editor opens automatically, without a material picker or an Apply changes step.
- Reshaping preserves each area's existing finish, including different pavement, sidewalk, bike and bus surfaces on the same segment.
- Adds **Edit another segment** and **Finish editing** controls. Right-click cancels a drag; right-click while idle leaves the editor.
- Keeps the editing session focused on converted areas and supports previously converted surfaces. Other roads continue generating automatically.
- Manual edits remain independent through rebuilds, rule changes, road changes, road removal and vanilla restoration. **Resume automatic surfaces** reactivates saved road rules while keeping independent areas.
- Roads still generating are left untouched with a message so conversion can be retried when ready.

## Junction, roundabout and highway fixes

- Improves automatic junction and roundabout filling using eligible finishes from connected roads, including highway-only junctions and mixed street/highway approaches.
- Updates connected junction approaches when road finishes change, including cases where the reference road itself has no custom finish.
- Repairs recoverable saved junction references that incorrectly reported **Saved surface is unavailable**, and rebuilds affected approaches automatically when a city loads. Saves stable asset names to improve recovery on later loads.
- Retains choices from genuinely missing surface packs and retries recovery when those assets return. The junction tool shows the saved asset name when known.
- Fixes triangular coverage gaps at acute highway merges, splits and angled junctions. Marked crosswalk cutoffs, lane protection, planted areas, saved junction finishes and independent manual areas retain their existing behavior.

## After updating

- Restart with the updated build and reload the city. Save the city to retain its profiles, chosen setup and recovered junction identities.
- Use **Rebuild applied surfaces** once to apply the junction and highway coverage improvements to existing automatic surfaces. Independent manual areas are kept.
- Save General-rule edits, then choose **Apply city rules** when ready. Segment presets retain their preference for updating roads already using that preset.
- If a saved surface pack is actually missing, reinstall it or choose a replacement with the junction tool.
- To edit an entire junction or roundabout, convert its approach segments using **Edit another segment**. Converted areas still need Road Surface Filler and their surface assets enabled. Automatic painting resumes only through **Resume automatic surfaces**.
- Version 0.5.39 is a GitHub source update; it has not been published to PDX Mods. The local build and 20 focused profile/data checks passed. Native city save/load and vertex editing still need in-game confirmation.

# Version 0.5.35

Includes the local updates from versions 0.5.29 through 0.5.35.

## Consistent pavement choices

- Offers **Original finish**, **Use road pavement** and custom surfaces for sidewalks, bike lanes and bus lanes in both General rules and Segment rules.
- Original finish preserves the native finish for that target. Use road pavement follows the road pavement actually chosen for the segment, including its segment-rule override and matching General-rule priority.
- Keeps Use road pavement linked to the road choice, so changing the pavement also changes linked targets. Linked targets use the road's material-selection seed to match the same selected finish.
- Saves pavement links with city-applied rules and segment records. Links are retained through preset updates, native road splits/replacements and the shoulder tools. Existing saved component layouts remain readable.
- Keeps shoulder filling beneath a checked Bus lanes picker. Choosing Use road pavement or Original finish disables that rule's separate bus shoulder filling.

## Other included local updates

- Combines segment lane handling and surface selection, simplifies General-rule lane controls and preserves older inherited preset behavior.
- Adds direct junction cycling and automatic restoration with right-click exit, replacing the legacy selected-road controls.
- Repairs recoverable saved junction references, saves stable source names and automatically rebuilds affected approaches.
- Improves automatic junction/roundabout material selection and connected-road refreshes, and fixes triangular gaps at oblique highway mouths while retaining crosswalk boundaries.
- Adds surface thumbnail previews and filters choices to assets that support rendering over roads.

## After updating

- Choose each target's finish in its surface picker. Save General-rule edits, then **Apply city rules** when ready. Segment presets retain their update-placed-roads preference.
- Load and save the city to retain recovered junction identities. Use **Rebuild applied surfaces** once for the earlier automatic-junction and geometry improvements.
- These changes still need confirmation in game before publication.

# Version 0.5.34

Includes the local updates from versions 0.5.29 through 0.5.34.

## Combined lane controls

- Moves **Fill paved shoulders with bus-lane surface** directly beneath the Bus lanes surface picker in both General rules and Segment rules. It appears only when Bus lanes is checked.
- Removes the separate Bike lane handling and Bus lane handling menus from Segment rules.
- Adds **Use road pavement** to each segment lane's surface picker, alongside **Original finish** and custom surfaces. It uses the **Road pavement surface selected in that same segment rule**, sharing its material and preview. If pavement is inherited, it follows that segment's existing pavement choice. Original finish keeps the native lane surface.
- Unchecking a segment lane removes its local surface and handling override so it follows existing city lane settings. Selecting a finish controls that lane directly.
- Preserves older presets with inherited lane handling or inherited materials when they are opened and saved without changing those choices. Their current saved behavior is identified in the combined picker.
- Keeps shoulder filling associated with the chosen bus finish. Choosing Original finish, Use road pavement, or unchecking Bus lanes turns shoulder filling off. Older presets can retain an inherited bus material for shoulder filling.

## Other included local updates

- Adds direct junction cycling and automatic restoration, removes the legacy road-selection controls and simplifies General-rule lane selection.
- Repairs recoverable saved junction references, saves stable source names and rebuilds affected approaches automatically. Missing-pack choices are retained for recovery.
- Improves automatic junction and roundabout material selection and updates their connected approaches.
- Corrects triangular gaps at oblique highway mouths while retaining crosswalk cutoffs.
- Adds surface thumbnail previews and filters choices to assets that support rendering over roads.

## After updating

- Use each lane's surface picker to choose its finish. In Segment rules, leave a lane unchecked to inherit existing settings.
- Save General-rule edits and choose **Apply city rules** when ready to update roads. Segment presets retain their update-placed-roads preference.
- Load and save the city to retain recovered junction identities. Use **Rebuild applied surfaces** once for the earlier automatic-junction and geometry improvements.
- These changes still need confirmation in game before publication.

# Version 0.5.33

Includes the local updates from versions 0.5.29 through 0.5.33.

## Simpler junction tools

- Removes the legacy nearest/other-road-end selector, Use selected road button and Info-tool instructions from the junction controls.
- Keeps direct click-to-cycle targeting and adds direct **Restore automatic** targeting. Choose the mode, then click junctions or roundabouts; the tool stays active until right-click or Exit junction tool.
- Allows switching between cycling and automatic restoration from the active tool panel. Restoring an already automatic junction does not rebuild it unnecessarily.

## Simpler general rules

- Removes the separate bike-lane and bus-lane switches from General rules. Checking a lane target enables its surface selection and preserves the lane separately from road pavement.
- Includes **Original finish** as the default choice for every selected target, including roads and sidewalks. Choose a custom surface to replace it.
- Makes explicit Original finish assignments respect road-type/category priority and preserve the native finish when equally specific rules overlap.
- Represents older lane-protection settings in the new editor without enabling dormant custom lane finishes. Existing rules and applied city snapshots keep their stored settings until edited and applied.
- Requires a custom bus-lane finish for paved shoulder filling. Choosing Original finish for bus lanes turns that rule's shoulder filling off.

## Other included local updates

- Repairs recoverable saved junction asset references, saves stable surface names and rebuilds affected approaches automatically. Missing-pack choices are retained and retried when their assets return.
- Improves automatic junction and roundabout selection from connected road finishes and refreshes directly connected approaches when assignments change.
- Corrects triangular coverage gaps at oblique highway mouths while retaining marked crosswalk cutoffs.
- Adds surface thumbnail previews and filters choices to surfaces that support rendering over roads.

## After updating

- Load and save the city to retain recovered junction identities. Use **Rebuild applied surfaces** once for existing automatic junctions and the earlier geometry improvements.
- Edit General rules using their checkboxes and finish pickers, then choose **Apply city rules** when ready to update existing roads.
- These changes still need confirmation in game before publication.

# Version 0.5.32

Includes the local updates from versions 0.5.29 through 0.5.32.

## Saved junction surface recovery

- Fixes custom junction surface and road-prefab references missing the game's prefab remapping during saving/loading. A junction can otherwise report its saved surface as unavailable even when that same surface works on connected roads.
- Recovers older junction records from their native prefab reference and rebuilds their connected approaches automatically. This migration runs when any user's city loads, including saved roundabouts and highway junctions.
- Saves the original surface name alongside junction choices so recreated prefab entities can be resolved to the same installed asset. The existing saved component format remains readable.
- Retains choices whose surface pack is genuinely missing, retries recovery as assets become available and refreshes the affected approaches when the same asset returns. The junction tool displays the saved asset name when known.
- Preserves the selected finish, bus-lane mode, independent manual areas and existing geometric cutoffs. Repairs do not cycle to a different finish.

## Other included local updates

- Improves automatic junction and roundabout selection from connected road finishes and refreshes directly connected approaches when assignments change.
- Corrects triangular coverage gaps at oblique highway mouths while retaining marked crosswalk cutoffs.
- Adds surface thumbnail previews and filters choices to surfaces that support rendering over roads.
- Adds direct click-to-cycle junction targeting with highlighting, instructions and right-click exit.

## After updating

- Load the city to migrate recoverable saved junction choices automatically, then save it to retain their stable asset names. The junction cycler remains available for changing a finish.
- If the junction tool reports a missing saved asset, restore its surface pack or deliberately choose a replacement. An old record with no surviving asset identity cannot identify a removed pack automatically.
- Use **Rebuild applied surfaces** once for existing automatic junctions and the earlier geometry improvements. These changes still need confirmation in game.

# Version 0.5.31

Includes the local updates from versions 0.5.29 through 0.5.31.

## Automatic junction and roundabout filling

- Fixes automatic pavement selection getting stuck on an unstyled reference road. When the reference has no finish, the junction can use an eligible connected segment's actual preset or applied city finish, using the same road/bus surface choices as the junction cycler.
- Mixed roundabouts prefer an available street finish, then fall back to an available highway finish. Highway-only junctions use a highway reference and retain highway classification.
- Refreshes directly connected junction approaches when a styled road is generated or updated, including ring sectors owned by unstyled approaches. Their road bodies keep their own assignments.
- Tracks neighboring segment finishes so preset changes refresh shared automatic pavement even when road geometry is unchanged. Shared junction interiors use an owner with applied placement rules.
- Retains saved cycler choices, manual-edit exclusions, lane protection, planted areas and road/elevation filters.

## Other included local updates

- Corrects triangular gaps caused by an oblique road mouth at highway merges and splits. Marked crosswalk cutoffs retain their marking-based boundary.
- Adds surface thumbnail previews to surface pickers, with images loaded as choices come into view.
- Filters out surfaces without road-rendering support while retaining older saved choices.
- Adds direct click-to-cycle junction targeting with highlighting, instructions and right-click exit.

## After updating

- Use **Rebuild applied surfaces** once to regenerate existing automatic junctions and roundabouts. The cycler remains available for choosing a different finish; it should no longer be needed to initialize otherwise eligible junctions.

# Version 0.5.30

Includes the local updates from versions 0.5.29 and 0.5.30.

## Angled junction coverage

- Corrects the boundary between a road body and its junction where there is no marked crosswalk. The cut now follows both endpoints of the native road mouth instead of assuming it is perpendicular to the road's direction.
- Prevents that cut from removing triangular sections at oblique highway merges, splits and other acute junctions. The change uses each junction's geometry and applies across cities.
- Actual crosswalks retain their marking-based boundary. Lane protections, planted areas and saved junction finishes keep their existing rules.

## Surface previews

- Adds small surface preview images beside choices and the selected finish in general rules, segment presets, bus-lane shoulder fill and the surface editor.
- Uses each installed asset's game thumbnail or provider icon, so compatible custom packs can supply their own previews. Missing or failed images show **No image** while keeping the surface selectable.
- Preview URLs are cached with the surface catalog. Dropdown images load as they come into view, and image loading does not apply or rebuild any roads.

## Surface selection and junction cycler

- Filters out installed surfaces that do not support rendering over roads, while retaining existing saved choices with an explanatory warning.
- **Cycle junction surface** activates a tool for clicking junctions and roundabouts directly. Each click cycles once, the target is highlighted, and the tool remains active until you right-click or choose **Exit junction tool**.

## After updating

- Use **Rebuild applied surfaces** to regenerate existing automatic areas with the corrected junction boundary. Independent manually edited areas remain independent.

# Version 0.5.29

## Surface selection

- Surface pickers now list only installed surfaces whose renderer supports roads. Terrain-only surfaces and surfaces without an area renderer are filtered out automatically, including custom surface packs.
- Existing saved surface assignments are retained. When editing a rule that uses a filtered surface, a warning explains why it cannot show over roads and offers compatible replacements.
- The same filtered list is used for general rules, segment presets, bus-lane shoulder fill and the surface editor.

## Junction cycler

- **Cycle junction surface** now activates a tool. Point at a junction or roundabout in the city and left-click to cycle its finish; selecting an approach road first is no longer required.
- The target is highlighted, with an instruction panel showing the current finish and any feedback. Each click advances once, and the tool stays active so you can cycle repeatedly or move to another junction.
- Right-click or choose **Exit junction tool** to stop cycling. Saved junction choices, bus-lane options, protected bike surfaces and connected-road updates retain their existing behavior.

## After updating

- Use **Refresh surfaces** if a surface pack was loaded or changed while the city was open. Surface filtering does not require rebuilding existing areas.

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
