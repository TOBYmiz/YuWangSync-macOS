# 开源组件与许可

| 组件 | 发布包中的版本 | 许可与来源 |
| --- | --- | --- |
| YuWangSync-macOS | 0.1.0-preview.3 | MIT；Copyright (c) 2026 TOBYmiz；见根目录 LICENSE |
| .NET Runtime | 按固定 SDK 解析的 osx-arm64 runtime pack（当前 10.0.12） | MIT；.NET Foundation and Contributors；运行时自身的完整第三方声明一并打包 |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.14 | MIT；.NET Foundation and Contributors；https://github.com/dotnet/efcore |
| SQLitePCLRaw（core、provider、bundle 与 native 包） | 2.1.13 | Apache-2.0；Copyright 2014-2024 SourceGear, LLC；https://github.com/ericsink/SQLitePCL.raw |
| SQLite | 随 SQLitePCLRaw 原生库提供 | Public Domain；https://sqlite.org/copyright.html |

Microsoft.Data.Sqlite 许可文本取自 EF Core 仓库的 LICENSE.txt；Apache-2.0 文本取自 Apache 官方许可页。两者保存在本目录的 `licenses/` 中。包版本与许可声明另由已还原 NuGet 包的 nuspec 对照确认。

`tools/collect-licenses.py` 在打包时读取实际解析的 .NET runtime pack 的 LICENSE.TXT 与 THIRD-PARTY-NOTICES.TXT，并合并项目许可和上述组件许可，写入应用的 `Contents/Resources/OpenSourceNotices.txt`。关于页直接显示这份随包提供的许可文本。
