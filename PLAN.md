# ItemNet — plan

A Windows tool, run through MO2, that reads the load order, lists wearable items, renders each one
**on a mannequin body** from four sides, has a vision model describe it, lets you review and edit
the result, and writes it into SkyrimNet so NPC prompts get an accurate description.

Status: Phase 0 done (2026-10-17, see §4). Phase 1 GUI (`src/ItemNet.App`) is in progress. Working
in Mode B on profile 5:
- item list with search and status filter (7,463 items)
- 4-view render with female/male, textured/clay/none mannequin and a weight slider
- SkyrimNet's current entry with its stored images
- settings with Providers.yaml and preset import
- the save path: catalog, then a locked OmniSight row, with a one-time online backup

Not yet exercised: a real Describe call, Save from the GUI, Mode A under MO2's VFS, and
self-contained publishing.

---

## 1. What SkyrimNet does today (verified against beta 27 and the live DBs)

### How it renders
- OmniSight's `StandaloneItemRenderer` (in `SkyrimNet.dll`) is a small D3D11 renderer: it loads the
  item's NIF and DDS files (loose, then BSA), renders the mesh **on its own** with standard and PBR
  shaders, and produces 4 views per gender at 512×512 (`omnisight.capture.subjects.items` in
  `config/OmniSight.yaml`).
- It **deliberately skips body meshes** ("Skipping body mesh '{}'"). When a NIF contains one, it only
  adds a "mannequin warning" to the prompt (`has_mannequin_body`).
- That is the bug in a nutshell. `item_06000873_female_0.png` (Dark Seducer Armor) is a green bottom
  piece floating above two greaves. Using the user's own prompt
  (`skyrimnet-describe-prompt-and-reply.txt`), nemotron VL called the bottom a "chest bandeau" and
  said it "exposes the midriff and shoulders".

### Where descriptions are stored
- The descriptions are in a **per-playthrough** SQLite DB:
  `SKSE/Plugins/SkyrimNet/data/SkyrimNet-<id>.db`. 8 exist; the current one
  (`…1788253036966-543566.db`, schema migration 30) has 510 item rows covering 418 distinct items.
  Older DBs hold 0–197 rows each.
