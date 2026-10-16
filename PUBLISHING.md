# Road Surface Filler: Visual Studio and publishing

Existing PDX Mods listing: [Road Surface Filler](https://mods.paradoxplaza.com/mods/163448/Windows), **ModId 163448**. Use **PublishNewVersion** for code releases and **UpdatePublishedConfiguration** for listing changes, including visibility.

## Open the existing project

Open **RoadSurfaceFiller.sln** in Visual Studio. It loads the current **SurfaceNetworkFilter.csproj**, displayed as **Road Surface Filler** in Solution Explorer.

The project already uses the official CS2 template's `Mod.props` and `Mod.targets`, game references, ECS source generators and post-processor. Its existing code, UI, save identities and settings stay in this project. Creating a second mod project is unnecessary.

The publishing profiles were copied from the installed **Colossal Order Mod Template 1.0.0**. Their deployment folder is set to `artifacts\publish\content` so publishing content is prepared separately from the installed local mod.

Keep `DeployDir` without a trailing backslash. The official publisher command quotes this path; a final backslash escapes its closing quote on Windows and causes the publisher to treat `-v` as part of the folder name.

## Review the mod page

Edit **Properties/PublishConfiguration.xml**. It includes:

- Display name **Road Surface Filler** and a draft description of the current features.
- The supplied RSF image at **Properties/Thumbnail.png**.
- Five screenshots under **Properties/Screenshots**: the two UI screenshots followed by the three city screenshots in the supplied order.
- Mod version **0.5.26** and installed game version **1.6.2f1**.
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

For subscription checks after publication, keep only one active copy of the mod: the local development copy and subscribed copy share an identity.

## Build without installing or publishing

From this folder:

```powershell
dotnet build RoadSurfaceFiller.sln -c Release --no-restore -p:SurfaceNetworkFilterStageOnly=true
```

This uses the official post-processor and writes the runtime files to `artifacts/v0.5.26`. Normal builds deploy to the local game mod folder, so close the game before using normal deployment. Disable the PDX Mods copy in your active playset before using a local development copy to avoid loading two copies.

For the 0.5.26 junction optimization, check a copy of a real city before uploading the release. Use **Rebuild applied surfaces** to replace existing fragmented overlays, then compare junction coverage, managed area counts, generation times and **planned areas before/after merge** in the mod log. Include bike/bus separation, shoulder fill, differing segment finishes, planted junctions, sloped roads and roundabouts. Compilation alone does not establish gameplay correctness or a measured performance improvement.

Official overview: https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding
