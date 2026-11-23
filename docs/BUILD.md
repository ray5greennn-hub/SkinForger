# 怎么编译

*最后验证：2026-11-22*

## 需要什么

| 东西 | 说明 |
|---|---|
| .NET SDK 6.0 | `dotnet --list-sdks` 能看到 6.0 就行（更高版本的 SDK 也能编，目标框架是 net6.0） |
| 一份装好 MelonLoader 的 Muse Dash | 编译要引用游戏自己的 IL2CPP 程序集，**仓库里不带这些 DLL** |
| Python 3 | 只有离线自检工具要用（`src/main/Analysis/*.py`） |

依赖清单见 [`DEPENDENCIES.md`](DEPENDENCIES.md)。

**没有任何 NuGet 包。** 两个主工程的 `NuGet.Config` 把包源清空了（`<clear />`），
所有引用都是本机 MelonLoader 目录里已有的 DLL，而且都是 `<Private>false</Private>` —— 不会被拷进输出。

MelonLoader 要先在游戏上跑过一次，这样才会生成 `MelonLoader\Il2CppAssemblies\`（反射出来的游戏程序集）。

## 最省事的做法：把仓库放在游戏文件夹里

```
Muse Dash\
├─ MelonLoader\
├─ Mods\
└─ SkinForger_ReleaseCandidate\   ← 这个仓库
   └─ src\
```

这样各工程默认的 `GameRoot`（往上三层）正好落在游戏根目录，什么都不用配。

### 1. Core

```
cd src\core
dotnet build -c Release
```

产出 `src\core\Build\SkinForger.Core.dll`。

### 2. Main

Main 的工程**引用 Core 的 DLL**（这是 Core 自己的检查：`SkinForger.Melon` 必须引用
`SkinForger.Core`，否则 Core 一个模块都不启动）。它找的是 `Mods\SkinForger\SkinForger.Core.dll`，
所以先把上一步的产物放过去，再编 Main：

```
copy src\core\Build\SkinForger.Core.dll "..\Mods\SkinForger\SkinForger.Core.dll"
cd src\main
dotnet build -c Release
```

产出 `src\main\Build\SkinForger.Melon.dll`（名字是 `Melon` 不是 `Main`，**不能改** ——
Core 按这个名字找 Main）。

### 3. 看门狗（可选）

```
cd src\Watchdog
dotnet build -c Release
```

产出 `SkinForger_MuseDashWatchdog.exe`：游戏退出后做清理用的小程序，不是必需品。

## 仓库放在别处

各工程都接受 `GameRoot` 覆盖，结尾要带反斜杠：

```
dotnet build -c Release -p:GameRoot="C:\Program Files (x86)\Steam\steamapps\common\Muse Dash\"
```

`src\PreviewOriginalContractRestore\Runtime`（已退役，见那个目录的 README）还额外接受
`-p:RuntimeReferenceDirectory=".../Microsoft.NETCore.App/6.0.x"`。

## 装到游戏里

把这两个文件放进 `Mods\SkinForger\`：

```
SkinForger.Core.dll
SkinForger.Melon.dll
```

`Mods\SkinForger\` 这个文件夹名无所谓，但两个 DLL 必须在**同一个文件夹**里 ——
MelonLoader 不会从 `Mods` 加载非 melon 的库，是 `Bootstrap` 模块装了一个
`AssemblyResolve` 钩子，从自己所在的文件夹解析 `SkinForger.*`。

## 能不能编出一模一样的 DLL

能。Core 和 Main 两个工程都设了 `DebugType none`，不写 PDB、不嵌入路径，
所以同一份源码在不同机器上编出来的 DLL **逐字节相同**。

已验证（2026-11-22）：本仓库的 `src/core` 与 `src/main` 编译结果，与 `Mods/SkinForger/`
里正在用的两个 DLL 的 SHA256 完全一致。

看门狗那个 exe 不在此列（没有关掉调试信息，MVID 每次都不同），但它由同一份
`src/Watchdog/Program.cs` 编出。

## 离线自检工具

不需要游戏，不需要编译，只读源码：

```
cd src\main\Analysis
python qa-sweep.py
```

它会把其余几个工具全跑一遍：方法名契约、Harmony 参数名、反射字段名、
注入调用是否真的被调、宿主调用点、死功能、扩框几何。

`qa-sweep.py` 不给参数时默认往上四层找游戏根目录；仓库放在别处就把游戏根目录作为第一个参数传进去：

```
python qa-sweep.py "C:\Program Files (x86)\Steam\steamapps\common\Muse Dash"
```

**判定标准是「0 未解释」，不是「0 FAIL」** —— 每一条 missing / not called 都得有一句解释写在工具里，
解释不了的那条就是真缺口。原因见 `docs/为什么重写了Main.md` 第六节。

最后一次结果（2026-11-22，本仓库的副本）：`qa-sweep` 43 项 0 FAIL；
`contract-parity` 59 项满足 0 missing；`param-name-parity` 55 项 0 name breaks；
`field-name-parity` 13 项 0 missing；`host-call-parity` 0 absent；`dead-feature-check` 0 never started。

两个工具在这个仓库里拿不到它要的东西，不是坏了：

* `expansion-check.py` 读的是**玩家自己的** `SkinCreator\Images`，要把那个路径传进去：
  `python expansion-check.py "<游戏>\SkinForger\SkinCreator\Images" ["<MelonLoader 日志>"]`。
  不给日志就只能列出扩了框的部件，判不了锚点落在哪。
* `injection-parity.py` 比的是「旧版用 Cecil 注入的调用，新版有没有真的调」。
  旧那套工具链已经退役（`legacy/`），所以在这个仓库里它报 0 条可比 —— 它的用处属于重写那几天。

Main 的纯逻辑层还有一套离线测试（617 条，不需要游戏）：

```
cd src\main\Tests
dotnet run -c Release
```
