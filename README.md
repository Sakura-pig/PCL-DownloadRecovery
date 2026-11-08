# PCL 下载恢复（PCL-DownloadRecovery）

PCL 从 2.99.0.0 正式版开始去掉了 MC 下载功能：下载页的「原版」入口被替换成一条红色提示，整合包、修改页里的安装入口也一起关掉了。到 2.99.0.1，官方干脆把「原版」「游戏资源」两个入口从下载页左栏直接藏了起来，主页、选文件夹页、更新提示弹窗里的下载按钮也全部隐藏。

这个仓库提供通用补丁工具和自动流水线：对官方发布的任意 PCL 2.99.0.x 正式版打补丁，把下载功能和各处入口加回来，产物除了补丁点之外与原版完全一致。

## 自动流水线

`.github/workflows/auto-patch.yml` 每天检查一次官方仓库（Meloong-Git/PCL）的 `最新正式版.zip`：

1. 取出官方最新版 exe；
2. 用 `McRestorePatcher` 打补丁（自动识别版本，匹配失败会直接退出）；
3. 对产物再跑一次补丁做自检（必须全部跳过且退出码 0）；
4. 生成便携包并发布 `v<版本号>` 的 Release。

如果某次官方更新把代码结构改得对不上，流水线会失败并自动开 Issue，需要人工适配补丁工具。也可以在 Actions 页面手动触发。

## 用成品

Release 页面提供：

- `Plain Craft Launcher 2.exe`：打好补丁的官方版。替换现有的启动器即可，替换前先备份。
- `PCL_<版本>_MCfix.zip`：带 `PCL\Setup.ini` 的便携包，解压就能用。
- `McRestorePatcher.zip`：补丁工具（源码在 `McRestorePatcher/` 目录），可对任意官方 2.99.0.x 自行打补丁。

启动器会检查更新。为防止修复版被自动更新覆盖，包里的 `Setup.ini` 已经写好 `SystemSystemUpdate:3`；如果你用的是自己的数据文件夹，在 `PCL\Setup.ini` 里加一行 `SystemSystemUpdate:3`，或者在设置里把更新改成「关闭更新提示」。

## 自己打补丁

对任意官方 PCL 2.99.0.x 正式版的 exe 打补丁：

```
dotnet build McRestorePatcher/McRestorePatcher.csproj -c Release
dotnet McRestorePatcher/bin/Release/net8.0/McRestorePatcher.dll "<原版 exe>" "<输出 exe>"
```

需要 .NET 8 SDK。工具用 dnlib 读 IL，输出每处补丁的状态（OK / SKIP / FAIL），全部成功才会写出文件，任何必需补丁失败都会返回非 0 且不产出半成品。

辅助模式（调试用）：

```
# 反汇编某个方法
dotnet McRestorePatcher.dll --dump <exe> PCL.PageDownloadLeft .ctor
# 列出引用了某个名字的方法
dotnet McRestorePatcher.dll --refs <exe> get_ItemInstall
# 搜索用户字符串
dotnet McRestorePatcher.dll --strings <exe> "相关法律法规"
```

## 补了什么

官方版在编译时用 `RestrictMcDownload` 常量把受限分支常量折叠掉，旧逻辑没有进入二进制。工具按「语义锚点 + 折叠形态」逐处还原，2.99.0.0 是 5 处核心功能，2.99.0.1 起额外有 7 处入口隐藏：

| 位置 | 恢复内容 |
| --- | --- |
| `PageDownloadInstall.LoaderInit` | 加载器改回 `DlClientListLoader`，下载页能正常拉版本列表 |
| `ModModpack.ModpackInstall()` | 「安装整合包」恢复文件选择框和后台安装 |
| `ModModpack.ModpackInstall(File)` | 恢复整合包类型分发（CurseForge / HMCL / MMC / MCBBS / Modrinth / 启动器包 / 普通压缩包） |
| `PageDownloadModify.LoadInstanceFinished` | 修改页的「MC 版本」卡片恢复显示 |
| `PageDownloadResourceDetail.InitializeStackResourceVersions` | 整合包版本列表恢复「安装」按钮 |
| `PageDownloadLeft..ctor` | 恢复下载页左栏的「原版游戏」入口和「游戏资源」分组标题 |
| `PageResource.set_PageType` | 资源页恢复「安装整合包」按钮 |
| `PageInstanceModDisabled.BtnDownload_Loaded` | Mod 禁用页恢复「下载」按钮 |
| `PageLaunchLeft.RefreshButtonsUI` | 没有可用版本时恢复「下载游戏」引导状态 |
| `PageSelectLeft.McFolderListUI` | 恢复「导入整合包」列表项 |
| `PageSelectRight.McInstanceListUI` | 空版本时恢复「下载」按钮 |
| `ModMinecraft.McDownloadClientUpdateHint` | MC 更新提示弹窗恢复「下载」按钮 |

## 容错设计

官方的合规改动基本是「同一套代码换文案 / 换折叠结果」，所以补丁匹配做了比较宽的容错，避免官方小改一下就失效：

- 锚点按成员名（`get_ItemInstall`、`set_Visibility` 等）而不是偏移量，忽略 nop 填充；
- 常量匹配兼容 `ldc.i4.0~8`、`ldc.i4`、`ldc.i4.s` 各种编码；
- 文案锚点（如合规异常文本）失效时，退回结构特征定位（例如 `ldstr + new Exception(string) + throw` 的形态）；
- 每处补丁带幂等检查，重复运行只会全部 SKIP，不会二次修改；
- 版本不匹配或必需补丁缺失时拒绝输出，宁可失败也不产出坏 exe。

## 验证

打开启动器，进入「下载」→「原版」，能看到 Mojang 的版本列表就说明补丁生效了。整合包列表的安装按钮、修改页的版本卡片也应该是正常状态。

## 注意

- 适用于官方 **2.99.0.x 系列**。更老的版本本来就没有限制；更新的版本如果结构大改，需要按 Issue 提示人工适配。
- 改过二进制，部分杀毒软件可能误报。
- 官方后续更新可能会覆盖修复版，记得关掉更新提示。
- 仅供学习交流。PCL 版权归原作者所有，请支持官方版本。

## 关于本仓库

其余目录是官方 PCL 源码（上游 Meloong-Git/PCL 的同步镜像），方便对照版本。补丁工具在 `McRestorePatcher/` 目录。

本仓库的源码部分遵循上游协议，详见 [LICENCE](LICENCE)。
