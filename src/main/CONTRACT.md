# 新 Main 必须提供的契约

> 这份文件是**规格书**，不是设计稿。它记录"SkinForger.Core 已经依赖了什么"，新 Main 必须一一对上，
> 换 DLL 时 Core 才不需要改一行。
>
> 来源：Core 模块里 Harmony 补丁的挂点和模块注释（这些注释当初是用 Cecil 读旧 Main 的 IL 反推出来的，
> 由作者本人写下）。**不是**从旧 DLL 的反编译里抄来的实现 —— 那是洁净室禁区。
>
> **状态（2026-11-22）**：迁移已完成，`src/main/` 就是满足这份契约的授权 Main，正在分发中。
> 下文里凡是写「切换前 / 阶段 6 / 旧 Main 仍在部署」的地方，都是迁移期间写下的，留作记录。

## 1. 类型身份（硬约束）

| 项 | 值 | 为什么 |
|---|---|---|
| 类型全名 | `SkinForger.SkinForgerMod` | Core 用 `AccessTools.TypeByName` 按名字找它，48 处 |
| 程序集名 | `SkinForger.Melon` | Core 按**类型名**绑定，但 Host 有一处按程序集名的加载检查（`IsMainBound` 找的就是叫 `SkinForger.Melon` 且引用了 Core 的那个程序集），所以**最终保留了原名** |
| MelonInfo 名称 | `SkinForger` | 日志前缀和玩家看到的名字 |

## 2. 被 Core 钩住的方法（25 个，约 112 处引用）

切换前每一个都必须存在，**签名一致**。签名在移植到对应阶段时，从 Core 的补丁声明里取（Core 的
`HarmonyMethod` 参数类型就是答案），不从反编译快照取。

| 方法 | 引用数 | 谁依赖它 / 行为契约写在哪 |
|---|---:|---|
| `ReloadAssets` | 12 | `OriginalStateRestore`（原版恢复的唯一路径）、`FastReload`、`DumpIsolation` |
| `TryBuildSynthesizedAtlasForSkeleton` | 10 | `CharacterPolygonPackedRectRepair`、`GameplayAtlasWarmReuse`、`AtlasBuildAccelerator` |
| `ReloadSkins` | 9 | `FastReload`、`ReloadFilter`、`PackageMounts` 挂载后刷新 |
| `ApplyRegionPngToAtlas` | 9 | `AtlasBuildAccelerator`（焦点差跳过）、`BattleAtlasEdgeIsolation` |
| `GetTexture` | 6 | `LoadCaches`、`PackageLoaderHooks`（虚拟文件）、`AtlasBuildAccelerator` |
| `SkeletonRendererAwake` | 5 | `R4_12MeshCanvas`、`AtlasOwnershipFix`、`PreviewEnhancements` |
| `SkeletonGraphicAwake` | 5 | 同上（UI 侧：小精灵、结算界面） |
| `DumpCostume` | 5 | `DumpIsolation`、`RoleSkins`、`ThumbnailWithDump` |
| `ApplySkeleton` | 5 | `R4_12MeshCanvas`、`ElfinMeshCanvas` |
| `Scan` | 4 | `RoleSkins`（多皮肤角色的归属判定） |
| `SaveSelection` | 4 | `SkinLibrary`（启用皮肤后切换选择）、`PackageLoaderHooks` |
| `GetExistingRegionPath` | 4 | `PackageLoaderHooks`、`UntouchedRegionRebase` |
| `DrawWindow` | 4 | `SkinPackagesMod`（页签）、`Loc`（中文化作用域）、`WindowInputShield` |
| `CharacterApplyOnEnable` | 4 | `OriginalStateRestore`、`PreviewLifecycleGuard` |
| `ApplyRegionPlacementToAttachment` | 4 | `CharacterPolygonPackedRectRepair`、`OffsetCanvas` |
| `GetRegionAnchor` | 3 | `AutoAnchor`（扩框后锚点不移动的规则） |
| `ExtractAttachment` | 3 | `DumpIsolation`、`EditDetector` |
| `EnsureAtlasCapacity` | 3 | `AtlasBuildAccelerator` |
| `CalculateAlphaBounds` | 3 | `AtlasBuildAccelerator`、`PreviewFrames` |
| `TrySpawnForExtract` | 2 | `RoleSkins`（额外皮肤的 prefab） |
| `GetSelected` | 2 | `PackageLoaderHooks`、`SkinLibraryWindow` |
| `ExtractAll` | 2 | `DumpIsolation` |
| `CreateTransparentTexture` | 2 | `AtlasBuildAccelerator` |
| `DumpCostumeResources` | 1 | `RoleSkins` |
| `CalculateTextureChecksum` | 1 | `AtlasBuildAccelerator`（调试关闭时返回 0） |

