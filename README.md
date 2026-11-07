# 育网云 macOS

独立的 macOS 原生客户端，目标为 macOS 14+、Apple Silicon。界面使用 SwiftUI / AppKit 系统控件，协议、缓存和持久离线队列使用 C# / .NET 10。Windows 用于开发和测试后台引擎，原生界面由 Mac 构建。

这是预览实现。构建和自动测试的结果见 GitHub Actions；macOS 14 真机、Office 编辑、Finder/拖放及真实内网写操作的验收边界见 [验证清单](docs/VALIDATION.md)。

## 功能

- 多账号、验证码、会话复用；Mac 凭据存入 Keychain。
- 共享盘和文件夹树后台递归预加载；展开节点可独立加载子目录，无需先点击进入。
- 路径导航、当前目录筛选、排序、强制刷新；通过文件直链探测真实大小，未确认时标为约数。
- 单文件、多文件、目录递归离线下载，以及独立导出。
- 选择文件或拖放上传，目录递归合并，同名文件汇总选择跳过、覆盖或改名。
- 编辑受管理的本地副本后自动回传，检测云端并发修改并暂停冲突。
- 新建目录、改名、同盘移动和回收站删除，可离线排队并按依赖执行。
- 持久化上传证据、完整内容校验、中断恢复；结果不明确时停止盲目重传。
- 上传与校验期间可继续浏览、下载及检查连接；下载校验为低优先级，新传输会暂停校验。
- 关闭窗口后驻留菜单栏；退出会保存队列。

## 开发

开发使用固定的 .NET SDK 10.0.401，CI 安装同一版本。托管依赖及 osx-arm64 单文件发布依赖使用分别提交的锁文件，避免 SDK 补丁升级改变隐式依赖。

```powershell
dotnet restore YuWangSync.slnx --locked-mode
dotnet test YuWangSync.slnx -c Release --no-restore
dotnet run --project src/YuWangSync.Engine -- --smoke-test --output artifacts/engine-smoke.json
```

Mac 应用可用 `open YuWangSync.app --args --demo` 进入演示。`--demo` 使用独立临时目录和内存模拟服务，不读取正式账号。正常启动在界面中填写服务器、账号和密码。Windows 引擎测试的凭据由当前用户 DPAPI 保护，数据独立于 Windows Cloud Files 版。

Mac 构建需要包含 SwiftUI SDK 的 Xcode / Apple 命令行工具，用于编译原生界面和 Keychain / NSFileCoordinator 桥接。GitHub Mac runner 提供这些工具；Windows 不安装苹果工具链。无需 .NET macOS workload。

```bash
bash tools/package-macos.sh
```

输出同时包含 `artifacts/YuWangSync-macOS-arm64-preview.zip` 和 `.dmg`，各有 SHA-256 校验文件。ZIP 内为 `.app`；DMG 内提供应用与 Applications 链接，可拖入安装。其中 `.app` 自带 .NET 运行时，采用 ad-hoc 签名，尚未做 Developer ID 公证。GitHub Actions 使用标准 Windows 和 ARM64 Mac runner，产物保留七天，不自动发布 Release。

关于页显示作者 TOBYmiz 及开源许可列表，完整声明随应用提供；来源见 [开源组件](docs/THIRD-PARTY-NOTICES.md)。

## 数据与同步

Mac 数据位于 `~/Library/Application Support/YuWangSync`，普通缓存位于 `~/Library/Caches/YuWangSync`。Windows 开发数据位于 `%LOCALAPPDATA%/YuWangSync-Mac-Development`。

双击打开的副本受应用管理，保存后会排队回传；“另存为”导出的副本不监视。主动离线下载是一次性快照，不自动跟踪未来的云端更新。刷新会重查云端列表，不覆盖本地编辑。

未完成任务、冲突和本地修改不会被缓存清理删除。上传前使用稳定文件快照。设置中的“上传文件后下载校验”默认开启，“使用数据流量/热点时启用”默认关闭；网络类型由 macOS 的费用标记识别，未知类型暂缓自动校验。校验在空闲时下载直链内容并复核 SHA-256，有新上传、下载或本地编辑时暂停，恢复后重新核对完整内容。

关闭下载校验时，仅新建且已完整确认、名称未变化的上传可在大小核对后完成。覆盖旧文件、断点续传和结果不明确的上传仍保留为待校验，旧备份不会提前回收；可在任务的“更多”菜单选择“立即下载校验”。启用校验但当前网络不允许时，任务保留至网络允许或手动校验。暂停后的重新校验会产生额外下载流量。

已发送但结果未知的任务先核对云端，再决定续办。取消任务不代表撤销已经发生的云端操作。详情见 [架构与恢复规则](docs/ARCHITECTURE.md)。

## 来源与许可

协议代码从 Windows 育网云项目的 `3d14869bd1627e2389ade276b73babc4247f3bfe` 选择性移植。记录见 [移植来源](docs/PORTING.md)。独立仓库保留初始提交的 [MIT 许可证](LICENSE)。
