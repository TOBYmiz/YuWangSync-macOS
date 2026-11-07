# 验证与验收

自动验证包括：从 Windows 移植的 34 个协议场景和新增的 2 个恢复场景，以及离线队列、账号隔离、目录合并、冲突、取消、SQLite 重开、缓存保护、递归下载、稳定快照、流式下载与引擎管道测试。

Mac 应用的 `--ui-selftest --output <json>` 在独立临时目录使用演示服务，检查原生窗口、侧栏和列表数据、经管道提交的目录任务，导出 JSON 与 PNG 后退出。Windows 使用引擎的 `--smoke-test`，不声称验证 Mac 原生界面。

`--platform-selftest --output <json>` 使用随机测试键检查凭据读写删除，并验证文件快照。Mac 运行真实 Keychain / NSFileCoordinator 桥接，Windows 运行独立 DPAPI 适配。不会读真实账号。

GitHub Actions 的绿色结果只表示对应提交通过所列检查。构建产物为 self-contained arm64 `.app`，包含原生桥接和 SQLite；包结构、ad-hoc 签名及启动自检进入 CI。

尚需独立实机验收：

- macOS 14 实机运行；macos-15 runner 不能证明 14 的兼容性。
- Keychain 首次授权、取消授权、重登录和升级后访问。
- Word/Excel/PowerPoint 的保存、原子替换、连续保存和关闭文档行为。
- Finder 定位、拖放、系统文件选择器、Dock 重开、菜单栏、睡眠唤醒与退出。
- 真实服务器登录、权限、目录合并、上传/覆盖、冲突处理及断网恢复；只在明确指定的测试目录执行。
- 下载产物在另一台 Mac 的首次启动与 Gatekeeper 交互；预览包尚未公证。

真实服务器请求、人工验收和签名公证不能用模拟测试替代。未满足以上关卡前保持预览状态。
