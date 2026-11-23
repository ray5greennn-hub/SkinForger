# SkinForger Bug 记录

开发过程中实际遇到的 70 个 bug，最新的在最前面。每条只写三件事：**现象**、**原因**、**解法**。

公开它的原因：这些坑大多不是 SkinForger 独有的。Spine 纸偶、IL2CPP 下的反射绑定、
图集重建后的 UV、uGUI 的遮罩与字体度量 —— 做同类游戏 Mod 的人会撞上同样的东西。
而一个 bug 真正值钱的部分不是「修好了」，是「为什么一开始会错」。

> 这是历史记录，不是说明书：后面的条目会推翻前面的。要看现在怎么回事，请读源码。
> 文件路径按本仓库的布局写。

---

## BUG-072　换字体后主页 "ENTER!" 的 E 被图标盖住；技能描述不换字体

**现象**　换上自定义字体后，主界面 "ENTER!" 的首字母伸到左边图标下面；技能描述这类文字根本没换字体。

**原因**　三件事叠在一起：

1. 那行字的框宽是 0，`horizontalOverflow=Wrap`。框宽 0 时不换行，文字从中心向两边展开，
   新字体更宽就两头溢出。上一版只给 `horizontalOverflow=Overflow` 的文字缩字号，漏掉了这种。
2. 游戏会在面板显示时**重新设置**字体，所以只在 `OnEnable` 时换一次不够（技能描述就是这种）。
3. TextMeshPro 这边：Windows 已安装的字体在游戏里没有字体数据，
   `FontEngine.LoadFontFace(Font)` 失败，`CreateFontAsset` 返回 null。

**解法**
- 单行文字（`Overflow`，或框宽 < 1）换字体后变宽时，按宽度比例缩小字号，切回时恢复（UGUI Text 和 TMP 都做）。
- 给 `Text.font` / `TMP_Text.font` 的 setter 加 postfix：游戏之后再设字体也换成 SF 的；另外每 3 秒补查一遍屏幕上的文字。
- 给 `FontEngine.LoadFontFace(Font…)` 加 prefix，改从字体文件读字形，TMP 字体就能建起来（2048 多图集）。
- 带描边属性 shader 的文字保留原字体。

**涉及**　`src/core/Modules/SkinPackages/Text/FontHooks.cs`、`TextPage.cs`、`FontFile.cs`

---

## BUG-071　给小精灵改英文名后，她的皮肤失效（封面回到原版，角色显示成碎块）

**现象**　在 Text 页把某个小精灵的英文名改掉之后，选择界面的封面变回原版，角色显示为碎块，待机界面同样。

**原因**　Main 用 `GetCostumeName(index, elfin)` 从 `elfin_English` / `character_English` 读英文名，
拿它去找 `Images\<名字>` 文件夹 —— 而文字替换写入的正好是这张缓存表。
名字一改，皮肤文件夹就找不到了。碎块是「改过的网格 UV」配上「原版图集」的结果。

**教训**　做文字替换时没检查名字字段是否同时被 SF 自己当作标识使用。

**解法**　`TextHooks` 给 `SkinForgerMod.GetCostumeName` 加 postfix（`Priority.First`），
返回文字替换写入**之前**的英文原名。玩家看到的名字仍然是改过的。

**涉及**　`src/core/Modules/SkinPackages/Text/TextHooks.cs`

---

## BUG-070　Text 页的 Loading Tips 一直是空的，Dump 之后也是空的

**现象**　Text 页筛选 "Loading Tips" 显示 0 条，点 Dump Text 仍然是 0。

**原因**　游戏在 SF 启动**之前**就读完了加载提示（`DBConfigLoadingTips`），
`LoadConfigFile` / `Deserialize` 钩子装上时已经错过了；按名字找含 "tip" 的表也找不到。

**解法**　`TextHooks.DiscoverTips`：每 5 秒（找到为止）调用
`ConfigManager.GetConfigObject(typeof DBConfigLoadingTips, -1)`，把它 `m_LocalDic` 里每种语言的
`DBConfigLocalLoadingTips` 加入可替换的表，用文件名推出表名（`loading_tips_ChineseS` → `loading_tips`）。

**涉及**　`src/core/Modules/SkinPackages/Text/TextHooks.cs`

---

## BUG-069　在 SF 输入框里打字时，Enter / 空格仍会触发游戏本体

**现象**　在 SF 的搜索框里按 Enter 或空格，游戏界面跟着响应（取消小精灵选择、按下界面按钮）。

**原因**　前三版一层层加拦截全都没用：关 Rewired 键盘控制器、暂停 Rewired 玩家、
拦截 `UnityEngine.Input` 托管包装、NativeHook 加全部 Rewired 按键查询（日志显示每次打字拦下十几万次）——
因为游戏**还用了 Unity 新输入系统**（`Unity.InputSystem`：PeroTools2 的 `InputActionAsset`、
uGUI 的 `InputSystemUIInputModule`），前三版一条都没碰到这条路。

**教训**　前两版没先查清游戏到底用哪些输入方式就开始加拦截，白费两轮。

**解法**　`TypingGuard` 第 5 层：打字时 `InputSystem.DisableDevice(Keyboard.current)`，结束时 Enable；前几层保留。

**涉及**　`src/core/Modules/SkinPackages/TypingGuard.cs`、`SkinPackagesMod.cs`、`WindowInputShield/`

---

## BUG-068　Dump 缺 68 张表情/动作部件；缺图的部件在游戏里显示成碎块

**现象**　新 Dump 出来的某个角色只有 129 张 PNG，而完整的应有 197 张，缺的全是**动画切换时才出现**的部件
（`angry_*`、`shy_*`、`yawn_*`、`sweat`、`face_2~6`、`fx_atk_1~11` 等）。
游戏里这些缺图的部件显示为不规则碎片。

**原因**　两层：

1. Main 的 `ExtractSkeleton` 只导出每个插槽「当前显示」的那个部件。
2. 更要紧的：Main 合成图集时 `EnsureAtlasCapacity` 把图集从 2048 行扩到 7711 行，
   原像素留在原来的（自底向上的）行上，但**只给放入了 PNG 的部件重算 UV**。
   缺图部件的 UV 仍按 2048 行归一化，在 7711 行的图集上读到了别处 → 碎块。
   以前全套 197 张时每个部件都有 PNG，所以从没暴露。Preview 早有 `RebaseUntouched` 处理这件事，Gameplay 没有。

**教训**　上一轮核查只比较了「导出的图是否正确」，没有反向检查「缺了哪些」。

**解法**
- `DumpIsolation.ExtractSkeletonPostfix`：遍历骨骼的 `defaultSkin` 与所有 skin，
  对当前未显示的每个 Mesh / Region 部件也调用 `ExtractAttachment`。已有文件跳过。
- `UntouchedRegionRebase`（`TryBuildSynthesizedAtlasForSkeleton` 的 prefix + finalizer）：
  构建前记录每个 region 的打包矩形和 UV；构建后，对 UV 没被改过、且页面尺寸与最终图集不同的 region，
  按最终图集尺寸重算 x/y/u/v 并刷新部件 UV，**几何不变**。只作用于角色 Gameplay，不碰 Preview 和小精灵。

**涉及**　`src/core/Modules/SkinPackages/DumpIsolation.cs`、`UntouchedRegionRebase.cs`、
`Modules/OriginalStateRestore/OriginalStateRestoreMod.cs`

---

## BUG-067　游戏内 Victory 仍无扩框；Preview 的部分部件仍无扩框；Preview Fail 黑屏

**原因**
1. 游戏内 Victory 的所有目标都被判成 `game-atlas`：`SkeletonGraphic` 的替换图集放在 `overrideTexture` 上，
   材质仍是游戏原图集，而扩框模块是**按材质贴图**判断的。
2. Preview 里测得的纹素尺寸面积和 PNG 相等但长宽比不同 ——
   Preview 自己写的 Mesh UV 与 Main 在 Preview 图集里放置 PNG 的位置不一致。
3. Fail 黑屏：Main 只按**第 0 帧**对准镜头，而 Fail 的 "in" 是入场动画，角色随后离开了画面。

**解法**
1. `SkeletonGraphic` 改用 `graphic.mainTexture`（即 `overrideTexture`）判断。
2. 记录 Main 为每个 `MeshAttachment` 写入的 placement（PackedRect、Degrees），
   用 gameplay 中 UV 已验证的 Mesh 自动校准方向约定（每种 Degrees 在 16 种候选里选误差 ≤ 0.75 texel 的一种）。
   Preview 尺寸不同时改用「本图集的 placement 矩形 + 校准约定」建立映射。
3. 结算上下文在整段动画上采样 25 帧，按 bounds 并集对准镜头。

---

## BUG-066　结算动画用了 Spine 裁剪（clipping），扩框模块对裁剪帧直接放弃

**现象**　Victory 仍然没有扩框；Preview 的 Victory 每帧都被拒绝（`active-clipping-fail-closed`，250 次）。

**原因**　带裁剪的 `SkeletonGraphic` 走 `AddSubmesh` 路径，不会调用 `BuildMeshWithArrays`，
所以上一版预留空间的那个点从未触发。

**解法**　扩框模块新增**裁剪帧模式**：不预留空间，在 `FillVertexData` 之前按「附件 UV 原样复制」
在本帧缓冲里定位目标，并在新鲜的三角形缓冲里校验该目标的三角形区段；
补充顶点追加在末尾，补充三角形插在目标自身三角形之后。处于裁剪范围内的目标保持原样。
renderer 和 graphic 两条路径都支持。

另外：Preview 的结算上下文若材质用的是 `SkeletonGraphic`/UI 着色器，换成 `Spine/Skeleton`（保留 straight alpha 标志）。

---

## BUG-065　Preview 里某个部件没扩框，但实际战斗里正常

**原因**　Preview 图集尺寸和 gameplay 不同，所以 gameplay 那边已验证的 UV 用不上。
而在 Preview 模式下，打包矩形修正模块只对「PNG 尺寸等于 region 原始尺寸」的 Mesh 使用完整画布 ——
扩过框的部件不符合这个条件，于是被放进了多边形打包矩形里。

**解法**　Preview 中所有 `MeshAttachment` 改用与 Gameplay 相同的完整画布规则（任何 PNG 尺寸）。
Preview 的 `RegionAttachment` 保持原有已冻结的路径不变。

---

## BUG-064　Victory / Fail 结算界面的角色没有扩框

**原因**　扩框模块只从 `SkeletonRenderer.LateUpdate` 发现渲染器，而结算角色是 UI Spine（`SkeletonGraphic`）。
Main 的 Preview 也因为同样原因跳过了 `victoryShow` / `failShow`（`SelectPrimaryRenderer` 只找 `SkeletonRenderer`）。
结果：Main 的几何处理被跳过，补充几何又没生效，部件只显示原 Mesh 轮廓内的部分。

**解法**　会话对象新增 `SkeletonGraphic` 构造：
`SkeletonGraphic.UpdateMesh` 前缀负责发现会话（只处理 Main 几何被跳过的角色图集，排除小精灵），
`MeshGenerator.BuildMeshWithArrays` 前缀在该 graphic 的 `UpdateMesh` 内做预留。

---

## BUG-063　增量 Reload 改一张图时回退成完整重载（`placement-changed:PackedRect`）

**原因**　首次构建后，Main 已用 `ApplyRegionPlacementToRegion` 把 `AtlasRegion` 改成运行时图集上的位置。
第二次调用 `ApplyRegionPngToAtlas` 时，打包矩形是从**已改过的** region 算出来的，所以和旧值不同。
回退本身安全，只是慢。

**解法**　在 `ApplyRegionPlacementToRegion` 前缀里，每个 region 第一次被改写前记录原始属性；
热应用时临时恢复这些原值，调用结束后再写回。仍不一致就把新旧值打进日志。

---

## BUG-062　Dump 之后点 Root、点 Reload、改贴图再 Reload，全都没有任何效果（严重）

**原因**　四件事叠在一起：
- 选择索引指向的文件夹被删过，重新 Dump 后同一个索引指向新建的文件夹，但**索引本身没变**；
- Main 的 Root 按钮在「已经选中」时什么都不做（IL 里 `selection==i` 直接跳过）；
- 自动选择的判断是 `previous==position`，认为无需改动；
- 增量 Reload 发现该皮肤不在任何活动图集的 key 上，判为「未使用」，于是跳过。

**解法**　记录「上次真正应用时，每个角色/小精灵解析出的皮肤路径」（首次初始化和每次 `ReloadAssets` 时记）。
点 Reload 时只要当前解析结果和记录不同就走完整重载；
并转译 `DrawSelectionList`，在「按钮已选中 → 跳过」那个分支里插入 `ReapplyIfStale()`。

---

## BUG-061　Dump 后还要手动点 Root 才用得上导出的贴图

**解法**　给 `DumpCostume` 加 postfix，记录 `(index, elfin, GetDumpSkinDirectory)`；
下次点 Reload 时按 Root 按钮同样的方式设置选择并保存，再走完整重载。只处理单个角色的 Dump，Dump All 不自动选择。

**同版本另一处**　选择文件 `Saved*.json` 就在皮肤目录里，点 Root/Original 后它会变 ——
上一版因此误判「非 PNG 文件改了」而回退完整重载，已从内容比较里排除。
备份也不再走整树遍历（只哈希 1 个文件也要 391 ms），改成只对大小或修改时间变了的文件做备份。

---

## BUG-060　Dump 出的 PNG 有一圈描边（重新导入后部件边缘出现光圈）

**原因**　Main 的 `ExtractAttachment` 从图集可读副本裁出之后直接写 PNG，**没有反预乘**；
导入时若 `ShouldUsePremultipliedAlpha` 为真又预乘一次 —— 半透明边缘被乘了两次。

**解法**　给 `ExtractAttachment` 加前缀，用导入时同样的判断；
是 PMA 就在写 PNG 前反预乘：`a=0` 时 rgb 置 0，否则 `rgb = round(c*255/a)`。

---

## BUG-059　改了图点 Reload 没变化（不是缺陷）

Dump 只把目录登记为 Root 皮肤，当前选的仍是 Original，所以不会应用。需要先点那个角色的 Root。
日志里写得清楚：`selected=-1 ... pathSource=registered-not-selected`。

---

## BUG-058　点一次 Reload 要 5~7 秒，有卡死风险（目标 < 0.5 秒）

**原因**　备份阶段约 1.1 秒（每个 PNG 都做一次完整哈希）＋ `ReloadAssets` 约 5.8 秒
（销毁并重建所有已选皮肤的全部图集，单个 0.5~0.8 秒）。图集热复用那一轮命中 0 次 ——
缓存上限 112 MB 放不下两个 50 MB 级别的图集。

**解法**（FastReload）
1. 转译 Reload 按钮：备份完成后先尝试增量路径，成功就跳过 Main 的完整重载。
2. 增量路径的条件：没有新增/删除 PNG 或 JSON、没有 JSON 改动、改动的 PNG 尺寸不变、
   受影响的活动图集都有记录。满足时用 Main 自己的 `ApplyRegionPngToAtlas` 把改动的 PNG
   写回原图集的原矩形（分配器返回旧矩形，不扩图集）；重写后的 placement 除 alpha bounds 外
   有任何值不同就回退完整重载。
3. 备份：文件大小和修改时间都没变、且记录的哈希与缓存一致时跳过哈希。
4. Shift+Reload 强制完整重载。

**已知限制**　新增/删除文件、改 JSON、改变 PNG 尺寸时仍走完整重载。

---

## BUG-053　Dump 任意角色后，当前角色贴图回到原版、小精灵贴图消失（Preview 仍正常）

**原因**　Main 的 Dump 按钮之后调用 `RequestIndexOnlyRefresh(); ReloadSkins()`，
而 `ReloadSkins` 只重建皮肤目录索引，不清缓存也不重新应用；
原状恢复模块的 `ReloadSkins` 前缀却**无条件**把活动骨架还原成原版，之后没有任何重新应用。
Preview 用独立图集，所以不受影响。

**第一版修完更糟**：贴图严重错乱、小精灵依旧消失。第二个根因是
Main 的 `ReloadSkins` 在检查 index-only 标志**之前**，无条件调用了
`GameplayReplacementPublicationRegistry.InvalidateAll()` 和 `AtlasOwnershipManager.InvalidateAll()`，
后者用 `Object.Destroy` 销毁运行时替换图集 —— 正常 Reload 会在随后的 `ReloadAssets` 里重建，
Dump 之后没有重建，于是正在用的贴图被销毁了。小精灵消失也是这个原因。

**解法**　用转译器把这两个 `InvalidateAll` 换成带守卫的版本：index-only 刷新时跳过，否则照常调用。

---

## BUG-054　Preview 里选了 Animation 之后下拉列表自动折叠

**解法**　和 Character 列表一样：选中项变化且之前处于展开状态时，重新把展开标志设回 true。

---

## BUG-055　鼠标在黑色预览图外面（左右空白处）滚轮仍然会缩放

**原因**　Main 用 `ScaleMode.ScaleToFit` 绘制，黑色图像是居中的等比矩形，而上一版用整个布局矩形判断。

**解法**　按 `RenderTexture` 宽高比算出实际显示矩形，命中判断和缩放中心都改用它。

---

## BUG-056　列表展开后 Play / Pause / Speed / Close 被挤出固定大小的 Preview 窗口

**解法**　`DrawPreviewWindow` 外层套一个纵向 ScrollView（前缀 Begin，finalizer End），
提供滚动条和滚轮（列表或图像已消耗滚轮时不滚窗口）；标题栏另调用一次 `DragWindow`。

---

## BUG-057　Dump 过程没有逐项进度，只有结束时一行汇总

**解法**　钩住 Dump 的开始/每个角色/每张 PNG/结束，依次输出进度行。只加日志，不改行为。

---

## BUG-052　Preview 的下拉列表无法用滚轮或滚动条滚动

**现象**　某些角色有 31 个动画，下方的项根本选不到。

**原因**　Main 那三处用 `GUILayout.BeginScrollView` 并正确回写滚动位置（IL 读过），代码层面没问题；
IL2CPP 下 IMGUI 滚动失效的根因**没查明**。

**解法（绕过）**　列表展开时由模块接管滚轮事件，直接调整滚动位置并消耗事件。滚动条拖动没单独修。

---

## BUG-051　每写一个区域都对整张图集算一次只用于调试日志的校验和

**现象**　图集构建慢的主要原因之一。

**原因**　`ApplyRegionPngToAtlas` 每次区域写入后执行
`DebugLog("Atlas write complete: checksum=" + CalculateTextureChecksum(targetAtlas))` ——
**插值字符串在调用前就求值了**，而 `DebugLog` 只在调试开关打开时输出。
校验和对整张图集（例如 512x7711）做 `GetPixels` + 每像素 4 次读取。
另外还有：对已经全零的新数组逐像素写零、在循环条件里每像素调用
`Texture.get_width` / `get_height` 这种原生方法。

**解法**　以 `Priority.Last` 前缀提供逐位等价的快速路径：调试关闭时校验和直接返回 0；
去掉零填充循环；预乘与旋转改用 `Span`；alpha 边界用转译器把宽高缓存下来。

---

## BUG-050　Preview 里依赖运行时 Spine 皮肤的角色缺部件，另一个角色误选了和谐皮肤

**原因**　游戏在**战斗中运行时**设置 Spine 皮肤（`ChangeInitialSkin`、`character_skin_normal|mad`），
而 Preview 用 `NewSkeletonAnimationGameObject` 只带默认皮肤。
上一版按「补齐数」挑皮肤，于是挑错了。

**解法**　先采用模板的 `initialSkinName`，否则优先 `normal`，再按补齐数；
新增 Form 行，可在同一角色的多个形态之间切换。

