# 依赖

*最后更新：2026-11-22*

## 一句话

**产品不打包、不转发任何第三方 DLL。** 两个工程都没有 NuGet 包，
所有引用都指向本机已装的 MelonLoader，而且全部 `<Private>false</Private>` —— 不进输出目录。

发布包里只有这些文件，没有别的：

```
SkinForger.Core.dll        本项目自有
SkinForger.Melon.dll       本项目自有
SkinForger_MuseDashWatchdog.exe   本项目自有
manifest.json  LICENSE  NOTICE.md
```

## 编译时引用什么

| 来源 | 用到的 | 许可 |
|---|---|---|
| `MelonLoader\net6\` | `MelonLoader.dll`、`0Harmony.dll`、`Il2CppInterop.Runtime.dll`、`Newtonsoft.Json.dll` | MelonLoader：Apache-2.0 |
| `MelonLoader\Il2CppAssemblies\` | 游戏自己的程序集（`Assembly-CSharp`、`Il2Cppspine-unity`、`Il2CppPeroTools2`、各 `UnityEngine.*Module` 等二十余个） | 游戏自己的素材，由 MelonLoader 在本机生成，**不分发** |

**没有任何 GPL / LGPL 库被链接或打包。**

`MelonLoader\Il2CppAssemblies\` 是 MelonLoader 在玩家自己机器上反射游戏生成的，
不在本仓库里，也不随发布包走。

## 工具链

| 东西 | 验证过的版本 |
|---|---|
| .NET SDK | 10.0.401（目标框架 net6.0；任何能编 net6.0 的 SDK 都行） |
| .NET 6 运行时 | 6.0.36 |
| MelonLoader | 0.7.4.2586 |
| Python | 3.x（只有 `src/main/Analysis/*.py` 用） |

这些是验证过的开发环境，不是「只能用这些版本」。

## Mono.Cecil

**现在的构建用不到它。** 2026-11-17 之前那套二进制补丁工具链要用 Mono.Cecil 0.11.6
（审计过的 SHA256 `831DCA77…`），那套东西已退役，留在 `legacy/`，不参与任何构建，
也不随发布包分发。
