# 移植来源

- Windows 源基线：`3d14869bd1627e2389ade276b73babc4247f3bfe`
- 远端仓库初始许可证提交：`da2b59bcd592f24106a0c6cefdeef7b0c38d710f`；本地 LICENSE 与远端 Git blob 一致。
- 移植内容：`YuWangClient.cs`、`YuWangClient.Write.cs`、`HtmlParser.cs`、`RsaUtils.cs`、`DiskTree.cs`、权限模型、`UploadAttempt`、`UploadProgress`、现有应用图标。

改动：保留原协议命名空间便于对照；移除 Windows 项目引用；添加平台无关版本信息、调用取消范围、流式整文件下载、目录创建前持久化回调。

测试：34 个纯 HTTP 上传回归场景移植为 `ProtocolTests`；原 CFAPI 身份绑定、原生 Shell 进度、Windows 文件监视器及旧缓存测试不移植，由新队列、文件快照及平台自检覆盖。

未复制账号配置、Cookie、数据库、日志、抓包、服务器地址和构建输出。Windows 原项目不作结构性修改。后续移植先记录来源提交，再对照协议和恢复测试，不整目录覆盖同步代码。