---

## BUG-049　Original 与 Root 切换时 Preview 角色消失且无法恢复

**原因**　Main 的 Original / Root 按钮只执行 `SaveSelection` + `ReloadAssets`，
**不调用** `PreviewManager.BeforeHostReload` / `AfterHostReload`（只有 Reload 按钮调用）。
启用图集复用后旧图集被回收，Preview 借用的纹理就失效了。

**解法**　在每次 `ReloadAssets` 前后调用 Main 自带的这对方法（Preview 关闭时是 no-op）。

---

## BUG-048　Preview 贴图错误（手缺失、部件错乱），且不显示扩框/完整画布

**原因**
1. 旋转修复对「旋转存放」的 Mesh 区域算出了错误 UV（实测纹素尺寸与 PNG 尺寸不符）。
2. 模块按**原生地址**识别渲染器，而 Preview 重建时地址会被复用；
   Preview 克隆的 `SkeletonDataAsset` 名字带 `(Clone)`，匹配不上任何皮肤目录，**静默**没建会话。

**解法**　Preview 的 Mesh 改用 Gameplay 里同名部件已验证的 UV 映射（两侧借用同一图集）；
按 Unity 实例 ID 识别渲染器；名称去掉 `(Clone)` 并优先使用共享的 `skeletonJSON` 名。

---

## BUG-047　角色图集从不复用，每次切换 8~15 秒

已修。剩余的冷构建开销由 BUG-051 处理。

---

## BUG-046　拖动 SF 窗口时点击穿透到游戏角色

**原因**　IMGUI 只吞自己的事件，uGUI 的 `EventSystem` 仍会处理同一次点击。

**解法**　光标位于打开的 SF 窗口上方、或从窗口内开始拖动时，跳过该帧的 `EventSystem.Update`。

**涉及**　`src/core/Modules/WindowInputShield/`

---

## BUG-045　点击 Original 后角色贴图错乱

**原因**　原状恢复模块用**哈希白名单**绑定旧版 Main，在新 Main 下一直处于禁用状态 —— 悄无声息地什么都没做。

**解法**　核对 7 个依赖方法的签名完全一致后，只扩展白名单，逻辑没改。

> 这类「按哈希绑定」的开关是沉默失效的典型：功能在，但没人启动它。

---

## BUG-044　角色 Mesh 扩框后原生变形异常（锯齿、拉扯、碎块）

**现象**　核心功能坏掉：扩了框的部件在动画里被拉扯、出现锯齿和碎块。

**原因**　当时的做法直接改写 `mesh.vertices` / `regionUVs`，破坏了「顶点 — 图像 — 权重」的对应关系；
而且对尺寸没变的 PNG 也一样改写。

**解法**　改成**不碰任何 Attachment**：每帧在 Spine 的瞬时缓冲里追加覆盖「画布减 Mesh」的补充几何并重映射 UV，
同时跳过 Main 的几何改写。这就是现在唯一正确的扩框机制。

**完整原理**　见 `docs/expansion/扩框机制说明.txt`

---

## BUG-040　按隐藏键关掉 SF 之后，Preview 的克隆体和动作继续驻留在场景里

**原因**　隐藏键那条路只切换 `_showMenu` 标志，而 Preview 的克隆体和相机根节点是 `DontDestroyOnLoad`，
只有 `PreviewManager.Close()` 会真正销毁它们。

**解法**　单独一个生命周期守卫：菜单隐藏、场景切换或卸载时，若 Preview 还是打开状态，
就调用 Main 自带的 `Close()`。不做全局对象搜索，不调 `StopAllCoroutines()`，不销毁 Gameplay 资源。

---

## BUG-041　完整画布的通用入口是通的，但部分加权 Mesh 被拓扑校验拒绝

**现象**　Gameplay 的闸门对所有有效的角色 `MeshAttachment` / `RegionAttachment` 用统一判定，
没有任何角色名或贴图名白名单（已加回归断言）。但运行日志里仍有十来个部件被**冻结的拓扑校验**拒绝。

**边界**　这些被拒绝的不算成功，也不许绕过校验。记在这里是为了别把「入口是通的」当成「覆盖是全的」。

---

## BUG-037　Battle Preview 因为「共享同一原始 Region 的不同替换文件」而整次回退

**原因**　旧逻辑以**原始 Region** 为键，错误地要求所有共享该 Region 的 attachment 解析到同一个替换文件；
两个 attachment 不同但共享原始 Region 时直接抛错，替换构建在读取武器之前就终止了。

**解法**　改成逐 attachment 验证文件分类并收集不同的 placement，用 attachment 覆盖率验证完整性。
没有改 Gameplay 缓存、图集所有权、UV/几何或回退路径。

---

## BUG-038　图集快照的容量上限导致结算/战斗生命周期缓存抖动

**原因**　四个常用快照合计约 104 MB，高于当时 96 MiB 的上限，于是反复冷构建；
一次 5.1 秒的卡顿正好对齐一次冷构建。

**解法**　上限提到 112 MiB，并把快照里逐 `Color32` 的循环换成 `Span.CopyTo`；
身份、指纹失效、LRU、条目上限、Preview 排除都不变。

**边界**　首次冷构建仍然存在。

---

## BUG-039　战斗贴图边缘出现白色颗粒（未查明）

**排除了的**：`char_1_rock` 的 57 张 PNG 里 449,087 个全透明像素 RGB 全为 0，
所以不是普遍的「透明像素带白色 RGB」污染；半透明的白像素主要属于 `fx_atk_*` 这类特效资源。

**没有定论**：还分不清是邻图采样、过期绑定，还是走了不同的资源路径。
这一轮**不改** Dump、PMA、双线性、打包矩形、UV、图集或冻结的几何 ——
下一步只应针对一个能指认的具体部件做一次绑定取证，再动共享根因。

> 留着这条是因为「查不出来就先别改」本身是个结论。

---

## BUG-033　「读贴图」和「扩几何」被混成同一个问题，导致超大 PNG 被原始 Mesh 裁掉

**现象**　超大的替换 PNG 能进入替换链路，但最终显示仍被原始 `MeshAttachment` 的几何裁切。

**原因**　`SetRegion()` / 改 Region 主要影响 `RendererObject`、Region UV 和采样信息，
**不会**改变 `MeshAttachment` 的几何顶点。贴图和 region 都对了，几何还是原始范围，图当然被裁。

**解法**　正式拆成两条流水线：
保留已经正确的「PNG → Texture → 替换 Region → attachment 贴图/region」读取链路；
扩框只在启用扩展模式时处理 `MeshAttachment` 的几何。普通角色保持原始 Mesh 与原始几何。
扩框角色必须保留原始拓扑、triangles、bones、weights、deform 和 ParentMesh 继承关系，**只重算几何**。