- Table `omnisight_screenshots`, `UNIQUE(subject_type, subject_identifier)`:

  | column | item rows contain |
  |---|---|
  | `subject_type` | `'item'` |
  | `subject_identifier` | `'<RUNTIME FORMID 8 hex>_<female\|male>'`, e.g. `06000873_female`, `FE0C0801_female` |
  | `subject_name` | game name |
  | `suggested_name` | AI display name (used as the item's name in prompts) |
  | `description` | AI description |
  | `description_model` | `''`, or `'user-edited'` for dashboard edits |
  | `description_locked` | 1 on user edits. SkyrimNet's only refresh path is `UPDATE … SET needs_refresh = 1 WHERE … AND description_locked = 0` |
  | `content_hash` | 16 hex, "hash of item meshes/textures", used for dedup (`getOmniSightScreenshotByContentHash`) |
  | `file_path` | JSON array of the 4 PNG paths (`…\screenshots/omnisight\item_<ID>_<gender>_<n>.png`) |
  | `image_width/height`, `capture_time_*`, `description_prompt='item'`, `is_valid`, `needs_refresh` | |

- The key is the **runtime FormID**, so it depends on load order. Row `FE0C0801_female` points at
  images named `item_FE0A3801_…`, which looks like a light-plugin index shift that was carried over
  by content-hash dedup.
- You already have 9 locked `user-edited` rows (Barkeeper Clothes, Novice Robes, SkyTitties
  variants, Steel Armor BDS, …). The tool must not overwrite these without asking.

### How prompts use the descriptions
- `submodules/character_bio/0410_equipment.prompt` tries OmniSight first
  (`get_item_name_for_wearer` / `get_item_description_for_wearer`, gender-aware, falling back to the
  other gender's row). Only when OmniSight has nothing does it use the content-library record
  (`get_item_description`).
- Content-library item records (`items/<form stem>.yaml`, keyed `form: "Plugin.esp|0x01396B"`,
  with `customName`, a description and `show_in_prompts`) don't depend on load order and can be
  shared as an `external/` layer. However, in the equipment list **they lose to OmniSight** unless
  OmniSight item capture is turned off.

**Conclusion:** v1 writes OmniSight rows, which is what SkyrimNet actually reads. The YAML layer
comes later as a shareable export.

---

## 2. Technology choice

**.NET 10 + WPF**, published self-contained as a single exe. Qt6 loses mainly because there is no
Mutagen equivalent in C++: plugin parsing, load order, light/ESL indexing, override resolution, and
localized strings would all have to be written by hand.

| Need | Library | Notes |
|---|---|---|
| Plugins, load order, link cache, strings | `Mutagen.Bethesda.Skyrim` 0.54.4 | GPL-3.0. 0.54 needs net9+, which is fine on net10 |
| BSA reading | `Mutagen.Bethesda.Archives` (bundled with Mutagen) | SSE v105 / LZ4 |
| NIF geometry, skin, shaders | `Nifly` 1.1.0 (ousnius/NiflySharp) | Pure C# rewrite of nifly, GPL-3.0. **Not** the NuGet `niflysharp`, which is a SWIG wrapper with a native DLL |
| GPU | `Vortice.Direct3D11` / `.DXGI` / `.D3DCompiler` 3.8.x | MIT. Offscreen render target with readback, so no swapchain/WPF interop |
| DDS | Our own header parse; BCn uploaded straight to D3D11 | `Pfim` (MIT) only as a CPU fallback for odd formats |
| SQLite | `Microsoft.Data.Sqlite` | |
| Prompt templates | `Scriban` | Liquid-like; the user can edit them |
| MVVM | `CommunityToolkit.Mvvm` | |

- **NIF fallback:** if the C# `Nifly` falls short in spike 4 (skin partitions, odd block types), the
  fallback is the C++ nifly that Outfit Studio uses. Either use the SWIG `niflysharp` package or
  build our own thin C-ABI shim DLL with the installed MSVC. Spike 4 is the gate for this.
- **Licence:** Mutagen and Nifly are GPL-3.0, so ItemNet is GPL-3.0.
- **Runtime dependencies:** D3D11 and `d3dcompiler_47` are part of Windows 10/11. Everything else
  is bundled. No Python, Node or .NET runtime install needed.
- **Spooky's toolkit:** used only as dev tooling (ESP queries, NIF texture listing, BSA extraction,
  skills). It doesn't ship with ItemNet. It pins Mutagen 0.52; we use 0.54.4.

---

## 3. Components

### 3.1 Game file system
- **Mode A (default): launched from MO2.** usvfs merges `Data` and redirects `plugins.txt`, so
  `GameEnvironment` works as-is. This also works for Vortex and manual installs.
- **Mode B: point at an MO2 instance and profile directly.** The tool reads `modlist.txt`
  (priority and enabled state), `overwrite`, the profile's `plugins.txt`/`loadorder.txt` and
  profile INIs, and builds its own path→file index. It's faster (no usvfs on directory walks), and
  it lets the tool be tested headless without going through MO2. (The ItemAlternates patcher
  couldn't run standalone for exactly this reason.)
- Resolution follows the game's rules: loose files beat BSAs, and BSAs follow plugin order plus
  `sResourceArchiveList`. Paths are lowercase-normalised. The BSA index is cached per session.

### 3.2 Item index (Mutagen)
- Winning overrides of `ARMO` (apparel, jewelry, shields) first; `WEAP` second.
- Skip non-playable armour, race skins and items with no models.
- Group variants that share visuals (e.g. a `TemplateArmor` chain like "Iron Helmet of Archery")
  by a **visual key**: sorted model paths, alt textures and skin texture. One render and one
  description can then serve the whole group.
- Per item: name, EditorID, FormKey (`Plugin|LocalID`), current runtime FormID, slots → body areas,
  armour type, keywords, ARMA list, and the SkyrimNet status for each gender (none / auto / locked
  user edit / ItemNet).

### 3.3 Assembling the scene
For the chosen race (default NordRace) and gender:
1. ARMO → ARMAs whose race or additional races match. Use the gendered `WorldModel`, falling back
   to male as the game does.
2. Weight slider: if the ARMA enables it, load `_0` and `_1` and lerp the vertices. One weight
   value is shared with the body.
3. Apply ARMA alternate textures (3D-name → TXST) and the ARMA skin texture.
4. **Mannequin:** the race's skin ARMO (`Race.Skin`) gives torso, hands, feet and tail, from *your*
   load order. That means your BodySlide-built body, which is the shape the outfit was built for.
   Head comes from the race's default face head part.
   - Skin ARMAs for slots the item occupies are hidden, as in game.
   - Any shape inside the item's NIF that uses the skin shader or `actors\character\` body textures
     is drawn as mannequin instead of being skipped.
5. **Mannequin material is a first-class toggle:**
   - **Body textures:** the race skin ARMA's own textures and skin tint, like a BodySlide preview.
     Your BodySlide test images for the Issue were described correctly with a prompt guard
     against describing the person.
   - **Clay:** flat neutral grey. It may help where skin and item colours are close, or with
     skin-tight items.
   
   The toggle is per render in the UI, with a default in settings. An A/B test on a few items in
   Phase 1 sets the shipped default.
6. Pose: bind pose (SSE T/A-pose). Skinned shapes are placed with the NiSkinData global transform,
   as Outfit Studio does. SSE keeps skinned `BSTriShape` vertex data in `NiSkinPartition`, and Nifly
   handles that.

### 3.4 Renderer (D3D11, offscreen)
- 1024² with 4× MSAA, resolved and downsampled to the output size (512 by default, matching
  SkyrimNet).
- Shading covers the parts of `BSLightingShaderProperty` that affect what a VLM sees:
  - diffuse, with no sRGB conversion, matching the game
  - tangent-space normal maps; specular from normal alpha
  - environment cubemap and mask for metals
  - glow/emissive, vertex colours, double-sided
  - `NiAlphaProperty` test and blend
  - Community Shaders TruePBR (RMAOS)
- Effect shaders, particles and collision are skipped.
- Fixed neutral three-point studio lighting, so colour names come out right.
- Background is a light neutral grey for the VLM and transparent for the UI.
- Missing textures show as magenta and are listed as warnings in the UI.
- 32-bit indices, since some meshes exceed 65,535 vertices.

### 3.5 Views and framing
- Apparel: front, right, back and left at about 10° elevation, with a narrow-FOV perspective camera.
  - Framed on the item's bounding box plus padding, so the body shows around it.
  - A minimum frame size keeps rings, circlets and necklaces in context (e.g. hand plus forearm).
- Weapons: side profile, edge, ¾ above, and pommel or tip as appropriate. No body.
- Shields: front, back and edge.
- Output is 4 PNGs, plus an optional labelled 2×2 contact sheet as a single image (fewer image
  tokens, and some models compare views better that way).

### 3.6 Vision client
- **OpenAI-compatible `/chat/completions`** with multiple `image_url` parts. This covers OpenRouter
  (your current setup), OpenAI, LM Studio, llama.cpp server, Ollama and vLLM.
  - Settings: endpoint, key, model, temperature, top_p, max_tokens, plus an "extra JSON" box for
    things like `reasoning: {effort: high}` and `provider`.
  - **"Import from SkyrimNet"** reads `config/Providers.yaml`. Every `api_style: chat` provider
    becomes an ItemNet provider:
    - `id`, `name`, `endpoint`, `api_key` (resolving `api_key_from`)
    - `provider_settings`, merged into the request body
    - `reasoning`, which marks whether the reasoning param is sent
    
    That covers OpenRouter, NanoGPT, NIM, Chutes and custom endpoints. It also reads the active
    model preset's `vision:` section (model list, `provider_id`, `model_params` such as
    `reasoning_effort`) to pre-select the same vision model. Keys stay in ItemNet's local settings
    file and are never logged.
- **Prompt:** an editable Scriban template with the same JSON contract
  (`{"suggestedName", "description"}`).
  - Variables: name, type, EditorID, keywords, slots → body areas, gender, race, view list.
  - The default wording is our own (SkyrimNet's prompt text isn't ours to redistribute). It adds
    what the mannequin now makes possible: **what the item covers and what it leaves bare**.
  - You can paste your customised SkyrimNet prompt in as your own template.
- Tolerant JSON parsing: strips code fences and finds the first `{…}`. Retries are configurable.
- Each result records model, prompt hash, token usage and cost if the API returns them.

### 3.7 Catalog: ItemNet's own store and source of truth
- `ItemNet.db` (SQLite), keyed by **FormKey + gender**, so it's independent of load order and
  playthrough.
- Fields: suggested name, description, model, prompt version, render settings, visual key,
  timestamps and status (`draft` → `approved` → `synced`).
- Images are stored as `images/<Plugin>/<LocalID>_<gender>_<view>.png`.
- This fixes two SkyrimNet weaknesses:
  - A **new playthrough DB** can be filled from the catalog in one click.
  - A **load-order change** is fixed by re-syncing, which recomputes the runtime IDs.
- Export/import as a pack makes descriptions shareable per armour mod.

### 3.8 Sinks
**OmniSight DB sink (v1).** For each chosen DB (default: newest; "all" is an option):
- Compute the runtime FormID from the current load order:
  - full plugins: `idx<<24 | local&0xFFFFFF`
  - light plugins (ESL or ESL-flagged): `0xFE000000 | lightIdx<<12 | local&0xFFF`
- Upsert `<ID>_<gender>` with:
  - `description`, `suggested_name`, `subject_name`
  - `description_model = 'ItemNet:<model>'` (with `+edited` if you changed the text)
  - `description_locked = 1`, `needs_refresh = 0`, `is_valid = 1`
  - `image_width/height`
  - `file_path` → our PNGs, copied into `screenshots/omnisight/item_<ID>_<gender>_<n>.png` so the
    SkyrimNet dashboard shows the better images
  - `content_hash`: kept from the existing row when updating; SkyrimNet-compatible if we can
    replicate it (Phase 0), otherwise NULL
- Safety:
  - Refuse (or require an explicit override) while `SkyrimSE.exe` is running.
  - Back up the DB file before the first write in each session.
  - One transaction, with a busy timeout.
  - Confirm before replacing a `user-edited` row.

**Content-library YAML sink (later).** Writes `items/<form stem>.yaml` into an
`external/maeyanie.itemnet-*` layer, for sharing and for people who turn off OmniSight item capture.

### 3.9 UI (WPF)
```
┌──────────────────────┬───────────────────────────────────────────────────────┐
│ [search……………]        │  [Front] [Right] [Back] [Left]    ◉F ○M  Race[Nord ▾]  │
│ Type▾ Plugin▾ Status▾│  (four tiles; click = large view)  Weight[──●──] ↻     │
│──────────────────────│  ☑ Mannequin  warnings: 1 missing texture             │
│ Name   Type  Plugin ●│───────────────────────────────────────────────────────│
│ …virtualised list…   │  EDID / FormKey / runtime ID / slots / keywords / ARMAs│
│                      │  SkyrimNet now: "Green Loin-Guard…" [their 4 images]   │
│                      │───────────────────────────────────────────────────────│
│                      │  [Describe]   model: nemotron… 3.1k tok                │
│                      │  Name: [______________________________]               │
│                      │  Description: [ multi-line                        ]   │
│                      │  ☑ both genders   Target DB [current ▾]   [Save]       │
└──────────────────────┴───────────────────────────────────────────────────────┘
```
- Status dot: none, SkyrimNet auto, locked user edit, ItemNet draft, ItemNet synced.
- The side-by-side "SkyrimNet now" panel lets you see whether a re-describe is worth it.

### 3.10 CLI (`ItemNet.Cli`)
The CLI shares the same core: `index`, `render <FormKey>`, `describe <FormKey>`, `sync`. It's used
for batch runs and for headless verification during development.

---

## 4. Phase 0 results (2026-10-17)

| # | Unknown | Result |
|---|---|---|
| 1 | `content_hash` algorithm | **Solved**, from disassembly plus the oracle (see below). 259/313 current rows reproduce with one rule; since 2026-09-27 the only miss is one Wet and Cold alt-texture item. Earlier misses look like an older SkyrimNet build. |
| 2 | Does a locked row stick in game? | **Confirmed.** Camilla Valerius wearing Dark Seducer Armor gets ItemNet's name and description from `get_item_*_for_wearer` (checked via MCP `render_template`). The dashboard Vision entry shows it too. The row was untouched after OmniSight ran. Writes made while the game runs apply at the next cell change (see below). |
| 3 | Runtime FormID parity | **Confirmed.** Every row captured since the last load-order change maps back to the right record. |
| 4 | Bind-pose alignment, Nifly coverage | **Confirmed.** Skinning against the race skeleton puts every bone influence of a vertex on the same point (spread 0.000–0.3 units). Nifly (pure C#) loaded every test NIF, so no native fallback is needed so far. |
| 5 | Item YAML field names | **Open.** Only needed for the later sink. |

### Content hash
From `SkyrimNet.dll` beta 27 via `SkyrimNet.pdb`. `ItemModelRenderer::ComputeContentHash` builds
the input and `StandaloneItemRenderer::ComputeContentHash` hashes it:
```
input = EditorID + "|" + modelPath + ("|" + texturePath)*      (EditorID part omitted if empty)
hash  = FNV-1a 64 (offset 0xCBF29CE484222325, prime 0x100000001B3) → "%016llX"
```
- **`modelPath`:** the first ARMA's biped model for the gender, falling back to the other gender's.
  For weapons, the weapon model. In both cases the path is exactly as written in the record.
- **Textures:** every TXST path in every alternate-texture entry, taken from the item's own world
  model and from both genders of each ARMA. They are sorted ordinally and de-duplicated.
- **EditorID: the one the game keeps is from the last *master-flagged* plugin in the override
  chain.** Plain `.esp` renames never reach it. For example, `Requiem.esp`'s `REQ_Heavy_Iron_Hands`
  is ignored and the hash uses `ArmorIronGauntlets` from Skyrim.esm or USSEP.

The implementation is `ItemNet.Core/SkyrimNet/ContentHash.cs`. Rules for writing:
- Updating an existing row keeps SkyrimNet's own hash.
- A new row gets the computed one.

### Other findings
- **SkyrimNet's body filter drops real armour.** Its render of Dark Seducer Armor kept only the
  panty and greaves. It discarded `MaleDarkSeducer_Body` (texture `darkseducerbodym_d.dds`), which
  is the pauldrons-and-spiked-corset piece. That is why the model described a "chest bandeau". This
  is worth adding to the Issue.
- **About 37% of the current DB's item rows are keyed to stale runtime IDs** after load-order
  changes. At least one now resolves to a *different* item: `FE110D63` holds a "Sideless Tunic"
  description but now points at a hood. ItemNet's FormKey-keyed catalog with re-sync fixes this.
- **Nifly's `Matrix33` is already in row-vector form.** Use it as-is with System.Numerics; don't
  transpose.
- **Meshes lit by model-space normal maps carry no vertex normals.** That covers bodies, hands,
  feet and heads. Normals are rebuilt from geometry, welded by position, after skinning.
- **Winding varies between meshes,** so normals are faced towards the camera rather than trusting
  `SV_IsFrontFace`.
- **Environment reflections are sampled in camera space,** like the lights. A world-fixed cube made
  shiny black armour look silver in the left view only.
- **SMP collision proxies** (`Collision`, `Bag Col`, …) are dropped when they have no diffuse
  texture or have the hidden flag set.
- **Inserted rows mirror SkyrimNet's NULL-free values** (`''` and `0.0`), so its readers never see
  a NULL text column.
- **Timing:** Mode B index plus load order takes about 2 s for 670 plugins. One item renders 4
  views at 512² in about 1.2–1.4 s, including texture loads.

### CLI (dev harness, `src/ItemNet.Cli`)
| Command | What it does |
|---|---|
| `lo` | Load order check against MO2's `loadorder.txt` |
| `parity --db` | Runtime-ID parity per capture date |
| `hashtest --db [--rule]` | Content-hash oracle |
| `plan <item> [--verbose]` | Resolved NIFs, shapes and texture formats |
| `skintest <nif>` | Skinning spread |
| `render <item...> [--male] [--clay] [--no-mannequin] [--weight]` | PNGs and a 2×2 sheet |
| `upsert <item> --db --images [--live]` | Writes a locked OmniSight row. Refuses MO2 paths without `--live`; backs up the DB first. |

`<item>` accepts a runtime FormID (`06000873`), a FormKey (`000873:ccBGSSSE025-AdvDSGS.esm`) or an
EditorID.

### Writing while the game runs (tested 2026-10-17)
- **SkyrimNet caches item names and descriptions** in `OmniSightDescriptionCache`. It is built at
  game start and preloaded per cell on cell load. No time-based expiry was seen in over 5 minutes,
  and the binary contains no TTL setting.
- **A direct DB write is not seen by a running game.** The row changed; the prompt kept the cached
  name.
- **SkyrimNet's dashboard API** is at `http://127.0.0.1:8080/omnisight?api=…`:
  - `status` (GET)
  - `refresh` `{type, id}`: invalidates the cache entry; marks the row only if unlocked
  - `update_description` `{id, description | suggestedName}`: also sets `user-edited` and locks
  - `toggle_lock` `{id}`
  - `delete&id=`
- **Neither `refresh` nor `update_description` refreshed the *suggested-name* cache.** SkyrimNet's
  own dashboard name edits don't take effect live either, which is a SkyrimNet bug. `refresh` did
  invalidate the description entry, per the log.
- **A cell change is enough.** On entering a cell, SkyrimNet re-reads the rows from the DB ("Preloaded
  OmniSight cache for cell"). Stepping out of the Frozen Hearth and back in picked up the
  directly-written name. The stale value came from the decorator's cache, not a compiled-prompt
  cache: several different raw templates all returned it.
- **Design consequence:** ItemNet can write the DB while the game runs. Use SQLite's busy timeout
  and the online backup API for the pre-write backup, since a plain file copy of an open DB can
  tear. Tell the user the change applies at the next cell change or load.

### Left for the in-game test
1. With Skyrim closed: `upsert … --live` for one item the player can equip, against the current
   playthrough DB.
2. Load the save and wear it. Check the description in the dashboard and in the rendered NPC/player
   prompt (SkyrimNet MCP `render_template`).
3. Re-equip it, wait past OmniSight's capture interval, and check the row is still ItemNet's.
   Then test once with the game running, to see whether SkyrimNet caches rows at load.

## 5. Phases
- **Phase 0, spikes (CLI only):** Mode B file system plus load order; item index; render one item
  with the mannequin to PNG; content-hash oracle test; one OmniSight upsert into a *copy* of the DB,
  then a live in-game test on a throwaway save.
- **Phase 1, MVP GUI:** list and search, render 4 views, Describe, edit, Save to the OmniSight DB
  plus the catalog. ARMO only; female and male; Mode A and Mode B.
- **Phase 2:**
  - Batch queue: filter → render + describe → drafts → keyboard-driven review (approve / edit /
    skip). Concurrency limit, resume, cost estimate.
  - Weapons and shields presets.
  - Interactive orbit viewer.
  - Arms-down pose by rotating the upper-arm bones through skinning, since T-pose arms hide side
    views.
  - Catalog pack export and import; YAML sink.

## 6. Solution layout
```
ItemNet.sln
src/ItemNet.Core     game file system (A/B), BSA index, item index, catalog, sinks, runtime-ID math
src/ItemNet.Render   NIF → GPU meshes, DDS, mannequin assembly, D3D11 offscreen renderer, framing
src/ItemNet.Vision   OpenAI-compatible client, Scriban prompts, response parsing
src/ItemNet.App      WPF (MVVM)
src/ItemNet.Cli      headless commands
tests/ItemNet.Tests  runtime-ID math, DDS header parsing, response parsing, sink against a DB copy
```

## 7. Risks
- **NIF edge cases:** `BSDynamicTriShape`, `BSSubIndexTriShape`, effect shaders, and SMP/HDT parts
  (rendered static in bind pose, which is fine).
- **The model describing the mannequin anyway.** Mitigated by the clay material plus a prompt line.
  It's worth A/B testing on a few items.
- **Load-order drift:** sync must recompute IDs every time, and never cache them in the catalog.
- **SkyrimNet schema or behaviour changes in later betas.** Check `schema_migrations` (currently 30)
  and refuse to write to unknown schemas.
