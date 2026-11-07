# PCL 下载恢复（PCL-DownloadRecovery）

PCL 从 2.99.0.0 正式版开始去掉了 MC 下载功能：下载页的「原版」入口被替换成一条红色提示，整合包、修改页里的安装入口也一起关掉了。到 2.99.0.1，官方干脆把「原版」「社区资源」两个入口直接藏了起来。

这个仓库用来恢复这些入口。恢复出来的就是一个普通的 PCL 2.99.0.0 正式版，界面、功能和原版一致，只是下载又能用了。

## 用成品

Release 页面提供了这些文件：

- `Plain Craft Launcher 2.exe`：打好补丁的 2.99.0.0 正式版。替换现有的启动器即可，替换前先备份。
- `PCL_2.99.0.0_MCfix.7z`：带 `PCL\Setup.ini` 的便携包，解压就能用。
- `McRestorePatcher.zip`：补丁工具（源码在 `McRestorePatcher/` 目录）。

启动器会检查更新。为防止修复版被自动更新覆盖，建议使用包里的 `Setup.ini`（已写入 `SystemSystemUpdate:3`）；如果用的是自己的数据文件夹，在 `PCL\Setup.ini` 里加一行 `SystemSystemUpdate:3`，或者在设置里把更新改成「关闭更新提示」。

## 自己打补丁

对官方 2.99.0.0 正式版的 exe 打补丁：

```
dotnet build -c Release
dotnet bin/Release/net8.0/McRestorePatcher.dll "<原版 exe>" "<输出 exe>"
```

需要 .NET 8 SDK。工具用 dnlib 读 IL，按 2.99.0.0 正式版的二进制特征定位代码，版本对不上会直接报错退出。

## 具体改了什么

官方版在编译时用 `RestrictMcDownload` 常量把下载入口裁掉了，旧版的分支没有进入二进制。补丁按照 2.13.1.1 / 2.14.0.0 的实现，把 5 处入口的 IL 补了回去：

| 位置 | 恢复内容 |
| --- | --- |
| `PageDownloadInstall.LoaderInit` | 加载器改回 `DlClientListLoader`，下载页能正常拉版本列表 |
| `ModModpack.ModpackInstall()` | 「安装整合包」恢复文件选择框和后台安装 |
| `ModModpack.ModpackInstall(File)` | 恢复整合包类型分发（CurseForge / HMCL / MMC / MCBBS / Modrinth / 启动器包 / 普通压缩包） |
| `PageDownloadModify.LoadInstanceFinished` | 修改页的「MC 版本」卡片恢复显示 |
| `PageDownloadResourceDetail.InitializeStackResourceVersions` | 整合包版本列表恢复「安装」按钮 |

## 验证

打开启动器，进入「下载」→「原版」，能看到 Mojang 的版本列表就说明补丁生效了。整合包列表的安装按钮、修改页的版本卡片也应该是正常状态。

## 注意

- 只适用于官方 **2.99.0.0 正式版**。2.99.0.1 改动更大（入口被隐藏），本工具不支持。
- 改过二进制，部分杀毒软件可能误报。
- 官方后续更新可能会覆盖修复版，记得关掉更新提示。
- 仅供学习交流。PCL 版权归原作者所有，请支持官方版本。

## 关于本仓库

其余目录是官方 PCL 源码（上游 Meloong-Git/PCL 的同步镜像），方便对照版本。补丁工具在 `McRestorePatcher/` 目录。

本仓库的源码部分遵循上游协议，详见 [LICENCE](LICENCE)。
