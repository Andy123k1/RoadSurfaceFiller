# Road Surface Filler: Visual Studio and publishing

## Open the existing project

Open **RoadSurfaceFiller.sln** in Visual Studio. It loads the current **SurfaceNetworkFilter.csproj**, displayed as **Road Surface Filler** in Solution Explorer.

The project already uses the official CS2 template's `Mod.props` and `Mod.targets`, game references, ECS source generators and post-processor. Its existing code, UI, save identities and settings stay in this project. Creating a second mod project is unnecessary.

The publishing profiles were copied from the installed **Colossal Order Mod Template 1.0.0**. Their deployment folder is set to `artifacts\publish\content` so publishing content is prepared separately from the installed local mod.

Keep `DeployDir` without a trailing backslash. The official publisher command quotes this path; a final backslash escapes its closing quote on Windows and causes the publisher to treat `-v` as part of the folder name.

## Review the mod page

Edit **Properties/PublishConfiguration.xml**. It includes:

- Display name **Road Surface Filler** and a draft description of the current features.
- The supplied RSF image at **Properties/Thumbnail.png**.
- Four supplied screenshots under **Properties/Screenshots**, in the order selected for the mod page.
- Mod version **0.5.23** and installed game version **1.6.2f1**.
- **Private** visibility for an initial listing review.
- An empty ModId for a first publication.

Before releasing, review the description, compatibility version, included screenshots and **Code Mod** tag, and include any source, support or required-mod links. Optional surface packs can be linked in the description. Change AccessLevel to **Public** when the listing is ready for players.

Every referenced thumbnail and screenshot is below 2 MB to fit the publisher's 2.1 MB image limit. The two larger screenshots use JPEG upload copies at their original dimensions; their original PNG files remain in the project.

## Publish from Visual Studio

1. Right-click **Road Surface Filler** in Solution Explorer and choose **Publish**.
2. Select an existing profile under **Properties/PublishProfiles**:
   - **PublishNewMod** creates the initial PDX Mods entry.
   - **PublishNewVersion** uploads a new release to an existing entry.
   - **UpdatePublishedConfiguration** updates an existing entry's listing information.
3. Review **Properties/PublishConfiguration.xml** and the selected profile before starting publication.
4. Complete any account sign-in requested by the official publisher on your computer.
5. Retain the ModId returned by the first publication in the configuration. Use that ID with the update profiles for later releases.

Selecting **Publish** starts an upload. A normal **Build** does not upload anything. The project setup was prepared locally without invoking publication.

For subscription checks after publication, keep only one active copy of the mod: the local development copy and subscribed copy share an identity.

## Build without installing or publishing

From this folder:

```powershell
dotnet build RoadSurfaceFiller.sln -c Release --no-restore -p:SurfaceNetworkFilterStageOnly=true
```

This uses the official post-processor and writes the runtime files to `artifacts/v0.5.23`. Normal builds deploy to the local game mod folder, so close the game before using normal deployment.

Official overview: https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding
