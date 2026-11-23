# SkinForger

**中文** · [English](README_EN.md)

给 **Steam 版《Muse Dash》（喵斯快跑）** 换皮肤的 Mod。
装好之后在游戏里按一下键盘 **`0`**，就能开关别人做好的皮肤包，给角色和小精灵换个样子；想回到原样，把开关关掉就行。

**完全免费，源码公开。**

---

## 三步装好

1. **装 MelonLoader**（Mod 加载器，用 **0.7** 系列）——
   到 [MelonLoader 发布页](https://github.com/LavaGang/MelonLoader/releases) 下载安装器，运行后选游戏目录里的 `MuseDash.exe`。
2. **把 `SkinForger` 文件夹整个丢进游戏的 `Mods` 文件夹。**
3. **启动游戏，按 `0`。**

以后要加皮肤，只要把 `.sfskin` 文件丢进 `Muse Dash\SkinForger\Skins\` ——
不用解压、不用改名，游戏开着放进去也行。

> 找不到游戏文件夹？Steam 库里右键 Muse Dash → 管理 → 浏览本地文件。
> 默认一般在 `C:\Program Files (x86)\Steam\steamapps\common\Muse Dash`
>
> 上面三步看不懂的话，往下翻[详细安装说明](#详细安装说明)。

---

## 怎么用

在主界面按 **`0`** 打开 **Skin Library（皮肤库）**，再按一次关闭。

- 每个皮肤包是一张卡片：名字、作者、预览图、改了哪些角色。
- 点卡片上的**开关**启用或关闭；点 **Details** 看介绍和详细内容。
- **可以同时开好几个。** 如果两个包改了同一个角色，**后开启的那个优先**，皮肤库会提示你哪里有冲突。
- 想换回游戏原样：把所有开关关掉。

按 **`1`** 打开的是**创作者界面**，给做皮肤的人用，普通玩家不用管它。

---

## 详细安装说明

### 需要准备什么

| 项目 | 说明 |
|---|---|
| 游戏 | **Steam 版** Muse Dash（Windows 电脑）。手机版、其他平台**不支持** |
| 前置 Mod | **MelonLoader 0.7.x**（开发时用的是 0.7.4） |
| 其他 Mod | 不需要 |
| 计划通（DLC） | **有没有都一样**，没买也能正常用 |

MelonLoader 装好后启动一次游戏，游戏文件夹里会出现 `Mods`、`MelonLoader`、`UserData`
几个文件夹，出现了就说明装成功了。

### 放文件

先**关闭游戏**，把下载到的 `SkinForger` 文件夹**整个**放进 `Mods` 里，放好后是这样：

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

> **不要**把这个文件夹里的文件拆开放，也不要改文件名。
> `SkinForger_MuseDashWatchdog.exe` 是自带的小助手，游戏关闭后做清理用的，不是病毒。

### 第一次启动

SkinForger 会在游戏文件夹里自动建一个 `SkinForger` 文件夹：

```
Muse Dash\
└─ SkinForger\
   ├─ Skins\          ← 皮肤包（.sfskin）放这里
   ├─ ExportSkin\     ← 做皮肤的人导出的包会出现在这里
   └─ SkinCreator\    ← 做皮肤用的，普通玩家不用管
```

把 `.sfskin` 丢进 `Skins\` 就行，可以同时放很多个。游戏开着放进去也能认到。

---

## 常见问题

**按 0 没有反应？**
- 确认 MelonLoader 装好了：启动游戏时会先出现一个黑色的命令行窗口。
- 确认 `SkinForger` 文件夹是放在 `Mods` 文件夹**里面**的。
- 确认按的是主键盘上方那排的 `0`。

**皮肤库里看不到我的皮肤包？**
- 确认文件在 `Muse Dash\SkinForger\Skins\` 里，文件名以 `.sfskin` 结尾。
- 从网上下载的如果是压缩包，要先解压，再把里面的 `.sfskin` 放进去。

**开了皮肤，但角色没变？**
- 进一次那个角色的界面，或者打一首歌 —— 皮肤会在角色出现时加载。
- 看看是不是另一个开着的包也改了这个角色，而且它的优先级更高。

**手机版能用吗？**
不能，只支持 Steam 电脑版。

**出了问题怎么反馈？**
把游戏文件夹里的 `MelonLoader\Latest.log` 发给作者，并说明你做了什么、看到了什么。

---

## 卸载

关掉游戏，删除 `Muse Dash\Mods\SkinForger` 文件夹就卸载完了。
不想留皮肤包和制作文件的话，`Muse Dash\SkinForger` 文件夹也可以一起删。

> SkinForger 不修改游戏本体文件，卸载后游戏就是原样。

---

## 关于这个项目 / 源码

作者 **雷茂教主Ray5Green**，2026 年 9 月 24 日开工，持续开发中。

起步时基于开源项目 [MuseDashSkinChanger](https://github.com/Nullctipus/MuseDashSkinChanger)（原作者 BustR75）；
那部分代码已于 2026-11-17 起全部重写移除，现在不含上游代码（见 `NOTICE.md`）。
仍然感谢原作者当年公开了代码 —— 没有它这个项目起不来。

**源码就在这个仓库里**，想看、想抄、想搬进自己的项目都可以（见下面的使用条款）：

| 想看什么 | 去哪儿 |
|---|---|
| 扩框是怎么做到的 | `docs/expansion/扩框机制说明.txt` —— 不需要用过 Spine 也能看懂 |
| 运行时代码 | `src/core/`（模块）、`src/main/`（Main）、`src/<功能>/Runtime/` |
| 为什么把 Main 整个重写了 | `docs/为什么重写了Main.md` |
| 踩过的坑和解法 | `docs/development-logs/BUG_LOG.md`（70 条） |
| 每个功能为什么这么做 | `docs/development-logs/开发历程.md` |
| 怎么编译 | `docs/BUILD.md` |

仓库源码编译出的 `SkinForger.Core.dll` 和 `SkinForger.Melon.dll` 与发布包里的**逐字节一致**。

> **想自己做皮肤？** 按 `1` 就是创作者界面，完整教程见本仓库根目录的
> **[SkinForger 皮肤制作教程_CN.pdf](SkinForger%20%E7%9A%AE%E8%82%A4%E5%88%B6%E4%BD%9C%E6%95%99%E7%A8%8B_CN.pdf)**（English: **[SkinMakingTutorial_EN.pdf](SkinForger%20SkinMakingTutorial_EN.pdf)**）。

---

## 使用条款

全文见随附的 `LICENSE`（《SkinForger 使用条款》，源码公开 / source-available）。两句话讲完：

**第一层：代码、做法、文档 —— 随便拿，没有任何条件。**

> 阅读、复制、修改、学习、讲解、搬进自己的项目都可以，**任何用途，包括商业用途**。
> 不需要署名，不需要开源你自己的项目，不需要告诉我，也不需要付钱。
> 扩框那套机制（`docs/expansion/扩框机制说明.txt`）本来就是写出来给人看的，请拿去用。

**第二层：SkinForger 这个成品 —— 只有一个条件。**

> 当你分发的东西**就是 SkinForger**（或一眼能认出是它的修改版，换名字也算）时：
>
> **不得把它用于、适配到、或分发给非 Steam 正版的《Muse Dash》。**
>
> 包括破解版、盗版、未经授权的移植版、未经授权的移动端或主机端版本。
> 想给别的平台做，**可以来问我**。

除此之外没有别的限制，唯一的手续是分发 SkinForger 时把 `LICENSE` 一起带上，
好让下一个人也知道上面那一条。（只搬代码片段连这个都不用 —— 那属于第一层。）

### 不保证，不负责

**本软件按「现状」提供，不附带任何形式的保证。**
用了这份代码或用了 SkinForger，无论出现什么问题 —— 游戏损坏、存档丢失、账号受影响、
数据损毁、经济损失或任何其他直接或间接的损害 —— **都由你自己承担**。
你改过之后出的问题，更是你自己的问题。

### 支持范围

SkinForger **只**针对 **Steam 正版 Muse Dash（Windows）** 开发和验证。
手机版、主机版、其他平台、其他来源的游戏本体，以及任何非官方构建，都不在本项目范围内 ——
作者不提供协助，也不受理由此产生的问题。非官方构建没走过实机验证流程，
**有可能让你的游戏文件处于异常状态**，请只使用作者发布的版本。

### 皮肤包属于皮肤作者，不属于这个 Mod

这一条请所有人都看清楚。

`.sfskin` 里的图是**做皮肤那个人的作品**，版权在 TA 自己手里，**和本 Mod 的许可完全无关**。
SkinForger 的许可管不着这些图，作者也无权替皮肤作者做任何授权。

每个包里都记录着**皮肤作者自己的选择**（导出时的 Sharing 勾选框）：

- **没有勾选（默认）**：只允许由官方 SkinForger、在作者制作时所在的平台上加载，其他构建会被拒绝。
- **勾选了**：作者同意任何构建加载它。

SkinForger 会尊重这个选择。**违背皮肤作者记录的选择去加载、转换或再分发皮肤包，
侵害的是那位皮肤作者的权利**，这件事独立于本 Mod 的许可 ——
在修改版里把这个检查删掉，并不能让你获得皮肤作者没有给出的许可。

如果你是皮肤作者：你的作品怎么流传由你决定。
如果你是玩家：请从皮肤作者本人发布的渠道获取皮肤包。

---

## 支持作者

SkinForger 是免费的，以后也一直免费。如果它让你玩得开心，可以请我喝杯咖啡 —— 完全自愿，不给也什么都不少，不影响任何功能。

[<img src="https://storage.ko-fi.com/cdn/kofi1.png" alt="请我喝杯咖啡" height="40">](https://ko-fi.com/ray5green)

<https://ko-fi.com/ray5green>

---

## 特别鸣谢

| 名字 | 帮助内容 |
|---|---|
| 诗歆岚 | 测试皮肤库、测试导出皮肤包 |

同时感谢：

- **Stitchwork**（作者 **FabulatorK**）—— 《空洞骑士：丝之歌》的皮肤制作工具。我之前做丝之歌皮肤时一直在用它，这是我用过最好用的皮肤制作工具；SkinForger 的制作流程和界面大量参考了它
- **BustR75** 和 **MuseDashSkinChanger** 项目，SkinForger 在它的基础上发展而来
- **MelonLoader** 团队
- **PeroPeroGames** 带来了 Muse Dash 这款游戏

---

SkinForger 是玩家制作的非官方 Mod，与 PeroPeroGames 无关。
Muse Dash 及其角色、美术素材的版权归原权利人所有。

**作者：雷茂教主Ray5Green**