**被否掉的做法**　图集 dump / 重建 / 分析；靠重新合成图集解决扩框；只靠 `SetRegion()` 解决几何扩展；
把 `MeshAttachment` 硬转成普通 `RegionAttachment`；大规模改全部 attachment 的 UV；统一成 `0..1` UV；
破坏原始 Material / Region / UV 对应关系。

**分流标准**　用纯色或强对比的超大 PNG 测：图本身显示错误 → 查贴图/Region/UV/Material；
图读对了但被截断 → 查几何。

---

## BUG-034　Preview 大规模 UV 归一化破坏了 Attachment / Region / Material 的对应关系

**现象**　本来正常的 Preview 又出现严重碎片化、错误采样和白毛边。

**原因**　根因不是读图，是**对应关系**：把大量 attachment 改成私有整图 region + `0..1` UV 之后，
最终 `SkeletonRenderer` 仍然只有一个 material，attachment 的数据阶段和 renderer 最终的 material/贴图对不上。

**解法**　回滚到已经实机验证过的那一版，只保留两个 `MeshAttachment` 的 Preview 私有替换路径，
这条路径连同 Material / 贴图 / UV / 图集 / Renderer 的契约一起冻结。

**被否掉的做法**　把所有 attachment 改成私有整图 region；统一 `0..1` UV；
不验证最终 material 绑定就铺到 81 个 attachment；靠重写读图来掩盖对应关系的错误。

**边界**　静态资源检查覆盖 6 个角色、24 个上下文、1201 张 PNG 且 0 失败 ——
但这**不等于**全 attachment 的运行时渲染已验收。

---

## BUG-035　加权 MeshAttachment 很容易被错误降级成普通 RegionAttachment

**原因**　两者的数据模型不同。加权 `MeshAttachment` 可能带 triangles、bones、weights、
加权顶点、deform 关系和 ParentMesh 继承关系，普通 `RegionAttachment` 承载不了这些。
为了显示超大 PNG 就简单建一个 `RegionAttachment` 顶替，会把 Spine 动画依赖的骨骼绑定结构整个丢掉。

**原则**　保留 attachment 类型与原始拓扑，只在扩展模式下重算几何。
不得因为 PNG 变大就重建普通 quad，不得为了规避几何问题丢掉 bones / weights / deform。

---

## BUG-029　完整画布替换复用了多边形打包矩形，导致贴图互相覆盖

**现象**　某个部件显示不完整，角色周围出现颜色和另一个部件相似的小色块。
调 winding、hull、triangle 都没有任何变化。

**原因**　旧逻辑只在替换 PNG **大于**原尺寸时申请扩展矩形；同尺寸的 Mesh 会复用原来的多边形打包矩形。
而 Spine 原图集的多边形打包**允许 Mesh 的外接矩形互相相交** ——
完整画布替换却向整个矩形写像素，于是后写的 region 覆盖了先写的。
已确认三个部件的旧打包矩形存在实际交叠。

**为什么之前改几何没用**　问题发生在写像素的目标矩形那一层，早于最终的 Mesh winding / hull / triangles；
改拓扑救不回已经被别人覆盖掉的像素。

**解法**　任何 Spine `MeshAttachment` 的替换都统一申请独立的扩展矩形
（分配器给每张运行时贴图独立的状态、NextY 和 padding，保证互不重叠），再照原路写像素、更新 Region 和 UV。
`RegionAttachment` 同尺寸继续复用安全的矩形。

**范围**　没有任何角色名、部件名或索引的特判；角色和小精灵共用同一规则。

---

## BUG-028　Reload 每次都全量备份 + 全局刷新，改一张图要等 8~10 秒

**基线**　备份约 5.78 秒（扫描并 SHA256 约 2594 个文件）；
`SkeletonRenderer` 全局刷新约 3.21 秒；`SkeletonGraphic` 全局刷新约 0.69 秒。
另外约 10943 项 / 2.21 MiB 的状态会在每个改动文件上重复完整序列化，
随后 `FindObjectsOfTypeAll` 还会对几十个**没受影响**的 renderer 重走一遍替换路径。

**解法**　先用文件元数据筛选，只有大小/修改时间可疑的才算哈希；改动文件合并成一次状态写入；
建立「改动的资源 → 皮肤 → 受影响的 renderer」定向刷新；没变化就不做昂贵刷新，同一 renderer 每轮最多处理一次。

**安全**　没有靠「跳过贴图更新」或「禁用替换」来换假性能。

---

## BUG-027　小精灵的 Dump 只收集当前显示的部件，遗漏动画切换时才用的部件

**现象**　某个小精灵只导出约 21 个部件，原始资源里 `sad`、`happy_close`、`lamp_B`、`love` 等
动画状态的资源一个都没有，等于没法做完整的小精灵皮肤。

**原因**　旧做法只遍历 Dump 那一瞬间各插槽**当前显示**的部件；
Spine 动画稍后通过 attachment timeline 切换到的其他部件不在这张快照里。

**解法**　用 `Skin.GetAttachments` 通用枚举骨架/皮肤/插槽里的全部部件，按稳定身份去重；
角色和 12 个小精灵共用同一个分帧会话，每帧最多导出一个部件，单项失败继续，最后统一写 manifest。

> 同一个坑在一年后的 BUG-068 上又踩了一次 —— 那次是角色侧。

---

## BUG-026　资源完整但小精灵仍显示原图，启动时在读 PNG 之前就退出了

**原因**　选择文件里那个索引还是 `-1`。`GetSelected` 返回 -1 后，
`SkeletonGraphicAwake` 直接进 Original 分支、清空 `OverrideTexture`，在 `GetTexture` 之前就 return 了 ——
所以根本不存在 PNG 解码、运行时贴图或缓存过期的问题。

**结论**　把索引改对就正常了。这同时证明通用的 manifest 映射和替换路径**不需要**给特定角色开专属分支。

> 「没反应」先查选择状态，再查代码。

---

## BUG-025　普通退出时残留进程，以及原生崩溃后的映像残留

**现象**　游戏窗口消失后，旧实例可能仍然存在并持有 Mod 的 DLL（于是下次部署/替换文件被拒）。

**普通退出这一半的解法**　上一轮已经请求退出、但进程没真正结束时，把会话记为 `quit_requested`；
下次启动读会话文件，**只对身份完全匹配的单一旧实例**做安全清理。

身份闸门必须同时满足：PID 不是当前 PID、进程仍存在、`Process.StartTime` 精确匹配、
完整可执行路径与当前游戏一致、Windows 会话 ID 一致、旧实例早于当前实例、
阶段是 `quit_requested`、退出请求已超过最小安全时间、没有可见顶层窗口；
结束前再验证一次 PID / StartTime / 路径，结束后确认该 PID 已消失。