## 2.1 契约比方法名更深（2026-11-17 补充）

清点时只数了"被 Harmony 打补丁的方法"，实际还有三类成员被按名字反射：

| 成员 | 形态 | 谁要 |
|---|---|---|
| `SkinForgerMod.RegionAnchor`（嵌套类型） | 属性 `AnchorX` `AnchorY` `DefaultAnchorX` `DefaultAnchorY` `FromSidecar` | `AutoAnchor`——扩框锚点规则靠它区分"玩家改过"和"Dump 默认" |
| `SkinForgerMod.GetRegionAnchor` | `(string regionPath, Attachment, AtlasRegion, string textureName, int ow, int oh)` | 同上 |
| `SkinForger.CharacterReplacement.ReplacementExecutionContext` | 静态 `Current` → 实例 `Preview`（bool） | `R4_12MeshCanvas`、`OriginalStateRestore`、`BattleAtlasEdgeIsolation`、`CharacterPolygonPackedRectRepair` |
| `SkinForgerMod.ShouldUsePremultipliedAlpha(Material)` | 1 参 | `GameplayAtlasWarmReuse` |
| `SkinForgerMod.PremultiplyAlphaInPlace(Texture2D)` | 1 参 | 同上 |
| `SkinForgerMod.GetSynthesizedAtlasKey(string, string, Texture2D)` | 3 参 | 同上 |
| `SkinForgerMod.GameplayScope` | **私有静态字段** | 同上 |
| `SkinForgerMod.TryBuildSynthesizedAtlasForSkeleton` | 5 参 | 同上 + `CharacterPolygonPackedRectRepair` |
| `SkinForgerMod.ApplyRegionPngToAtlas` | **8 参**，含 `out RegionPlacement` 和 `SkinLoadMetrics` 两个 Main 嵌套类型 | 同上 |

反射按名字 + `Public | NonPublic` 取，所以**这些名字一个都不能改**。

## 2.1.1 MainBridge：Core 不再直接读 Main 的私有成员（2026-11-18，B 路线）

试跑暴露的真正问题不是"少了几个方法"，而是**每个模块各自反射进旧 Main 的私有形状**。
修法不是在新 Main 里把那些私有字段长回来，而是在 Core 里加一座桥：

`Modules\MainBridge\MainHost.cs` —— Core 问 Main 的唯一入口，**同时支持两代 Main**
（`MainGeneration.Legacy` / `Authored`，按成员探测，不按 SHA、不按程序集名）：

| MainHost 成员 | 旧 Main | 新 Main |
|---|---|---|
| `TryWindowShown` | 私有字段 `_showMenu` | `SkinForgerMod.WindowShown` |
| `TryWindowRect` | 私有字段 `_windowRect` | `SkinForgerMod.WindowRect` |
| `TryWindowScroll` / `TrySetWindowScroll` | 私有字段 `_scroll` | `SkinForgerMod.WindowScroll` |
| `TextureSettingsHook` | `ApplySkinForgerTextureSettings(Texture2D)` | `ApplyTextureSettings(Texture2D)` |

**纪律：Core 里任何模块都不准再直接读 Main 的私有成员。** 需要新东西，先加到 `MainHost`。

新 Main 这一侧新增的三个成员是**它自己的 API**，不是旧形状的复制：窗口状态是属性而不是私有字段，
`ApplyTextureSettings` 是一个**故意空的扩展点**（`NoInlining`），真正的采样设置留在 `RuntimeAtlas`
里，只把"可供 Core 打补丁的那一个点"公开出来。

### 为什么现在不删那两个加速器

2.2 判定 `AtlasBuildAccelerator`（500 行）和 `GameplayAtlasWarmReuse`（978 行）应当删除 ——
它们补的是旧 Main 的短板（发热、重载慢）。迁移期间不能删：同一份 Core.dll 被新旧两个 Main 共用，
删了会让当时的环境直接退化。

**现在（2026-11-22）**：授权 Main 自己合成图集，这两个模块在启动时会自报
"not needed: the authored Main composes the atlas itself" 并自我禁用 —— 这个行为本来就是正确的，
所以留着也不花代价。要不要真删，是以后的事。

## 2.1.2 四个装载缝（B2，2026-11-18）

Core 不是"读 Main 的值"，而是**给 Main 的方法打补丁**。所以这四个契约必须是新 Main 自己装载路径上
的真实方法，被调用的次数和时机都要对，Core 才能照原样挂上去。**参数名也是契约**——Harmony 按名字
绑定 Core 的补丁。

