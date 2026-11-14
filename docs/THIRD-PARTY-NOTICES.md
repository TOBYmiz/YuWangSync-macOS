# 开源组件与许可

| 组件                                               | 发布包中的版本                         | 许可与来源                                                                                 |
| -------------------------------------------------- | -------------------------------------- | ------------------------------------------------------------------------------------------ |
| YuWangSync-macOS                                   | 1.0.0                                  | MIT；Copyright (c) 2026 TOBYmiz；见根目录 LICENSE                                          |
| .NET Runtime                                       | osx-arm64 runtime pack（当前 10.0.12） | MIT；.NET Foundation and Contributors；完整第三方声明随运行时打包                          |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.14                                | MIT；.NET Foundation and Contributors；https://github.com/dotnet/efcore                    |
| SQLitePCLRaw（core、provider、bundle、native）     | 2.1.13                                 | Apache-2.0；Copyright 2014-2024 SourceGear, LLC；https://github.com/ericsink/SQLitePCL.raw |
| SQLite                                             | 随 SQLitePCLRaw 原生库                 | Public Domain；https://sqlite.org/copyright.html                                           |

许可文本存于 `licenses/`：Microsoft.Data.Sqlite 取自 EF Core 仓库 LICENSE.txt，Apache-2.0 取自 Apache 官方许可页。包版本与许可声明经 NuGet 包的 nuspec 对照确认。

`tools/collect-licenses.py` 在打包时读取实际解析的 .NET runtime pack 的 LICENSE.TXT 与 THIRD-PARTY-NOTICES.TXT，合并项目及上述组件许可，写入 `Contents/Resources/OpenSourceNotices.txt`。关于页显示这份文本。