**拒绝策略**　任何一项读不到或对不上、权限不足、还有窗口 —— 一律不动，只记原因。
**禁止**按进程名批量结束、写死 PID、动服务或计划任务。

**原生崩溃那一半：边界**　某个诊断模块对全局 Spine 方法做 Harmony hook，
已确认会引发 `0xC00000FD` 原生栈溢出。崩溃发生在退出回调之前时会话仍是 running，
之后的进程快照 / 映像段残留**不可能**由已经崩溃的自己可靠清理。
这两类残留不得混为一个问题；那个诊断方案永久禁用。

---

## BUG-020　Mesh 部件的 PNG 导出成功，但缺少同名 JSON

**现象**　某个角色导出 420 张 PNG，只有 22 个 JSON，缺 398 个。

**原因**　PNG 导出支持任何能解析出图集区域的部件，而 JSON 写入对**非** `RegionAttachment` 直接 return。
于是 Mesh 部件只有 PNG 没有 JSON。

**解法**　去掉「只有 RegionAttachment 才写」的门槛。`RegionAttachment` 继续用原来的锚点算法，
其他部件用已有的「当前图片中心」回退。保留文件已存在的保护和正式字段默认值。

**验证**　重新导出后，修复前已有的 22 个 JSON 的 SHA256 全部不变；
全局 30 个皮肤共 5302/5302，缺失 0，全部可解析。

**过程**　第一版的 IL 插入点落在不可达分支里，实机没有生成任何新 JSON ——
改成以异常处理块真实的 try 起点作为插入点才对。

---

## BUG-021　Dump All 大规模导出到一半进程退出

**基线证据**　一次归档日志记录 4528 张独立导出，耗时约 845.6 秒（平均约 187 ms/张），
随后在某个角色中途终止，**没有**托管异常、栈溢出或明确的 OutOfMemoryException。

**查到的性能原因**
- 每个图集区域都重新对整张源图集做一次 `Graphics.Blit` + `ReadPixels` + `Apply`；
- 每新增一个区域就重写一遍不断变长的完整 manifest；
- 同步循环里的临时可读纹理和 prefab 克隆用的是帧末才生效的 `Object.Destroy`，
  单帧跑很久的 Dump All 会让对象一直堆积；
- 每张图还打一条日志。

**解法**　按角色生命周期管理可读图集缓存（同一源贴图只读回一次）；
manifest 改成标脏、角色结束时统一写；临时纹理和克隆立即释放；
逐张日志改为聚合汇总；Dump All 不再永久开启生命周期提取，也不再在只读导出后做无关的全局重载。

**边界**　修复后没有再跑一次完整的 Full Dump，所以不能用单角色数据冒充全量 benchmark；
「中途退出」是否彻底消失仍需下一次自然的 Full Dump 验证。

---

## BUG-022　单角色 Dump 之后，当前角色 / 小精灵的运行时贴图消失

**现象**　Dump 当前小精灵后主界面小精灵消失，进一次选择界面能恢复；
Dump 当前角色后主界面角色消失，进选择界面**也不一定**恢复。

**原因**　Dump 按钮在只读导出完成后调用了 `ReloadSkins`，
而它会销毁贴图缓存和合成图集缓存里的运行时图集贴图、清空 region/manifest 缓存；
单角色 Dump 之后又没有重新应用当前活着的骨架，renderer 于是继续引用**已经被销毁的**图集。

**为什么两边表现不同**　小精灵的选择界面会重建或刷新 UI/renderer，重新绑定资源就恢复了；
主界面的角色 renderer 通常不会因为打开选择界面而重建，所以一直握着失效引用。

**解法**　Dump 之后只请求一次「只刷索引」：更新皮肤目录和列表，
但**不销毁**运行时图集、替换贴图、material、图集区域或活动 renderer。
这个请求在当前 IMGUI 列表画完之后才消费，避免遍历中改列表。

---

## BUG-023　Dump 完成了但没有可见反馈，点 Root 才触发昂贵的全局重载

**现象**　点 Dump 看起来「没反应」，之后点 Root 才像开始工作，紧接着电脑风扇升速。

**真相**　日志显示 Dump 其实立刻执行了两次，分别 151.4 ms 和 96.4 ms ——
因为目标文件都已存在，`written=0`、`skipped=135`，没有图集读回、PNG 编码或磁盘写入。
风扇升速对应的是后面点 Root 触发的 `ReloadAssets`：
约 13 秒内全局处理 60 个 `SkeletonRenderer`、46 个 `SkeletonGraphic`、3 个 `CharacterApply`，
并重建多套运行时图集。

**解法**　目录索引刷新延后到列表绘制结束；
点击**已经选中**的 Original / Root 变成幂等 no-op，不再无意义地保存选择 + 全局重载。
真正改变选择时该重载还是要做。

**结论**　那次风扇事件是一次性的同步资源重载负载，不是 Dump 留下的 Update、协程、线程或后台轮询。

---

# 早期条目

编号不连续，是按发现顺序排的。有几条标注了「原始证据不完整」——
那是早期没留完整终端输出的部分，宁可写明也不编原因。

---

## BUG-036　完整画布的旧 UV 映射让加权 Mesh 的三角形碎片化

**现象**　某个部件扩框成功、图集放置也成功，但主体三角形仍然撕裂、错位或拉伸。

**原因**　旧映射只把 **hull 顶点**映射到完整画布边界，**内部顶点**继续用并 clamp 原来的 region UV。
对有内部顶点的加权 Mesh，这在保留原三角形拓扑的前提下产生了不连续的 UV 映射，
导致部分三角形方向翻转或新增退化。日志实测：一个部件 8 次翻转 + 2 个新退化。

**解法**　`BuildCanvasUvs` 保留完整画布边界，`MapInteriorHarmonically` 给内部顶点建立**连续的调和映射**；
`ValidateTriangleMapping` 在写入之前拒绝非法索引、奇异映射、实质方向翻转和不允许的退化。
原 triangles、bones、weights、deformAttachment、ParentMesh 及顶点/骨骼关联全部不动。

---

## BUG-032　Preview 私有替换铺到 81 个部件后碎片化 + 白色毛边

**原因**　81 个部件各自被设成私有整图 region + `0..1` UV，但最终 `SkeletonRenderer` 日志里**只有 1 个 material**。
部件的数据阶段 UV/region 和 renderer 最终阶段的 material/贴图不属于同一套绑定关系，于是错误采样。

**解法**　**不**继续实现 81 部件私有 material 的方案，也不加 UV 变通 ——
回滚到已验证的那一版（只保留两个 Mesh 部件的私有替换路径）。