| 新 Main 的方法 | 签名 | Core 在这里做什么 |
|---|---|---|
| `EnumerateAttachmentContexts` | `(Skeleton skeleton)` → `List<AttachmentContext>` | HexieBlock 在 postfix 里**从这个列表里删掉**和谐部件，所以返回的必须是可变列表，且是下游唯一走的那一个 |
| `GetExistingRegionPath` | `(string skin, Attachment attachment, AtlasRegion region, string textureName)` → `string?` | RoleSkins 改写成皮肤包里的文件、把"别的皮肤未改动的图"退回 null；LoadCaches 记住答案 |
| `GetTexture` | `(string path, string textureName, bool linear)` → `Texture2D?` | OffsetCanvas 在 postfix 里换成**加了透明边的副本**，让越出画布的锚点生效 |
| `ApplySkeleton` | `(Skeleton skeleton, int index, int selected, bool elfin)` → `int` | TouhouCompat 在之后重新同步自定义材质；HexieBlock 在之前学这个骨架的和谐皮肤 |

配套的新类型 `SkinForger.AttachmentContext`：属性 `Attachment` / `Region`（**Core 按名字读，不可改名**）
加 `Atlas` / `Identity`。

**结构上的结果**：`PartCollector` 现在只走骨架**一次**（`Walk`），结果经 `EnumerateAttachmentContexts`
交给 Core 过滤，Dump 与替换路径都从这同一份已过滤的列表出发（`PartsOf`）。`RuntimeAtlas.RegionsOf`
不再自己重走一遍骨架。`RuntimeAtlas.Apply` 多一个 `skinDirectory` 参数：给了它就走
`GetExistingRegionPath` 这条缝，没给（Preview）就用调用方已解析好的 `images`。

`ApplySkeleton` 保持四个参数，所以渲染器实例 id 走 `SkinForgerMod.ApplyingInstanceId`（由
`SkeletonHooks.ApplyTo` 在调用前后设置）而不是挤进签名。

## 2.1.3 契约清点工具（`Analysis\contract-parity.py`）

不要再靠人数一遍。这个脚本扫 Core 各模块对 host 的查找（名字 + Core 明说的参数个数），再扫新 Main
的声明，输出三张表：**缺失 / 参数个数不符 / 已满足**。2026-11-18 的结果：**31 满足，37 缺失，
1 个参数个数不符**。

它的局限要记住：**它看不出参数名不一致**。而参数名正是 Harmony 绑定用的东西——下面这条就是它漏掉、
靠人读出来的。

### 已知陷阱：`GetSkinLabel` 同名不同形

新 Main 现有 `GetSkinLabel(int index, int position, bool elfin)`；Core 的 `CreatorWindowHooks` 用
`AccessTools.DeclaredMethod(host, "GetSkinLabel")`（**不带参数个数**）找它，然后挂
`LabelPostfix(string costume, string skinPath, ref string __result)`。它会找到新 Main 那个三参数的，
按名字绑 `costume` / `skinPath` 失败 → 打补丁时抛异常 → `CreatorWindowHooks` 整块挂掉。
**下次试跑前必须处理**：要么把新 Main 自己那个改名，要么按 `(costume, skinPath)` 实现契约那一个。

### 待办：选择状态这一族（B2b）

清点查出 `SaveSelection` 的真实契约是 **2 个参数**：`SaveSelection(Dictionary<int,int> selection,
string path)`——"把这份选择写进这个文件"。FastReload 和 DumpIsolation 都是这么 `Invoke` 的。
新 Main 现在是 `SaveSelection(int index, int position, bool elfin)`，对不上。它和另外四个成员是一族，
要一起补（Core 的 Skin Library / 打包加载 / Dump 隔离都依赖它们）：

| 成员 | 形态 | 谁要 |
|---|---|---|
| `SaveSelection` | `(Dictionary<int,int>, string)` | FastReload、DumpIsolation、PackageLoaderHooks、CreatorWindowHooks |
| `SelectedCharacters` / `SelectedElfins` | `Dictionary<int,int>` 静态字段，Core **会写** | 同上 |
| `_savedCharactersPath` / `_savedElfinsPath` | `string` 静态字段 | 同上 |

## 2.2 哪些 Core 模块在新 Main 下会失效（而且应该失效）

`GameplayAtlasWarmReuse` 在契约缺失时打 `disabled reason=host-contract-unavailable` 后自我禁用，
`AutoAnchor` 同样会打一行警告后放弃。这给了判断空间：

