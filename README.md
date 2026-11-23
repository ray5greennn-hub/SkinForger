# SkinForger by 雷茂教主Ray5Green

给 **Steam 版《Muse Dash》（喵斯快跑）** 换皮肤的 Mod。装好之后在游戏里按键盘 **`0`**，就能开关别人做好的皮肤包，给角色和小精灵换个样子。
A skin mod for **Muse Dash on Steam**. Once installed, press **`0`** in game to switch skin packs on and off, giving characters and elfins a new look.

完全免费，源码公开。
Free, and the source is public.

完整说明：**[中文 README_CN.md](README_CN.md)** · **[English README_EN.md](README_EN.md)**
Full documentation: **[中文 README_CN.md](README_CN.md)** · **[English README_EN.md](README_EN.md)**

---

## 安装 / Installation

**1. 装 MelonLoader（Mod 加载器，用 0.7 系列）**
**1. Install MelonLoader** (the mod loader — use the **0.7** series)

下载地址 / Download: **https://github.com/LavaGang/MelonLoader/releases**

下载安装器，运行后选游戏目录里的 `MuseDash.exe`。
Run the installer and point it at `MuseDash.exe` in your game folder.

> 找不到游戏文件夹？Steam 库里右键 Muse Dash → 管理 → 浏览本地文件。
> Can't find the game folder? In your Steam library: right-click Muse Dash → Manage → Browse local files.

**2. 关掉游戏，把 `SkinForger` 文件夹整个放进游戏的 `Mods` 文件夹**
**2. Close the game, then put the whole `SkinForger` folder into the game's `Mods` folder**

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

不要把里面的文件拆开放，也不要改文件名。
Do not split the files out of that folder, and do not rename them.

**3. 启动游戏，在主界面按 `0`**
**3. Start the game and press `0` on the main menu**

按 `0` 打开皮肤库，点卡片上的开关启用皮肤；想回到游戏原样，把开关全部关掉。
Press `0` to open the Skin Library and use the toggle on a card to enable a pack. To go back to the original game, turn every toggle off.

**4. 以后要加皮肤，把 `.sfskin` 文件丢进 `Muse Dash\SkinForger\Skins\`**
**4. To add skins later, drop `.sfskin` files into `Muse Dash\SkinForger\Skins\`**

不用解压、不用改名，游戏开着放进去也能认到。
No unpacking, no renaming, and you can do it while the game is running.

### 卸载 / Uninstalling

关掉游戏，删除 `Muse Dash\Mods\SkinForger` 文件夹就行。SkinForger 不修改游戏本体文件。
Close the game and delete the `Muse Dash\Mods\SkinForger` folder. SkinForger does not modify the game's own files.

---

## 支持作者 / Support the author

SkinForger 是免费的，以后也一直免费。如果它让你玩得开心，可以请我喝杯咖啡。
SkinForger is free and always will be. If it made your game more fun, you can buy me a coffee.

[<img src="https://storage.ko-fi.com/cdn/kofi1.png" alt="Buy me a coffee at ko-fi.com" height="40">](https://ko-fi.com/ray5green)

<https://ko-fi.com/ray5green>

---

## 特别鸣谢 / Special thanks

| 名字 / Name | 帮助内容 / For |
|---|---|
| 诗歆岚 | 测试皮肤库、测试导出皮肤包 / Testing the Skin Library and skin pack export |

同时感谢：
With thanks also to:

- **Stitchwork**（作者 **FabulatorK**）—— 《空洞骑士：丝之歌》的皮肤制作工具。我之前做丝之歌皮肤时一直在用它，这是我用过最好用的皮肤制作工具；SkinForger 的制作流程和界面大量参考了它
  **Stitchwork** by **FabulatorK** — the skin-making tool for Hollow Knight: Silksong. I used it for my own Silksong skins and it is the best skin tool I have used; SkinForger's workflow and UI borrow heavily from it
- **BustR75** 和 **[MuseDashSkinChanger](https://github.com/Nullctipus/MuseDashSkinChanger)** 项目 —— SkinForger 当年是在它的基础上起步的，感谢原作者公开了代码
  **BustR75** and the **[MuseDashSkinChanger](https://github.com/Nullctipus/MuseDashSkinChanger)** project — SkinForger got its start from it; thanks to the original author for making that code public
- **[MelonLoader](https://github.com/LavaGang/MelonLoader)** 团队
  the **[MelonLoader](https://github.com/LavaGang/MelonLoader)** team
- **PeroPeroGames** 带来了 Muse Dash 这款游戏
  **PeroPeroGames**, for Muse Dash itself

---

SkinForger 是玩家制作的非官方 Mod，与 PeroPeroGames 无关。Muse Dash 及其角色、美术素材的版权归原权利人所有。
SkinForger is an unofficial, player-made mod and is not affiliated with PeroPeroGames. Muse Dash, its characters, and its art assets remain the property of their respective owners.

使用条款见随附的 `LICENSE`；皮肤包的版权属于皮肤作者本人，不属于本 Mod（详见完整说明）。
Terms of use are in the bundled `LICENSE`. Skin packs are the copyright of their own creators, not of this mod (see the full documentation).

**作者 / Author：雷茂教主Ray5Green**