> 与 BUG-034 是同一件事的两面：这条记录的是症状与回滚，那条记录的是架构结论。

---

## BUG-031　Preview 克隆了运行时的 Region/UV，却绑定原始贴图，导致角色碎片化

**现象**　Preview 的 UI 和动画都能创建，但**原版**角色（根本没开替换）稳定碎片化。
关掉替换、合成图集、打包矩形修正、renderer 守卫、相机实验之后仍能复现。

**原因**　Gameplay 的贴图修复会建立一张 512x4556 的运行时贴图，并更新共享的图集页/region/UV。
Preview 随后从**当前**运行时资产克隆，坐标系是 512x4556 的，
却继续绑定游戏导入的 512x2048 原贴图 —— 「运行时 Region/UV + 原始贴图」的错误组合。

**铁证**　被污染的图集页元数据是 512x4556，而 `Material.mainTexture` 是 512x2048；
某个部件的运行时 region `y=2185`，已经超出实际纹理高度（原图集里它的 `y=309`）。

**解法**　从 `SpineAtlasAsset.atlasFile` 读**原始** atlas 元数据，
在 Preview 的私有骨架数据解析完成后恢复全部真实的 region/page/UV 契约，
并给 Preview 的私有 material 绑定原始导入贴图。

> 一年后 Main 重写期间的 Preview 碎片化又是这一条的回归 ——
> 「新 UV + 原贴图」这个组合值得记牢。

---

## BUG-030　扩框模块的全局 Renderer hook 导致 Preview / 启动卡死

**现象**　Gameplay 正常，但点 Preview 就卡死并异常退出；后续版本甚至在点 Preview 之前的启动阶段就卡死。

**原因**　那个模块在 `OnInitializeMelon` 就注册了全局的 `SkeletonRenderer.Initialize(bool)` patch，
而「是不是 Preview」的判断发生在 postfix **内部** ——
隔离不了 patch 注册本身和更早的 renderer 生命周期影响。
对照组完全不创建 Harmony、不 `PatchAll`、不引用 Spine 之后，一切正常。

**解法**　先硬禁用，再按单变量逐步恢复，不许直接恢复全量 hook。

> 和 BUG-017 是同一类错误：**全局 hook 注册本身**就是副作用，不是「postfix 里判断一下就行」。

---

## BUG-024　Preview 的运行时骨架数据是空的，Animation 显示 unavailable

**原因**　这个游戏的 Spine IL2CPP 包装**不可靠支持**
「`ScriptableObject.CreateInstance<SkeletonDataAsset>` + `InitializeWithData(托管创建的 SkeletonData)`」
这条纯运行时路径。即使显式设置了 `skeletonData` / `stateData`，
原生侧的 `IsLoaded` / `GetSkeletonData(false)` 仍然不接受。

**解法**　不再往空资产里注入托管数据。改为 `Object.Instantiate` 原资产，
在克隆体上 `Clear` + `GetSkeletonData(true)`，让 Spine 从克隆的 JSON / 图集资产正常解析出独立的骨架数据；
随后再克隆各个部件。创建成功要过 Skeleton、AnimationState、MeshRenderer 三项硬校验，失败立即清理。

**结论**　这是 IL2CPP 包装 / Spine 资产初始化的边界问题，
不是相机、RenderTexture、动画列表或替换逻辑的问题。

---

## BUG-019　小精灵的选择界面封面身份整体错后一位

**现象**　一个小精灵的替换图出现在**另一个**小精灵的位置上；Dump 某个小精灵却导出了别人的图。
名字、实际图片、输出路径之间整体错位一格。

**原因**　`ElfinInfo.order` 是**一基**的游戏 UI 顺序，而 `BaseDataObject.listIndex`、
SF 的配置索引和 UI 循环索引都是**零基**。导航入口把 `order` 直接传了进去，
于是存成 `E:<order>:<variant>`，后面 Dump 和替换又按零基索引查 —— 整条链错一位。

**铁证**　日志里 `Captured Elfin id=6 key=little_witch_elfin_thumb`、`id=7 key=dragon_girl_elfin_thumb`，
而随后 `Dumped Elfin id=6` 写进了另一个角色的目录。

**解法**　封面的稳定身份统一为 `BaseDataObject.listIndex`，
采集、保存、Dump、解析、替换路径、Reload 全部用同一套语义，`order` 只留作显示顺序。
另外：建立绑定前先算出预期的 Addressables key 并校验实际图片名字，
不一致就记一行 identity mismatch、拒绝污染原始记录。

---

## BUG-018　Bad Apple 场景里最终材质采样的是原图集，和运行时 UV 对不上

**现象**　自定义皮肤在普通 Preview 和 Gameplay 都正常，但在 Bad Apple 那个黑白场景里显示成
一堆碎片化的黑块，拼不成完整剪影。

**原因**　部件和最终 mesh 的 UV 是按 1024x843 的运行时图集生成的；
兼容模块虽然在替换阶段建了正确的材质，游戏后续的 mesh/material 刷新
又把最终的 `sharedMaterials[0]` 恢复成采样 1024x256 **原版**图集的那个材质。

**解法**　在替换之后给目标 renderer 注册一次 `OnMeshAndMaterialsUpdated` 回调，
等游戏完成最终刷新，再一次性把序列化的 override、活动的材质 override 和最终 material 槽
同步成「BadApple shader + 运行时图集」的组合。

**排除了的**　A/B 证明 renderer 只有 1 个 submesh、1 个材质槽，没有 MaterialPropertyBlock，
UV 没有越界或非有限值，302 个顶点 alpha 全是 255 ——
所以不是多 submesh 混材质、不是 MPB、不是顶点 alpha，也不是全屏特效单独造成的。

---

## BUG-017　旧的全局 Renderer setter hook 导致点 Enter 后栈溢出卡死

**现象**　游戏能启动，但在欢迎界面点 Enter 后立刻无响应，普通关闭也结束不掉，
记录为 `System.StackOverflowException` / `0xC00000FD`。

**原因**　旧版全局 patch 了 `Renderer.set_material` 和 `Renderer.set_sharedMaterial`。
setter 的 postfix 会进入查找和应用逻辑，而那条路**又去访问或写入** Renderer 的材质状态 ——
在 IL2CPP 的 setter 上形成重入链。

**解法**　**永久废弃**全局 Renderer 材质 setter hook。
改为挂在具体的替换入口和目标 renderer 的一次性 mesh/material 生命周期事件上。

**读日志的坑**　不同次运行里 fault module 显示过 `Il2CppInterop.Runtime`、`MelonLoader`、
`System.Private.CoreLib` —— 那是递归栈在托管/IL2CPP 桥的不同层耗尽时的落点，
**不代表三个独立根因**。旧版那个局部的「正在应用」标志也没能消除全局 detour 的重入危险。

