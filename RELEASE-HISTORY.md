# 版本历史

## 还没有正式发布

SkinForger 至今**没有公开发布过任何版本**。开发期间只有少量私下试玩的测试包，不算发布。

## 当前状态（2026-11-22）

| 文件 | SHA256 | 来源 |
|---|---|---|
| `SkinForger.Core.dll` | `D7AFF324…` | `src/core/`，逐字节可复现 |
| `SkinForger.Melon.dll` | `88B09082…` | `src/main/`，逐字节可复现 |
| `SkinForger_MuseDashWatchdog.exe` | `88CD5E31…` | `src/Watchdog/Program.cs` |

两个 DLL 都已核对：本仓库源码编译出来的结果与正在用的文件 SHA256 完全一致。

## 重要节点

| 日期 | 事情 |
|---|---|
| 2026-09-24 | 开工。起点是 MuseDashSkinChanger（GPL-3.0）的修改版 |
| 2026-11-14 | 二十来个独立 melon 合并成一个 `SkinForger.Core.dll` |
| 2026-11-17 | 决定把 Main 整个重写，摆脱上游代码（见 `docs/为什么重写了Main.md`） |
| 2026-11-19 | 授权 Main 接管，旧 Main 不再随产品分发 |
| 2026-11-20 | 许可换成《SkinForger 使用条款》；反编译参考与上游源码移出发布包 |
| 2026-11-22 | 全部源码进入本仓库；旧的 Cecil 工具链移进 `legacy/` |

2026-11-17 之前那条二进制补丁链的完整哈希历史，留在
`legacy/旧二进制补丁链-哈希历史.md` 和 `legacy/旧二进制补丁链-RELEASE-MANIFEST.json`。
那些哈希描述的是已经退役的构建方式，**不要**拿来对照现在的文件。

## 发布前还没做完的事

见 `FINAL-PRE-RELEASE-AUDIT.md`。
