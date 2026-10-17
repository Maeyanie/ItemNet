# ItemNet

ItemNet writes better clothing and armour descriptions for SkyrimNet.

SkyrimNet's OmniSight describes each item from screenshots of the item on its own. It also strips
out any body mesh. Without a body for context, vision models often misread what they see: a brief
floating in mid-air gets described as a "chest bandeau", and pieces of an outfit come out as
unrelated objects.

ItemNet renders each item **on a mannequin body** from four sides, using your own load order:
- the race's body, hands, feet and head
- your BodySlide meshes and skin textures
- the slot hiding the game itself applies

It then has a vision model describe the item, lets you review and edit the result, and saves it into
SkyrimNet's database as a locked entry.

> **Status:** early development. The workflow works end to end on one setup; expect rough edges.

## What it does
- Lists every playable armour and clothing item in the load order, searchable, showing where each
  gender's current description comes from.
- Renders front, right, back and left views on a textured or clay mannequin, with a BodySlide weight
  slider.
- Shows SkyrimNet's current name, description and images for comparison.
- Describes with any OpenAI-compatible endpoint, such as OpenRouter, NanoGPT, NVIDIA NIM or a local
  server. Providers and vision models can be imported from SkyrimNet's own config.
- Saves to:
  - its own catalog, keyed by plugin and FormID, so it survives load-order changes
  - the chosen SkyrimNet playthrough database, as a locked row that OmniSight won't overwrite

  The database is backed up before the first write of each session.

## Requirements
- Windows 10 or 11 with a Direct3D 11 GPU.
- Skyrim Special Edition / Anniversary Edition with SkyrimNet. Tested with SkyrimNet beta 27, schema
  version 30; ItemNet refuses to write to a schema it doesn't know.
- An API key for a vision-capable model, unless you run one locally.
- Building from source needs the .NET 10 SDK. Released builds are self-contained and need nothing
  installed.

## Building
```
dotnet build ItemNet.slnx -c Release
```
For a single self-contained executable:
```
dotnet publish src/ItemNet.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
The output is `src/ItemNet.App/bin/Release/net10.0-windows/win-x64/publish/ItemNet.exe`.

## Running from Mod Organizer 2
1. In MO2, open **Modify Executables** (the gear icon next to Run), add `ItemNet.exe` and give it a
   title.
2. Pick it from the executables list and click **Run**.

Launched this way, ItemNet sees Data and plugins.txt through MO2's virtual file system, exactly as
the game does for the active profile. Files it writes go to MO2's overwrite folder, the same as
SkyrimNet's own files:
- OmniSight images, under `SKSE\Plugins\SkyrimNet\screenshots\omnisight`
- database backups, beside the database

ItemNet can also run outside MO2. Choose **Settings → Data source → MO2 instance and profile** and
it reads the instance's mod list, profile and overwrite folder directly.

You can save while Skyrim is running. SkyrimNet picks up changes the next time you change cells or
load a save.

### Where things are kept
- Settings, including API keys: `%LOCALAPPDATA%\ItemNet\settings.json`
- Catalog: `%LOCALAPPDATA%\ItemNet\ItemNet.db` plus `images\`. The folder can be changed in
  Settings.

## Prompt
The describe prompt is an editable [Scriban](https://github.com/scriban/scriban) template
(**Settings → Prompt**). The reply must be a JSON object with `suggestedName` and `description`,
the same shape SkyrimNet uses.

## Credits
- [Mutagen](https://github.com/Mutagen-Modding/Mutagen): plugins, load order, archives (GPL-3.0)
- [NiflySharp](https://github.com/ousnius/NiflySharp): NIF reading (GPL-3.0)
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows): Direct3D 11 (MIT)
- [Scriban](https://github.com/scriban/scriban), [YamlDotNet](https://github.com/aaubry/YamlDotNet),
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), Microsoft.Data.Sqlite

## License
GPL-3.0; see [LICENSE](LICENSE).