| Core 模块 | 行数 | 在新 Main 下 |
|---|---:|---|
| `AtlasBuildAccelerator` | 500 | **应当删除**。它的全部工作是绕开旧 Main 的慢路径（每写一个区域就算一次全图集校验和、每个放大部件扫两遍 alpha 边界），新 Main 的 `AtlasComposer` 一次合成，这些慢路径根本不存在 |
| `GameplayAtlasWarmReuse` | 978 | **应当删除**，同上；缺契约时它自己会禁用 |
| `R4_12MeshCanvas` | 2792 | **必须保留**——扩框 |
| `CharacterPolygonPackedRectRepair` | 1131 | **多半可删**：它为每张替换图分配独立矩形，而新 Main 的 `AtlasComposer` 本来就这么做（规则 1）。待实机确认 |
| `AutoAnchor` / `OffsetCanvas` / `CanvasMatch` | ~900 | 保留——扩框的锚点规则 |
| `OriginalStateRestore` | 950 | 保留——回到原版 |
| `SkinPackages` | 9906 | 保留——皮肤包、皮肤库、导出、文字、声音 |
| `BattleAtlasEdgeIsolation` / `ElfinMeshCanvas` / `ElfinFreshPackedRect` | ~960 | 保留 |

也就是说：**切割旧 Main 顺带还能让 Core 瘦掉 1500~2600 行**，因为那些模块是为了补旧 Main 的短板而存在的。

## 3. Main 里内嵌的运行时类型（12 个）

Core 也按名字找这些类型。**其中 11 个已经有授权源码**（私有开发目录 里的 `Runtime\` 目录），
新 Main 直接把它们编译进来即可，不需要重写：

| 类型 | 源码位置 |
|---|---|
| `SkinForger.Preview.PreviewManager` | `PreviewMvp/Runtime/PreviewManager.cs` |
| `SkinForger.CharacterMeshGeometry.CharacterMeshGeometry` | `CharacterMeshGeometry/Runtime/` |
| `SkinForger.ReplacedBackup` | `ReplacedBackup/Runtime/` |
| `SkinForger.DumpSystem.DumpSessionManager` | `DumpSystemFix/Runtime/` |
| `SkinForger.AtlasOwnershipFix.AtlasOwnershipManager` | `AtlasOwnershipFix/Runtime/` |
| `SkinForger.SelectionThumbnail.SelectionThumbnailManager` | `SelectionThumbnail/Runtime/` |
| `SkinForger.LoadingPaths` / `SkinForger.CustomLoadingPicManager` / `SkinForger.LoadingConfig` | `CustomLoadingPic/Runtime/` |
| `SkinForger.FpsProtection.FpsProtectionConfig` | `FpsLimiterPatch/Runtime/` |
| `SkinForger.CharacterReplacement.GameplayReplacementPublicationRegistry` | `PreviewSharedCore/Runtime/` |
| `SkinForger.Preview.PreviewSharedCore`（Gate / Decision） | `PreviewSharedCore/Runtime/` |
| `SkinForger.CharacterReplacement.ReplacementExecutionContext` | **已重写**：`Unity/ReplacementExecution.cs`（2026-11-17） |

## 4. 冻结的磁盘格式（已在 `Pure\` 实现）

玩家和皮肤作者的现有文件必须继续可用。已实现并由 `Tests\` 对真实文件验证：

| 格式 | 实现 | 要点 |
|---|---|---|
| `manifest.json` | `Pure/RegionManifest.cs` | camelCase；`file` 用反斜杠；部件身份 = `atlas\|region\|attachment` |
| 每图锚点 sidecar（v2） | `Pure/RegionAnchorFile.cs` | 混合大小写是故意的：v2 核心字段 camelCase，后加的 `Scale`/`MovementAmplitude`/`X`/`Y`/`Rotate` 为 PascalCase；`origin` 恒为 `top-left` |
| `Saved.json` / `SavedElfin.json` | `Pure/SelectionStore.cs` | `{ "角色序号": 位置 }`，`-1` = 原版；损坏时退回"无选择"而不是报错 |
| 目录布局 | `Pure/MainLayout.cs` | 与 Core 的 `SkinForgerPaths` 一致 |
| 像素规则 | `Pure/PixelMath.cs` | 反预乘、alpha 边界、裁剪、90° 旋转契约 |

## 5. 切换检查表（阶段 6 用）

- [x] 契约方法存在且签名一致（见第 2 节、2.1 节；加速器要的那组故意不提供，见 2.2）
- [x] **12 个内嵌类型全部可解析**：11 个由各自的 `Runtime\` 授权源码编译进来（2026-11-17），
      第 12 个 `ReplacementExecutionContext` 为重写
- [x] AtlasOwnership 按渲染器绑定已接通（`Prepare` / `ResolveSource` / `RegisterRuntime` / `Record` / `Bind`）
- [ ] Host 里的 `"SkinForger.Melon"` 加载检查改为新程序集名
- [ ] `MainGuard` 绑定改为新 Main 内部直接调用（不再需要 Cecil）
- [ ] 旧 `SkinForger.Melon.dll` 移出 `Mods\`，留在 `DeploymentBackups\`
- [ ] BUG_LOG 全量复查
- [ ] 确认无上游表达残留 → 再改 `LICENSE` / `NOTICE.md` / `README`
