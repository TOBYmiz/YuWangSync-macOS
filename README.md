# 育网云 macOS

独立的 macOS 原生客户端，目标为 macOS 14+、Apple Silicon。界面使用 SwiftUI / AppKit 系统控件，协议、缓存和持久离线队列使用 C# / .NET 10。Windows 用于开发和测试后台引擎，原生界面由 Mac 构建。

这是预览实现。构建和自动测试的结果见 GitHub Actions；macOS 14 真机、Office 编辑、Finder/拖放及真实内网写操作的验收边界见 [验证清单](docs/VALIDATION.md)。

## 功能

- 多账号、验证码、会话复用；Mac 凭据存入 Keychain。
- 共享盘树、路径导航、当前目录筛选、排序、强制刷新。
- 单文件、多文件、目录递归离线下载，以及独立导出。
- 选择文件或拖放上传，目录递归合并，同名文件汇总选择跳过、覆盖或改名。
- 编辑受管理的本地副本后自动回传，检测云端并发修改并暂停冲突。
- 新建目录、改名、同盘移动和回收站删除，可离线排队并按依赖执行。
- 持久化上传证据、完整内容校验、中断恢复；结果不明确时停止盲目重传。
- 关闭窗口后驻留菜单栏；退出会保存队列。

## 开发

需要 .NET SDK 10.0.401 或兼容的 .NET 10 SDK。依赖版本在项目和锁文件中固定。

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

输出为 `artifacts/YuWangSync-macOS-arm64-preview.zip`。其中 `.app` 自带 .NET 运行时，采用 ad-hoc 签名，尚未做 Developer ID 公证。GitHub Actions 使用标准 Windows 和 ARM64 Mac runner，产物保留七天，不自动发布 Release。

## 数据与同步

Mac 数据位于 `~/Library/Application Support/YuWangSync`，普通缓存位于 `~/Library/Caches/YuWangSync`。Windows 开发数据位于 `%LOCALAPPDATA%/YuWangSync-Mac-Development`。

双击打开的副本受应用管理，保存后会排队回传；“另存为”导出的副本不监视。主动离线下载是一次性快照，不自动跟踪未来的云端更新。刷新会重查云端列表，不覆盖本地编辑。

未完成任务、冲突和本地修改不会被缓存清理删除。上传前使用稳定文件快照，并在完成后下载复核 SHA-256；因此上传验证会产生额外下载流量。覆盖前保存云端旧版本，成功后移入服务器回收站。

已发送但结果未知的任务先核对云端，再决定续办。取消任务不代表撤销已经发生的云端操作。详情见 [架构与恢复规则](docs/ARCHITECTURE.md)。

## 来源与许可

协议代码从 Windows 育网云项目的 `3d14869bd1627e2389ade276b73babc4247f3bfe` 选择性移植。记录见 [移植来源](docs/PORTING.md)。独立仓库保留初始提交的 [MIT 许可证](LICENSE)。
