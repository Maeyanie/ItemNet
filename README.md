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

ItemNet has been tested on one large load order (Requiem, about 670 plugins). If something goes
wrong on yours, please [open an issue](https://github.com/Maeyanie/ItemNet/issues) and attach
`ItemNet.log` (see [Where things are kept](#where-things-are-kept)).

## What it does
- Lists every playable armour and clothing item in the load order, searchable, showing where each
  gender's current description comes from. Names are in the game's language (`sLanguage`).
- Renders front, right, back and left views on a textured or clay mannequin, or none, with a
  BodySlide weight slider.
- Shows SkyrimNet's current name, description and images for comparison.
- Describes with any OpenAI-compatible endpoint, such as OpenRouter, NanoGPT, NVIDIA NIM or a local
  server. Providers and vision models can be imported from SkyrimNet's own config. Failed requests
  are retried with backoff.
- Describes in batches, a few requests at a time, saving drafts to review or writing straight to
  SkyrimNet. Identical items (the same meshes and textures, such as every enchanted copy of an
  armour) can share one result.
- Saves to:
  - its own catalog, keyed by plugin and FormID, so it survives load-order changes
  - the chosen SkyrimNet playthrough database, as a locked row that OmniSight won't overwrite

  The database is backed up before the first write of each session.
- Syncs the catalog into a SkyrimNet database after a load-order change, or into a new playthrough's.

## Requirements
- Windows 10 or 11 with a Direct3D 11 GPU.
- Skyrim Special Edition / Anniversary Edition with SkyrimNet. Tested with SkyrimNet beta 27 and
  beta 28 (database schema versions 30 and 31); ItemNet refuses to write to a schema it doesn't know.
- An API key for a vision-capable model, unless you run one locally.

## Installing
Download `ItemNet.exe` from the [Releases](https://github.com/Maeyanie/ItemNet/releases) page. It's
self-contained: there's nothing else to install, and it can live anywhere.

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

On first run, open **Settings** and import SkyrimNet's providers and vision models, or add your own.

You can save while Skyrim is running. SkyrimNet picks up changes the next time you change cells or
load a save.

## Using it

### One item at a time
Pick an item, check the render, click **Describe**, edit the name and description if you like, and
**Save**. **Save & next** (Ctrl+Enter, even from inside the description box) saves and moves to the
next item in the list. Click any view, ItemNet's or SkyrimNet's, to see it full size.

### Batches
**Batch…** works on the items you've selected in the list (Shift/Ctrl+click), or on everything the
list shows when you haven't selected several. It renders with the main window's mannequin, weight
and size settings. Options:
- **Gender:** female, male or both.
- **At once:** how many describe requests run at the same time. Renders always run one at a time.
- **Skip items ItemNet already did:** leave this on and a stopped batch picks up where it left off.
  **Retry failed** reruns the failures.
- **Skip hand-edited SkyrimNet entries.**
- **Give each result to identical un-described items too:** items with the same meshes and textures
  get the same description, and aren't described separately. This searches the whole item list, not
  just the batch. Items ItemNet already did, or that you hand-edited in SkyrimNet, are left alone.
- **Save drafts for review** (the default) or **Write to SkyrimNet as each finishes**. Hand-edited
  SkyrimNet entries are never overwritten by a batch; they get a draft instead.

The window shows progress, the time left, and the cost so far and for the rest, when the provider
reports it. To review drafts, set the list's filter to **Drafts** and go through them with
**Save & next** or **Discard draft**.

### Copying a description
Select an item in the list and press Ctrl+C. Then select one or more other items and press Ctrl+V to
give them its name and description, with their own renders. Both are also on the list's right-click
menu.

### After a load-order change
SkyrimNet keys its rows by the FormID the running game assigns, which changes when plugins are added,
removed or reordered. **Sync…** writes the catalog into the selected database at each item's current
ID, and shows what it will do before doing anything:
- it removes rows ItemNet wrote at IDs that now belong to a different item, or to none
- it moves your hand-edited SkyrimNet descriptions back to their items, matched by SkyrimNet's
  content hash
- it never overwrites a hand-edited description

The same button fills a new playthrough's database from the catalog.

### What ItemNet writes
Each row it writes is locked, so OmniSight won't replace it. Its `description_model` is
`ItemNet:<model>`, with `+edited` if you changed the text and `+copied` if it came from another item.
ItemNet asks before replacing a description you edited in SkyrimNet's dashboard (`user-edited`).

### Where things are kept
- Settings, including API keys: `%LOCALAPPDATA%\ItemNet\settings.json`
- Catalog: `%LOCALAPPDATA%\ItemNet\ItemNet.db` plus `images\`. The folder can be changed in
  Settings.
- Log: `%LOCALAPPDATA%\ItemNet\ItemNet.log`, with the previous session's in `ItemNet.old.log`.

## Prompt
The describe prompt is an editable [Scriban](https://github.com/scriban/scriban) template
(**Settings → Prompt**). The reply must be a JSON object with `suggestedName` and `description`,
the same shape SkyrimNet uses.

## Known limitations
- Armour and clothing only. Weapons and shields aren't rendered yet.
- The mannequin stands in the skeleton's rest pose, arms out, which can hide parts of the side views.
  Physics parts (SMP, CBPC) render in their rest shape too.
- While ItemNet is open it keeps the load order's plugins and archives open. Outside MO2, that stops
  MO2 from updating or removing those mods until you close ItemNet.
- Plugins with no strings in the game's language fall back to their English strings.

## Building
Building from source needs the .NET 10 SDK.
```
dotnet build ItemNet.slnx -c Release
```
For a single self-contained executable:
```
dotnet publish src/ItemNet.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```
The output is `src/ItemNet.App/bin/Release/net10.0-windows/win-x64/publish/ItemNet.exe`.

## Use of AI
This was mostly written by Claude. The concept, design work, and extensive testing were done by a human.

## Credits
- [Mutagen](https://github.com/Mutagen-Modding/Mutagen): plugins, load order, archives (GPL-3.0)
- [NiflySharp](https://github.com/ousnius/NiflySharp): NIF reading (GPL-3.0)
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows): Direct3D 11 (MIT)
- [Scriban](https://github.com/scriban/scriban), [YamlDotNet](https://github.com/aaubry/YamlDotNet),
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), Microsoft.Data.Sqlite

## License
GPL-3.0; see [LICENSE](LICENSE).
