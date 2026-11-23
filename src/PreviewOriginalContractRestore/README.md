# PreviewOriginalContractRestore（已退役）

Preview 的「原状还原」与「私有替换」运行时，2026-10 写的，当时编成一个独立的
`SkinForger.PreviewOriginalContractRestore.Runtime.dll` 放在旧 Main 旁边。

**现在的构建不包含它**：

* 它的判定层（`PreviewOriginalContractGate` / `PreviewOriginalContractDecision`）已经搬到
  `src/PreviewSharedCore/Runtime/`，由 `SkinForger.Melon`（授权 Main）直接编进去；
* 两份同名类型同时加载会让按名字的反射绑定变得不确定，所以这个 DLL 已从 `Mods/SkinForger/` 移出
  （2026-11-22），也不随发布包分发。

这两个文件留在仓库里只作为机制的参照：`PreviewOriginalSpineDataRestore` 记录了
Preview 打开前如何把骨架数据还原成游戏原状，`PreviewPrivateReplacement` 记录了
如何只对 Preview 那一份克隆体套用替换图而不影响正在跑的游戏。
当前构建没有编译它们。
