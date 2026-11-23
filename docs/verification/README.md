# 验证记录

这些是各功能当年做完时留下的验证记录：测了什么、怎么测的、哪些边界没覆盖。

**读之前请注意：它们描述的是 2026-11-17 重写 Main 之前的构建方式。**

那时候 Main 没有可编译的源码，功能是用 Mono.Cecil 把运行时代码「嵌进」一个已验证的 DLL 里的 ——
所以这些文档里会出现写死的输入 SHA256、`Build/Program.cs` 这样的加工步骤、
以及一些已经不在本仓库里的目录名。那套工具链已经退役，留在 `legacy/`
（为什么退役见 `docs/为什么重写了Main.md`）。

**文档里的结论仍然成立**（这些功能现在还在用），但要找现在的源码，去这里：

| 这份文档讲的 | 现在的源码 |
|---|---|
| CUSTOM-LOADING-* | `src/CustomLoadingPic/Runtime/` |
| REPLACED-BACKUP | `src/ReplacedBackup/Runtime/` |
| FPS-LIMITER-* | `src/FpsLimiterPatch/Runtime/` |
| TOUHOU-COMPAT-V2 | `src/core/Modules/TouhouCompat/`（2026-11-16 并入 Core） |

文档里提到的「禁用的旧二进制」「反编译参考」都不在本仓库里，也不会发布。
