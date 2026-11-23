# 源码状态 / Source status

*最后更新：2026-11-22*

## 一句话

SkinForger 现在**不含任何上游代码**，以 **《SkinForger 使用条款》（源码公开 / source-available）** 发布。

## 随产品分发的东西

`Mods/SkinForger/`：

| 文件 | 来源 |
|---|---|
| `SkinForger.Melon.dll` | 授权 Main —— 2026-11-17 起洁净室重写 |
| `SkinForger.Core.dll` | 自始原创 |
| `SkinForger_MuseDashWatchdog.exe` | 本项目自有（`src/Watchdog/`） |
| `manifest.json` | 本项目自有 |

旧的那版 Main（SHA256 `962A257B…`）**不随产品分发**，只在私有备份里留作回滚。

`SkinForger.PreviewOriginalContractRestore.Runtime.dll` 从 2026-11-22 起**也不再分发**：
日志证实 MelonLoader 从未加载它，而它里面那几个类型已经由 `PreviewSharedCore` 编进授权 Main，
两份同名类型同时在场会让按名字的反射绑定变得不确定。源码与说明见
`src/PreviewOriginalContractRestore/`。

**可复现构建**：本仓库 `src/core` 与 `src/main` 编译出来的两个 DLL，与正在分发的文件
SHA256 逐字节一致（2026-11-22 核对；两个工程都设了 `DebugType none`）。步骤见 `docs/BUILD.md`。

## 「不含上游代码」是怎么核验的

对照 2022 年归档的上游源码（`Back.cs` + `Front.cs`，945 行）：

**代码重合度**（按连续 token 片段比对，查抄袭的标准做法）

| 片段长度 | 重合 | 占上游 |
|---|---|---|
| 6 token | 231 | 5.88% |
| 8 token | 81 | 1.81% |
| **12 token** | **15** | **0.29%** |

那 15 条**全部**是 C#/Unity 的通用写法或游戏自己的名字，例如
`GUILayout.EndScrollView(); GUI.DragWindow(new Rect(`、`if (File.Exists(file))`、
`{ mainShow, battleShow, victoryShow, failShow }`。没有一条属于上游的独创表达。

**标识符重合**：上游声明 46 个，Main 里出现 14 个。其中 `OnGUI` / `Start` 是引擎回调，
`Scan` / `SaveSelection` / `ReloadAssets` / `GetTexture` 等是 `SkinForger.Core`
用 Harmony **按名字**绑定的接缝，**必须**叫这个名字。名字是接口，不是表达。

**构建**：`SkinForger.Main.csproj` 只编译自身目录与十个 `<功能>/Runtime/` 目录，
上游那两个文件**不在其中**（它们根本不在本仓库里）。

**第三方依赖**：两个项目**没有任何 NuGet 包**；所有引用都是本机已有的 DLL 且不随产品拷贝
（MelonLoader 为 Apache-2.0）。**没有任何 GPL / LGPL 库被链接或打包。**

做法与完整说明见 `docs/为什么重写了Main.md`。

## 这个仓库里有什么

目录逐个说明见 `docs/仓库结构.md`。一句话：`src/` 是能编译出产品的全部源码，
`docs/` 是说明与开发记录，`legacy/` 是重写之前那套已退役的 Cecil 工具链。

**不在这个仓库里的东西**（故意的）：

* 旧 DLL 的反编译 —— **洁净室禁区**，不得阅读，也不该随包发布；
* 上游 GPL-3.0 的 `Back.cs` / `Front.cs` —— 产品已不含上游代码，
  再把它放进来会给这一个目录重新带上 GPL 义务，徒增混淆。
  出处与致谢改由 `NOTICE.md` 承担，上游当年的 README 留在 `docs/upstream/`；
* 历次部署的 DLL 备份、本机日志、玩家自己的皮肤文件。

## 未完成

见 `FINAL-PRE-RELEASE-AUDIT.md`。最要紧的一条：功能恢复清单还没全绿。
