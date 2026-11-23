# SkinForger


A skin mod for **Muse Dash on Steam**.
Once it is installed, press **`0`** in game to switch skin packs on and off, giving characters and elfins a new look. Turn the switches off and you are back to the original game.

**Free, and the source is public.**

---

## Install in three steps

1. **Install MelonLoader** (the mod loader — use the **0.7** series).
   Grab the installer from the [MelonLoader releases page](https://github.com/LavaGang/MelonLoader/releases), run it, and point it at `MuseDash.exe` in your game folder.
2. **Drop the whole `SkinForger` folder into the game's `Mods` folder.**
3. **Start the game and press `0`.**

To add skins later, just drop `.sfskin` files into `Muse Dash\SkinForger\Skins\` —
no unpacking, no renaming, and you can do it while the game is running.

> Can't find the game folder? In your Steam library, right-click Muse Dash → Manage → Browse local files.
> The default is usually `C:\Program Files (x86)\Steam\steamapps\common\Muse Dash`
>
> If those three steps were too terse, see [Detailed installation](#detailed-installation) below.

---

## How to use it

Press **`0`** on the main menu to open the **Skin Library**; press it again to close.

- Each skin pack is a card: name, author, preview image, and which characters it changes.
- Use the **toggle** on a card to enable or disable it; click **Details** for the description and full contents.
- **Several packs can be on at once.** If two packs change the same character, **the one enabled later wins**, and the Skin Library tells you where the conflicts are.
- To go back to the original game: turn every toggle off.

**`1`** opens the **creator interface**, which is for people making skins. Players can ignore it.

---

## Detailed installation

### What you need

| Item | Notes |
|---|---|
| Game | Muse Dash, **Steam version** (Windows PC). Mobile and other platforms are **not supported** |
| Prerequisite | **MelonLoader 0.7.x** (0.7.4 was used during development) |
| Other mods | None needed |
| DLC ("Just As Planned") | **Makes no difference** — everything works without it |

After installing MelonLoader, launch the game once. The folders `Mods`, `MelonLoader` and `UserData`
will appear in the game folder; that means it worked.

### Put the files in place

**Close the game first**, then move the downloaded `SkinForger` folder **as a whole** into `Mods`. It should end up like this:

```
Muse Dash\
└─ Mods\
   └─ SkinForger\
      ├─ SkinForger.Melon.dll
      ├─ SkinForger.Core.dll
      ├─ SkinForger_MuseDashWatchdog.exe
      ├─ manifest.json
      ├─ LICENSE
      └─ NOTICE.md
```

> **Do not** split the files out of that folder, and do not rename them.
> `SkinForger_MuseDashWatchdog.exe` is a small bundled helper that cleans up after the game closes. It is not a virus.

### First launch

SkinForger creates a `SkinForger` folder in the game directory on its own:

```
Muse Dash\
└─ SkinForger\
   ├─ Skins\          ← put skin packs (.sfskin) here
   ├─ ExportSkin\     ← packs exported by skin makers appear here
   └─ SkinCreator\    ← for making skins; players can ignore it
```

Drop `.sfskin` files into `Skins\` — as many as you like. They are picked up even while the game is running.

---

## FAQ

**Pressing 0 does nothing.**
- Check that MelonLoader is installed: a black console window appears first when the game starts.
- Check that the `SkinForger` folder sits **inside** the `Mods` folder.
- Make sure you are pressing the `0` on the main number row.

**My skin pack isn't in the Skin Library.**
- Check that the file is in `Muse Dash\SkinForger\Skins\` and that its name ends in `.sfskin`.
- If you downloaded an archive, extract it first and put the `.sfskin` from inside it into the folder.

**The pack is enabled but the character looks unchanged.**
- Visit that character's screen once, or play a song — skins load when the character appears.
- Check whether another enabled pack also changes that character and has higher priority.

**Does it work on mobile?**
No. Steam on PC only.

**How do I report a problem?**
Send the author `MelonLoader\Latest.log` from your game folder, along with what you did and what you saw.

---

## Uninstalling

Close the game and delete the `Muse Dash\Mods\SkinForger` folder. That's it.
If you don't want to keep your skin packs and creator files, you can delete `Muse Dash\SkinForger` as well.

> SkinForger does not modify the game's own files, so after uninstalling the game is exactly as it was.

---

## About this project / source code

By **雷茂教主Ray5Green**. Started 24 September 2026, still in active development.

It began from the open-source project [MuseDashSkinChanger](https://github.com/Nullctipus/MuseDashSkinChanger) (originally by BustR75).
That code has been fully rewritten and removed since 2026-11-17, so no upstream code remains (see `NOTICE.md`).
Thanks to the original author all the same — without that code this project would never have got started.

**The source is in this repository.** Read it, copy it, lift it into your own project (see the terms below):

| What you're looking for | Where |
|---|---|
| How frame expansion works | `docs/expansion/扩框机制说明.txt` — readable even if you have never touched Spine |
| Runtime code | `src/core/` (modules), `src/main/` (Main), `src/<feature>/Runtime/` |
| Why Main was rewritten from scratch | `docs/为什么重写了Main.md` |
| Bugs hit and how they were solved | `docs/development-logs/BUG_LOG.md` (70 entries) |
| Why each feature is built the way it is | `docs/development-logs/开发历程.md` |
| How to build | `docs/BUILD.md` |

`SkinForger.Core.dll` and `SkinForger.Melon.dll` built from this repository are **byte-for-byte identical** to the ones in the release package.

> **Want to make your own skins?** Press `1` for the creator interface, and see
> **[SkinForger SkinMakingTutorial_EN.pdf](SkinForger%20SkinMakingTutorial_EN.pdf)** in the repository root (中文: **[皮肤制作教程_CN.pdf](SkinForger%20%E7%9A%AE%E8%82%A4%E5%88%B6%E4%BD%9C%E6%95%99%E7%A8%8B_CN.pdf)**).

---

## Terms of use

The full text is in the bundled `LICENSE` (the *SkinForger Terms of Use*, source-available). In short:

**Layer one: the code, the techniques, the documentation — take them, no conditions at all.**

> Read, copy, modify, study, explain, or lift them into your own project, **for any purpose, commercial included**.
> No attribution required, no need to open-source your own project, no need to tell me, no payment.
> The frame-expansion mechanism (`docs/expansion/扩框机制说明.txt`) was written to be read — please use it.

**Layer two: the finished SkinForger product — one condition only.**

> When the thing you distribute **is SkinForger** (or a modified version recognisable as it, renamed or not):
>
> **It must not be used with, adapted to, or distributed for non-legitimate (non-Steam) copies of Muse Dash.**
>
> That includes cracked and pirated copies, unauthorised ports, and unauthorised mobile or console builds.
> If you want to support another platform, **ask me** — that conversation is open.

There are no other restrictions. The only formality is to ship `LICENSE` along with SkinForger when you distribute it,
so the next person knows about that one condition. (Lifting code snippets doesn't even need that — that's layer one.)

### No warranty, no liability

**This software is provided "as is", without warranty of any kind.**
Whatever happens from using this code or using SkinForger — a broken game, lost saves, an affected account,
corrupted data, financial loss, or any other direct or indirect damage — **is on you**.
Problems caused by your own modifications are all the more your own.

### Scope of support

SkinForger is developed and verified **only** against **legitimate Steam Muse Dash on Windows**.
Mobile, console, other platforms, game binaries from other sources, and any unofficial build are out of scope —
the author offers no help with them and will not take up issues arising from them. Unofficial builds have not
been through the on-device verification process and **may leave your game files in a bad state**,
so please only use builds released by the author.

### Skin packs belong to their creators, not to this mod

Everyone please read this one carefully.

The artwork inside a `.sfskin` is **the work of the person who made that skin**. They hold the copyright, and it has
**nothing to do with this mod's licence**. SkinForger's licence does not govern that artwork, and the author has no
right to grant any permission on a skin creator's behalf.

Every pack records **the skin creator's own choice** (the Sharing checkbox at export time):

- **Unchecked (default)**: the pack may only be loaded by official SkinForger, on the platform the creator made it on. Other builds are refused.
- **Checked**: the creator agrees to any build loading it.

SkinForger respects that choice. **Loading, converting, or redistributing a pack against the creator's recorded choice
violates that creator's rights** — a matter independent of this mod's licence. Deleting the check in a modified build
does not grant you permission the skin creator never gave.

If you make skins: how your work travels is your decision.
If you play: please get skin packs from the channels the skin creator publishes them on.

---

## Support the author

SkinForger is free and always will be. If it made your game more fun, you can buy me a coffee — entirely optional, and it unlocks nothing.

[<img src="https://storage.ko-fi.com/cdn/kofi1.png" alt="Buy me a coffee at ko-fi.com" height="40">](https://ko-fi.com/ray5green)

<https://ko-fi.com/ray5green>

---

## Special thanks

| Name | For |
|---|---|
| 诗歆岚 | Testing the Skin Library and skin pack export |

With thanks also to:

- **Stitchwork** by **FabulatorK** — the skin-making tool for Hollow Knight: Silksong. I used it for my own Silksong skins and it is the best skin tool I have used; SkinForger's workflow and UI borrow heavily from it
- **BustR75** and the **MuseDashSkinChanger** project, which SkinForger grew out of
- the **MelonLoader** team
- **PeroPeroGames**, for Muse Dash itself

---

SkinForger is an unofficial, player-made mod and is not affiliated with PeroPeroGames.
Muse Dash, its characters, and its art assets remain the property of their respective owners.

**Author: 雷茂教主Ray5Green**
