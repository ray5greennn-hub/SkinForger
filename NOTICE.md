# NOTICE — SkinForger 的来源与现状

*最后更新：2026-11-20*

这份文件说明 SkinForger 的代码从哪里来、现在还剩什么，以及切割上游代码这件事做到了哪一步。
它是事实陈述，不是法律意见；如需对外发布，建议先由你本人通读一遍。

---

## 一、它的出身

SkinForger 最初是 **MuseDashSkinChanger** 的修改版。

- 上游：<https://github.com/BustR75/MuseDashSkinChanger>
- 上游许可：**GNU General Public License v3.0**
- 著作权：原作者及其贡献者

上游代码的归属没有变过，也不应当被淡化。本目录下的 `Back.cs` 与 `Front.cs`（共 945 行）
就是 2022 年归档的上游源码，原样保留在这里作为出处记录。

## 二、现在还剩什么

SkinForger 由两个程序集组成：

| 程序集 | 来源 |
|---|---|
| `SkinForger.Core.dll` | **全部为本项目原创**，从第一行起就与上游无关 |
| `SkinForger.Melon.dll`（Main） | **2026-11-17 起重写**，不包含上游代码 |

Main 的重写遵循洁净室纪律：

- 不从 `CurrentModDecompiled/`（旧 DLL 的反编译）转录任何内容；
- 也不从上游的 `Back.cs` / `Front.cs` 转录；
- 依据只有三样：游戏自己的 API、`SkinForger.Core` 各模块已经写明的行为契约、以及 `docs/development-logs/`。

这条纪律写在 `SkinForgerMain-Authored/SkinForger.Main.csproj` 的注释里，
来龙去脉与核验方法写在 `docs/为什么重写了Main.md`。

**可核验的事实**（2026-11-20）：

- `Back.cs` / `Front.cs` **不参与** Main 的构建（`SkinForger.Main.csproj` 只编译自身目录
  与九个 `<功能>/Runtime/` 目录，上游两个文件都不在其中）；
- 游戏中实际加载的是授权 Main，启动日志自报 `authored Main 0.1.0`；
  旧 Main（SHA256 `962A257B…`）只留在 私有备份 里作回滚，**不随产品分发**。

## 三、许可

**SkinForger 以《SkinForger 使用条款》（源码公开 / source-available）发布**，见 `LICENSE`。

从 GPL-3.0 换成它的依据，就是上面第二节那些可核验的事实：产品里已不含上游代码，
因此作者有权为自己的作品重新选择许可。换许可**不影响**上游项目本身，
MuseDashSkinChanger 依旧是它自己的 GPL-3.0 作品。

新许可的要点（以 `LICENSE` 原文为准）：

* **代码、做法、文档随便拿**——任何用途，含商业，无需署名；
* **唯一的条件**：SkinForger 这个成品不得用于非 Steam 正版的《Muse Dash》，要用请先问作者；
* **不保证、不负责**：用了出任何问题都由使用者自负。

## 四、旧工具链

2026-11-17 之前那套 Cecil 加工工具链已移进 `legacy/cecil-toolchain/`，
已核实**不参与任何构建**（见 `qa-sweep` 的 `no Cecil tool takes part in the build`）。
它是旧 Main 行为的唯一可读权威出处，作为参考资料保留，不删除。

## 五、皮肤作者

玩家制作的皮肤（`SkinForger/SkinCreator/Images/` 下的 PNG 与 JSON）属于各自的作者，
不受本项目许可约束。分发他人皮肤前请先取得其授权。