---

## BUG-016　提高动画幅度后，图片本身也跟着变大了

**原因**　这个游戏的 Spine 运行时里继承关系是
`ScaleTimeline -> TranslateTimeline`、`ShearTimeline -> TranslateTimeline`。
旧实现用 `TryCast<TranslateTimeline>()` 判断「是不是位移时间线」，
多态转换把 `ScaleTimeline` 和 `ShearTimeline` 一起匹配上了，于是缩放也被乘了倍率。

**解法**　处理位移时间线之前显式检测并跳过缩放和切变时间线，旋转继续独立处理。
没有加 `localScale` 补偿，也没有改部件尺寸。

**结论**　这是类型继承误判，不是「整体乘了倍率」。

---

## BUG-015　反编译出来的源码不能直接编译成可替换的 DLL（已知限制）

反编译输出带着大量托管/IL2CPP 同名类型歧义，而且不是原始工程，直接 build 过不去。
当时只能走「对已验证的 DLL 做小范围二进制补丁」这条路，靠 SHA256 核对加实机测试。

> **这条就是后来重写 Main 的起点。** 一条没有对应源码的二进制补丁链，
> 既没法满足 GPL 的「对应源码」义务，也没法长期维护。
> 现在 `src/main/` 是能编译的授权源码，这个限制不再存在。见 `docs/为什么重写了Main.md`。

---

## BUG-014　Mods 子目录缺少 manifest 时加载不了

MelonLoader（验证版本 0.7.4）会扫描 `Mods` 的一级子目录，但要求该子目录里有 `manifest.json`。
所以发布结构是 `Mods/SkinForger/` 里带一个 `manifest.json`，内容就是 `{}`，纯粹作为准入标记。

---

## BUG-013　异常残留的游戏进程持有 Mod 的 DLL 锁

替换 DLL 被拒，因为残留的游戏进程仍然映射着它。
Restart Manager 能确认是哪个进程持有；普通权限处理不了，需要提权定向处理那一个。
完整的内核/进程生命周期原因没有完全查清。后来做成了 BUG-025 那套严格身份校验的清理流程。

> 不要把「强杀进程」或「重启系统」当成常规开发流程。

---

## BUG-012　独立的诊断 DLL 造成启动 / 欢迎阶段的问题

原始证据不完整，但结论明确：那条「单独做一个诊断 Mod」的调查路线被禁用了。
后来改成在主候选 DLL 里做受控 tracing。

---

## BUG-011　patch 了 SkeletonMecanim.Initialize(bool)，导致高频重复应用 + 栈溢出

那个版本把 `SkeletonMecanim.Initialize(bool)` 当作替换入口，
结果高频重复执行，并记录到 `0xC00000FD` 栈溢出崩溃。该 patch 点**废弃**，
并且明确禁止再把它用作 tracing 或替换入口。

现在也没有安装 `Update` / `LateUpdate` / `FixedUpdate` 这类高频 patch。

---

## BUG-010　运行时 placement 缓存跨贴图复用，UV 映射错位

**原因**　合成 region 的缓存 key 里**少了运行时贴图的 InstanceID**，
于是 Preview 建的扩框 placement 被 gameplay 的另一张运行时图集错误复用。

**解法**　缓存 key 加上 `|rt=<运行时 Texture2D InstanceID>`。

> 一行 key 的疏漏，表现出来是 UV 越界。这是早期证据最清楚的一个 bug。

---

## BUG-009　改了图之后没法调整它在游戏里的 X/Y 位置

**解法**　加 JSON sidecar 的 X/Y 调整。显式写了锚点的 sidecar，
在原尺寸和扩框两种情况下都参与部件的 X/Y 定位。

---

## BUG-008　换图之后中心点 / pivot 没法调整

**原因**　新图的尺寸、构图或中心和原图不同时，光换 PNG 保证不了位置正确 ——
替换路径缺一个可编辑的锚点机制。

**解法**　加 JSON sidecar（现在的 v2 字段是 `anchorX`、`anchorY`、`origin`、`rotation`；
旧的 `sourceWidth` / `sourceHeight` 被忽略）。

---

## BUG-007　游戏在启动过程中直接退出

原始证据不完整。后来移除或禁用了多个启动期的风险项（包括 BUG-011 的那个 patch 点和 BUG-012 的诊断 DLL）之后，
启动恢复正常。**不要**把这个问题归因到某一个具体原因 —— 当时没有留下足以定论的证据。

---

## BUG-006　没有帧率上限，测试中跑到过约 800 FPS

界面/渲染循环没有合理的帧率上限。加了限制（现在约 55 FPS）。

---

## BUG-005　Preview 正常，但实际打歌时部分贴图异常

**原因**　部分确认：BUG-010 的运行时缓存问题确实是其中一环并已修复。
完整的视觉异常**没能完全查清**，所以这里不编一个更具体的原因。

**解法**　修正运行时贴图/部件的处理，让实际 gameplay 走替换后的路径。

---

## BUG-004　普通透明 PNG 的 Alpha 和游戏的渲染方式不兼容

**原因**　这条替换路径需要**预乘 Alpha（PMA）**，而画师导出的是普通 straight-alpha PNG。

**解法**　在生成运行时贴图数据的前后做预乘处理。

> 这条和后来的 BUG-060（Dump 出的图带白边）是一枚硬币的两面：
> 导入要预乘，**导出就必须反预乘**，否则来回一趟就乘了两次。

---

## BUG-003　改完 Dump 出来的 PNG，回到游戏里显示不对

原始证据不完整。最终把路径定成
`PNG → Texture2D → 运行时图集 → AtlasRegion → RegionAttachment`，问题随之解决。

---

## BUG-002　Dump 出来的是整张图集大图，不是单独的小图

**原因**　早期工作流直接暴露了打包后的图集贴图。整张图集没法给画师编辑。

**解法**　改成「以独立 PNG 为输入，运行时生成图集」，彻底废弃直接编辑整张图集的路线。
磁盘图集的输入/输出至今保持禁用。

> 这是架构变更，不是单点修补 —— 也是 SkinForger 后来一切功能的前提。

---

## BUG-001　上游的 SkinChanger 没法直接用在当前的 MelonLoader 环境里

归档的上游源码编译不出当时装在游戏里的那个 DLL；当前安装的是后续大幅修改过的 IL2CPP 构建。
于是整个项目转向自己的 MelonLoader / IL2CPP 实现路线，旧源码只留作出处与归属记录。

> 这条是项目的起点，也是 BUG-015 和后来重写 Main 的伏笔。
