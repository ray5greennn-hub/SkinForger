# 为什么把 Main 整个重写了

*2026-11-22*

SkinForger 起步时基于 [MuseDashSkinChanger](https://github.com/Nullctipus/MuseDashSkinChanger)（原作者 BustR75，GPL-3.0，2022 年）。
2026-11-17 决定把那部分代码全部重写移除，11-19 完成。这份文档说明为什么要做、怎么做的、以及怎么核验「真的不含上游代码了」。

公开它的原因很简单：这类「从别人的 Mod 起步、三个月后想换许可」的处境不少见，而网上几乎没人写过具体怎么收尾。

---

## 一、三个各自独立的理由

任何一个单独成立都值得做。

### 1. GPL 的「对应源码」义务

GPL-3.0 第 6 条要求：分发二进制时必须提供**对应的源码**，定义是「修改这个作品时最常用的形式」。

当时的 `SkinForger.Melon.dll`（下文称 Main）**没有可编译的源码**。它是一条 Cecil 二进制补丁链的产物 —— 每加一个功能就对上一版 DLL 做一次 IL 手术，手里只有 ILSpy 的反编译快照，而快照编译不出装在游戏里的那个 DLL。

**这个义务只在「分发」时产生。** SkinForger 当时尚未公开发布，所以不存在违规。这不是补锅，是发布前必须做完的一件事。

### 2. 可维护性

Main 每改一次都要走一遍 IL 手术。而 `SkinForger.Core` 的二十来个模块几乎全是「钩在 Main 上」的补丁层 —— 模块注释里反复出现 "postfix on Main's X"、"Main's IL read with Cecil"。这个结构能跑，但每个新功能的成本都在上升。

### 3. 许可自由（真正的动因）

只要 Main 还含上游的表达，整个 SkinForger 就必须是 GPL-3.0。更要紧的是：Main 被绑定到了 `SkinForger.Core`（缺 Core 直接拒绝加载），这意味着 **Main 和 Core 构成同一个程序，Core 两万行自主代码也被一起锁在 GPL 里**。

Main 删掉，这个锁才解除。

---

## 二、清点：上游到底占多少

| 对象 | 行数 | 性质 |
|---|---:|---|
| 上游 2022 年的 `Back.cs` 676 + `Front.cs` 269 | **945** | GPL-3.0，要切除的来源 |
| 当时的 Main（反编译快照，155 个方法） | **4,469** | 无可编译源码；含上游表达 |
| `SkinForger.Core`（18 个模块） | **20,251** | 完全自主，但依附 Main |

比例约 **1 : 21**。扩框、皮肤包、Preview、原状恢复、图集所有权、和谐部件拦截 —— 上游一行代码都没有。

### 上游真正贡献的东西

读完那 945 行后确认，它实际只做了六件事：

1. 一个 `GUI.Window` + 快捷键开关 + 可拖动窗口；
2. `Dictionary<int,int>` 选择表，存成 `Saved.json` / `SavedElfin.json`；
3. 每个角色一行，`default` 按钮 + 若干皮肤按钮，选中变绿；
4. `Reload` / `Extract All` / `Deselect All` / `Show Preview` / `Reset Window` 五个按钮；
5. 扫描 `Skins\` 建字典、贴图缓存、**整张贴图替换**、整图导出；
6. 五个 Harmony 挂点。

全部是最通用的实现，没有受保护的创造性表达。但**写法**必须换掉：新 Main 不照抄这些方法体，不沿用它的类/字段命名结构。

**文件格式和目录约定故意保持不变** —— `Saved.json`、`Skins\`、manifest 这些是互操作格式，不是受保护的表达，而且必须兼容，否则玩家现有的皮肤文件夹就全废了。

---

## 三、关键的一件好事：规格说明早就写好了

Core 当时钩住 **25 个 Main 方法、约 112 处引用**。这就是新 Main 必须提供的 API 面：

| 被钩的方法 | 引用数 |
|---|---:|
| `ReloadAssets` | 12 |
| `TryBuildSynthesizedAtlasForSkeleton` | 10 |
| `ReloadSkins` / `ApplyRegionPngToAtlas` | 各 9 |
| `GetTexture` | 6 |
| `SkeletonRendererAwake` / `SkeletonGraphicAwake` / `DumpCostume` / `ApplySkeleton` | 各 5 |
| `Scan` / `SaveSelection` / `GetExistingRegionPath` / `DrawWindow` / `CharacterApplyOnEnable` / `ApplyRegionPlacementToAttachment` | 各 4 |
| `GetRegionAnchor` / `ExtractAttachment` / `EnsureAtlasCapacity` / `CalculateAlphaBounds` | 各 3 |

Core 的模块注释里早已把这 25 个方法的行为契约写清楚了（当初是用 Cecil 读 Main 的 IL 反推出来的）。

**也就是说：新 Main 的规格说明已经存在，而且是本项目自己写的。** 照这份契约写，不需要再看一眼上游代码 —— 这正好是 clean-room 需要的状态。

---

## 四、clean-room 纪律

1. **不照抄反编译快照。** 那份快照是上游的衍生物，整段复制会把 GPL 义务带进新代码。
2. **写新 Main 的依据只有三样**：游戏自己的 API、Core 模块注释里自己写下的契约、开发日志里自己的记录。
3. **自己发明的算法可以重新表达**：anchor sidecar、placement、packed rect 独立矩形、旋转契约、反预乘、alpha 边界、扩框补几何 —— 这些本来就是自己的，Core 里还有更新的版本。
4. **格式与路径保持兼容**：玩家和皮肤作者现有的文件必须继续可用。
5. **留记录**：每个阶段写一条「依据什么写的」。将来若有人质疑，这份记录就是证据。
6. **留一条退路**：真有人质疑，上面第五节那套核验数据就是摆出来的东西。

---

## 五、怎么核验「不含上游代码」

对照 2022 年归档的上游源码（945 行），用查抄袭的标准做法 —— 连续 token 片段比对：

| 片段长度 | 重合 | 占上游 |
|---|---:|---:|
| 6 token | 231 | 5.88% |
| 8 token | 81 | 1.81% |
| **12 token** | **15** | **0.29%** |

那 15 条**全部**是 C#/Unity 的通用写法或游戏自己的名字，例如
`GUILayout.EndScrollView(); GUI.DragWindow(new Rect(`、`if (File.Exists(file))`、
`{ mainShow, battleShow, victoryShow, failShow }`。没有一条属于上游的独创表达。

**标识符重合**：上游声明 46 个，新 Main 里出现 14 个。其中 `OnGUI` / `Start` 是引擎回调；
`Scan` / `SaveSelection` / `ReloadAssets` / `GetTexture` 等是 `SkinForger.Core` 用 Harmony
**按名字**绑定的接缝，**必须**叫这个名字。名字是接口，不是表达。

**构建**：`SkinForger.Main.csproj` 只编译自身目录与十个 `<功能>/Runtime/` 目录，上游那两个文件不在其中。

**第三方依赖**：两个项目**没有任何 NuGet 包**；所有引用都是本机已有的 DLL 且不随产品拷贝
（MelonLoader 为 Apache-2.0）。**没有任何 GPL / LGPL 库被链接或打包。**

---

## 六、最大的教训：切掉的不是 945 行，是一块主板

这件事真正的难点不在版权，在于**旧 Main 三个月里长成了一块主板**，而所有功能都是插在它上面的卡。
插卡的方式有三种，**三种在源码里都看不见**：

### 1. 方法名契约

Core 用 `AccessTools.TypeByName` + 方法名找 Main。名字对不上 → Harmony 补丁装不上 → 整个模块静默消失。
新 Main 必须保留这些名字，哪怕它们是上游起的。

### 2. 参数名契约

Harmony 的 prefix/postfix 按**参数名**绑定。参数改个名字、换个顺序，那一块功能整体失效，而且**不报错**。
真实案例：授权版把 `(skeleton, atlas, textureName, skin, elfin)` 里两个字符串参数换了位置 —— 皮肤包整条路静默失灵。

### 3. 字段名契约

反射取字段，取不到就返回 null，然后**什么都不发生**。
真实案例：Core 的动画控制挂在 `_skeletonAnimation` 上，而授权 Preview 克隆的是战斗物体、类型是 `SkeletonMecanim`、放在 `_skeletonRenderer` 里 → 四处 `is not SkeletonAnimation` 判断一律失败 → Preview 窗口开着，里面什么都没有。

还有一类最毒的：**同名同签名，但含义被改了**。
旧 Main 的 `GetCount(string)` 是「配置表有几行」，授权版写成了「这个角色有几套皮肤」 ——
`GetCount("character")` 返回 0，角色列表为空，Preview 一片空白。所有按名字比对的工具都查不出来。

### 于是做了一套离线检查工具

都在 `src/main/Analysis/`，纯 Python，不需要游戏：

| 工具 | 查什么 |
|---|---|
| `contract-parity.py` | Core 要的方法，新 Main 有没有、签名对不对 |
| `param-name-parity.py` | Harmony 绑定的参数名有没有被改 |
| `field-name-parity.py` | 反射取的字段名还在不在 |
| `injection-parity.py` | 旧版用 Cecil 注入的调用，新版有没有真的调 |
| `host-call-parity.py` | Core 的宿主调用点有没有落空 |
| `dead-feature-check.py` | 有没有「代码在、但没人调用」的功能 |
| `expansion-check.py` | 扩框的几何结论（离线算） |
| `qa-sweep.py` | 以上全跑一遍 |

**每个工具都必须收敛到「0 未解释」**，不是「0 FAIL」。因为工具会假绿也会假红：
`injection-parity` 最初拿名字在整个 Main 里搜，把 `Reload` / `Begin` 这种撞名的判成「已调用」——
正是那个假绿盖住了一个真 bug（改封面图点 Reload 不生效）。
每一条 missing / not called 都得有一句解释，解释不了的就是真缺口。

---

## 七、结果

- 部署的 `SkinForger.Melon.dll` 与 `src/main/` 的源码**逐字节一致**（`SkinForger.Core.dll` 同样）。
- 旧 Main 不随产品分发，只在私有备份里留作回滚。
- 许可从 GPL-3.0 改为《SkinForger 使用条款》（源码公开 / source-available），见 `LICENSE`。
- 出处与致谢改由 `NOTICE.md` 承担 —— 代码虽然全部重写了，但没有当年那 945 行，这个项目起不来。

重写期间踩到的具体 bug（三类静默契约各自的实例）记在 `docs/development-logs/BUG_LOG.md`；
整个项目的来龙去脉在 `docs/development-logs/开发历程.md`。
