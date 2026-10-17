# Road Surface Filler: Visual Studio and publishing

Existing PDX Mods listing: [Road Surface Filler](https://mods.paradoxplaza.com/mods/163448/Windows), **ModId 163448**. Use **PublishNewVersion** for code releases and **UpdatePublishedConfiguration** for listing changes, including visibility.

## Open the existing project

Open **RoadSurfaceFiller.sln** in Visual Studio. It loads the current **SurfaceNetworkFilter.csproj**, displayed as **Road Surface Filler** in Solution Explorer.

The project already uses the official CS2 template's `Mod.props` and `Mod.targets`, game references, ECS source generators and post-processor. Its existing code, UI, save identities and settings stay in this project. Creating a second mod project is unnecessary.

The publishing profiles were copied from the installed **Colossal Order Mod Template 1.0.0**. Their deployment folder is set to `artifacts\publish\content` so publishing content is prepared separately from the installed local mod.

Keep `DeployDir` without a trailing backslash. The official publisher command quotes this path; a final backslash escapes its closing quote on Windows and causes the publisher to treat `-v` as part of the folder name.

## Review the mod page

Edit **Properties/PublishConfiguration.xml**. It includes:

- Display name **Road Surface Filler** and a description of the current features.
- The supplied RSF image at **Properties/Thumbnail.png**.
- Five screenshots under **Properties/Screenshots**: the two UI screenshots followed by the three city screenshots in the supplied order.
- Mod version **0.5.36** (published) and installed game version **1.6.2f1**.
- **Public** visibility for the released listing.
- **ModId 163448** for the existing Road Surface Filler listing.

For each release, review the description, compatibility version, included screenshots and **Code Mod** tag, and include any source, support or required-mod links. Optional surface packs can be linked in the description. Keep AccessLevel set to **Public** for the released listing.

Every referenced thumbnail and screenshot is below 2 MB to fit the publisher's 2.1 MB image limit. The three city screenshots use JPEG upload copies at their original dimensions; their supplied PNG originals are preserved in local backups.

## Publish from Visual Studio

1. Right-click **Road Surface Filler** in Solution Explorer and choose **Publish**.
2. Select an existing profile under **Properties/PublishProfiles**:
   - **PublishNewMod** creates a separate new PDX Mods entry; the current project already has its listing.
   - **PublishNewVersion** uploads a new release to an existing entry.
   - **UpdatePublishedConfiguration** updates an existing entry's listing information.
3. Review **Properties/PublishConfiguration.xml** and the selected profile before starting publication.
4. Complete any account sign-in requested by the official publisher on your computer.
5. Keep **ModId 163448** in the configuration so update profiles target the existing listing.

Selecting **Publish** starts an upload. A normal **Build** does not upload anything.

Version **0.5.36** was uploaded to **ModId 163448** with the official publisher's **NewVersion** command. The publisher confirmed **New mod version published** and **Publishing new version updating process finished successfully**, with exit code **0**. The approved cumulative changelog includes **0.5.29 through 0.5.36**. Local publication evidence is saved in `artifacts/v0.5.36-publication.json` and `.tmp/v0.5.36-publish.log`.

For subscription checks after publication, keep only one active copy of the mod: the local development copy and subscribed copy share an identity.

## Build without installing or publishing

From this folder:

```powershell
dotnet build RoadSurfaceFiller.sln -c Release --no-restore -p:SurfaceNetworkFilterStageOnly=true
```

This uses the official post-processor and writes the runtime files to `artifacts/v0.5.36`. Normal builds deploy to the local game mod folder, so close the game before using normal deployment. Disable the PDX Mods copy in your active playset before using a local development copy to avoid loading two copies.

For 0.5.36, activate Make generated surfaces editable and click a completed road with several different surface finishes. Confirm automatic editor activation without material selection or Apply changes, then move/insert vertices, including a shared point between different materials. Every affected converted area must retain its own finish. Check another-segment targeting, pending-generation retry, right-click cancellation/exit, unrelated area protection and automatic generation on other roads. Reopen older converted areas, save/reload, rebuild, change/remove/split the converted road and confirm manual edits remain. Resume automatic surfaces must retain independent areas. Gameplay checks have not been run automatically.

For 0.5.35, choose Original finish, Use road pavement and custom finishes for sidewalks, bike lanes and bus lanes in both editors. Apply a General rule and a segment preset with different road finishes, confirm linked targets match each segment's chosen pavement, then change the pavement and update it. Include overlapping General rules, multiple road-material choices, preset update preferences, copying rules, shoulder-tool changes, native splits/replacements and save/reload. Confirm physical boundaries and independent manual areas are retained. Gameplay checks have not been run automatically.

For 0.5.34, confirm shoulder filling appears only beneath a checked Bus lanes picker in both rule editors. In Segment rules, switch between Original finish, Use road pavement and custom finishes, and uncheck lanes to restore inheritance. Open older presets with nullable modes, mode-only lane protection and inherited shoulder materials, then save without changing those lane choices and confirm their behavior is retained. Check preset updates, Replace and saving/reloading. The separate lane-handling dropdowns should be absent. Gameplay checks have not been run automatically.

For 0.5.33, check direct junction cycling and automatic restoration on ordinary, highway and bus-lane junctions. Switch modes, click multiple junctions and right-click to exit. Confirm the old selected-road controls are absent. In General rules, check/uncheck lane targets, choose Original finish or custom surfaces and inspect older rules with protected lanes or dormant lane assignments. Confirm Original finish works for roads and sidewalks, takes precedence over broader assignments and wins ties. Check shoulder filling with custom and original bus finishes, then save and Apply city rules. Segment presets must retain their lane-handling controls. Gameplay checks have not been run automatically.

For 0.5.32, load a copy of a city with saved junction choices that previously showed **Saved surface is unavailable**. Recoverable records should fill without cycling; the log identifies recovered source assets and queued approaches. Save and reload, confirm the same finish and bus-lane mode, and inspect a deliberately unavailable surface pack followed by its restoration. Missing choices must be retained and returning assets should rebuild affected approaches automatically. Include independently edited roads and native road replacements. Gameplay checks have not been run automatically.

For 0.5.31, rebuild applied surfaces and check roundabouts/junctions without a saved cycler override. Include highway-only and mixed approaches, an unstyled widest reference, a single styled approach with unstyled neighbors, changing/removing a preset and saved cycler overrides. Confirm ring sectors appear automatically while unstyled road bodies and manual areas retain their assignments. Confirm the generation queue settles after native areas finish materializing. Gameplay checks have not been run automatically.

For 0.5.30, rebuild applied surfaces in a copy of the city and inspect acute highway merges and splits from both directions, including curved and graded approaches. Check that road bodies meet the junction at the native seam without triangular gaps, and that marked crossings, protected lanes and planted areas retain their coverage. Inspect thumbnails in all four surface pickers, including custom packs, search results, selected finishes and a missing image. Gameplay checks have not been run automatically.

The 0.5.29 local build adds road-compatible surface filtering and direct junction cycling. In a city, confirm terrain-only surfaces are omitted, compatible custom packs remain listed and existing filtered assignments are retained with a warning. Activate Cycle junction surface without selecting a road, click ordinary and highway junctions and roundabouts repeatedly, then right-click to leave the tool. Check that a distant approach click does not change its junction, and that bus-lane choices, saved finishes and protected bike lanes retain their behavior. These gameplay checks have not been run automatically.

For the junction optimization, check a copy of a real city before uploading the release. Use **Rebuild applied surfaces** to replace existing fragmented overlays, then compare junction coverage, managed area counts, generation times and **planned areas before/after merge** in the mod log. Include bike/bus separation, shoulder fill, differing segment finishes, planted junctions, sloped roads, street/mixed roundabouts and highway-only roundabouts. Check highway category and exact-prefab rules and optional highway sidewalks. Compilation alone does not establish gameplay correctness or a measured performance improvement.

Official overview: https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding

For 0.5.28, use a copy of a city to check Tools > Make generated surfaces editable. Convert a straight segment and all approaches of a junction/roundabout, edit an area boundary with Open surface editor, then rebuild, change rules, split/upgrade/remove a converted road and save/reload. Confirm the edits remain, automatic overlays are not regenerated on converted roads, and neighboring automatic roads still update. Check both vanilla restoration actions preserve independent areas. Resume automatic surfaces and confirm saved rules return while independent areas remain. In-progress roads should be skipped with a clear message. The areas still depend on the mod and their surface assets; gameplay has not been verified by the build.
