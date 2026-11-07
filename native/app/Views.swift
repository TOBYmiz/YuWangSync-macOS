import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct RootView: View {
    @ObservedObject var model: AppModel
    @State private var sortOrder = [KeyPathComparator(\CloudEntry.name)]
    @State private var dropTarget = false
    private var rows: [CloudEntry] {
        let entries = (model.state?.entries ?? []).filter { $0.parentId == model.state?.folderId && (model.filter.isEmpty || $0.name.localizedCaseInsensitiveContains(model.filter)) }.sorted(using: sortOrder)
        return entries.filter(\.isDirectory) + entries.filter { !$0.isDirectory }
    }
    var body: some View {
        NavigationSplitView { sidebar } detail: { detail }
            .frame(minWidth: 900, minHeight: 620)
            .onChange(of: model.sidebarSelection) { _, value in
                if let value, !value.hasPrefix("container:"), value != model.state?.folderId {
                    model.perform { try await model.navigate(value) }
                }
            }
            .sheet(isPresented: $model.showLogin) { LoginSheet(model: model, existing: model.loginAccount) }
            .sheet(isPresented: $model.showCollisions) { CollisionSheet(model: model) }
            .sheet(isPresented: $model.showMove) { MoveSheet(model: model) }
            .alert("操作未完成", isPresented: Binding(get: { model.errorMessage != nil && !model.isSelfTest }, set: { if !$0 { model.errorMessage = nil } })) {
                Button("确定") { model.errorMessage = nil }
            } message: { Text(model.errorMessage ?? "") }
            .task { model.start() }
    }
    private var sidebar: some View {
        VStack(spacing: 0) {
            HStack {
                if let state = model.state, !state.accounts.isEmpty {
                    Picker("账号", selection: Binding(get: { state.activeAccount?.id ?? "" }, set: { model.activate($0) })) {
                        Text("选择账号").tag("")
                        ForEach(state.accounts) { account in Text(account.label).tag(account.id) }
                    }.labelsHidden()
                } else { Text("育网云").font(.headline) }
                Spacer()
                Button { model.loginAccount = nil; model.showLogin = true } label: { Image(systemName: "person.badge.plus") }
                    .buttonStyle(.borderless).help("添加账号")
            }.padding(12)
            Divider()
            List(selection: $model.sidebarSelection) {
                ForEach(model.sidebarNodes) { node in
                    if node.id.hasPrefix("container:") {
                        Section(node.title) {
                            OutlineGroup(node.children ?? [], children: \.children) { child in Label(child.title, systemImage: child.symbol).tag(child.id) }
                        }
                    } else {
                        OutlineGroup([node], children: \.children) { child in Label(child.title, systemImage: child.symbol).tag(child.id) }
                    }
                }
            }.listStyle(.sidebar)
            Divider()
            VStack(alignment: .leading, spacing: 5) {
                Label(model.state?.connectionStatus ?? "正在启动…", systemImage: model.state?.offline == true ? "wifi.slash" : "checkmark.icloud")
                    .font(.caption).foregroundStyle(.secondary)
                if model.state?.demo == true { Text("演示数据 · 不连接真实服务器").font(.caption2).foregroundStyle(.secondary) }
            }.frame(maxWidth: .infinity, alignment: .leading).padding(12)
        }.navigationSplitViewColumnWidth(min: 180, ideal: 230, max: 330)
    }
    private var detail: some View {
        content
            .navigationTitle(model.currentFolder?.name ?? "育网云")
            .navigationSubtitle(model.state?.activeAccount?.displayName ?? "")
    }
    @ViewBuilder private var content: some View {
        if model.state?.activeAccount == nil {
            if model.state == nil && model.errorMessage == nil {
                ProgressView("正在打开育网云…").frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ContentUnavailableView {
                    Label("添加育网云账号", systemImage: "externaldrive.badge.icloud")
                } description: { Text("连接服务器后，在这里浏览和管理共享资源。") }
                actions: { Button("添加账号") { model.loginAccount = nil; model.showLogin = true }.buttonStyle(.borderedProminent) }
            }
        } else {
            VStack(spacing: 0) {
                breadcrumbBar
                VSplitView {
                    fileList.frame(minHeight: 240)
                    if model.showTasks { TaskPane(model: model).frame(minHeight: 150, idealHeight: 220, maxHeight: 340) }
                }
                statusBar
            }
        }
    }
    private var fileList: some View {
        Table(rows, selection: $model.selection, sortOrder: $sortOrder) {
            TableColumn("名称", value: \.name) { entry in
                Label(entry.name, systemImage: entry.isDirectory ? "folder.fill" : "doc").onTapGesture(count: 2) { model.open(entry) }
            }.width(min: 180, ideal: 300)
            TableColumn("大小") { (entry: CloudEntry) in Text(entry.sizeLabel).foregroundStyle(.secondary) }.width(min: 65, ideal: 90, max: 110)
            TableColumn("上传时间") { (entry: CloudEntry) in Text(entry.dateLabel).foregroundStyle(.secondary) }.width(min: 130, ideal: 145, max: 180)
            TableColumn("状态") { (entry: CloudEntry) in Text(entry.status).foregroundStyle(.secondary) }.width(min: 110, ideal: 180)
        }
        .overlay {
            if rows.isEmpty {
                ContentUnavailableView(model.filter.isEmpty ? "文件夹为空" : "没有匹配的文件", systemImage: "folder", description: Text(model.filter.isEmpty ? "可以上传文件或新建文件夹。" : "试试其他名称。"))
                    .allowsHitTesting(false)
            }
        }
        .contextMenu { rowMenu }
        .onDrop(of: [UTType.fileURL], isTargeted: $dropTarget, perform: acceptDrop)
    }
    @ViewBuilder private var rowMenu: some View {
        Button("打开") { if let entry = model.selectedEntries.first { model.open(entry) } }
        Button("离线下载") { model.download() }
        Button("另存为…") { model.exportSelected() }
        Button("在访达中显示") { model.revealSelected() }.disabled(model.selectedEntries.first?.localPath == nil)
        Divider()
        Button("重命名…") { model.renameSelected() }
        Button("移动到…") { model.showMove = true }
        Button("移到云端回收站…", role: .destructive) { model.deleteSelected() }
    }
    private func acceptDrop(_ providers: [NSItemProvider]) -> Bool {
        guard model.state?.folderId != nil else { return false }
        model.perform {
            var paths: [String] = []
            for provider in providers {
                let path: String? = await withCheckedContinuation { continuation in
                    provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                        if let data = item as? Data { continuation.resume(returning: URL(dataRepresentation: data, relativeTo: nil)?.path) }
                        else if let url = item as? URL { continuation.resume(returning: url.path) }
                        else { continuation.resume(returning: nil) }
                    }
                }
                if let path { paths.append(path) }
            }
            if !paths.isEmpty { try await model.importPaths(paths) }
        }
        return true
    }
    private var breadcrumbBar: some View {
        HStack(spacing: 8) {
            ForEach(model.breadcrumbs) { entry in
                if entry.id != model.breadcrumbs.first?.id { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary) }
                Button(entry.name) { model.perform { try await model.navigate(entry.id) } }.buttonStyle(.plain).foregroundStyle(entry.id == model.state?.folderId ? .primary : .secondary)
            }
            Spacer()
            if model.state?.offline == true { Label("离线", systemImage: "wifi.slash").font(.caption).foregroundStyle(.secondary) }
        }.font(.callout).padding(.horizontal, 16).padding(.vertical, 9)
    }
    private var statusBar: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 12) {
                Text("\(rows.count) 项").foregroundStyle(.secondary)
                if let transfer = model.transfer {
                    ProgressView(value: transfer.percent, total: 100).frame(width: 100)
                    Text("\(transfer.name) · \(transfer.stage)").lineLimit(1).foregroundStyle(.secondary)
                    Button { model.perform { _ = try await model.send("cancelTransfer") } } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain).help("停止当前下载")
                } else if !model.message.isEmpty { Text(model.message).lineLimit(1).foregroundStyle(.secondary) }
                Spacer()
                Button { model.showTasks.toggle() } label: { Label("\(model.pendingCount) 项待办", systemImage: model.errorCount > 0 ? "exclamationmark.circle" : "list.bullet.rectangle") }.buttonStyle(.plain)
            }.font(.caption).padding(.horizontal, 16).padding(.vertical, 9)
        }
    }
}

struct TaskPane: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("传输与待办").font(.headline); Spacer()
                Button { model.showTasks = false } label: { Image(systemName: "xmark") }.buttonStyle(.plain).foregroundStyle(.secondary)
            }.padding(.horizontal, 14).padding(.vertical, 9)
            Table(model.state?.operations ?? [], selection: $model.taskSelection) {
                TableColumn("操作") { Text($0.kindLabel) }.width(70)
                TableColumn("文件") { Text($0.name) }.width(min: 160, ideal: 250)
                TableColumn("状态") { Text($0.stateLabel) }.width(110)
                TableColumn("说明") { Text($0.error ?? "").foregroundStyle(.secondary).lineLimit(1) }
            }
            HStack(spacing: 10) {
                Button("重试 / 核对") { model.retryTask() }
                Button("确认继续…") { model.overwriteTask() }
                Button("另存云端副本…") { model.keepBothTask() }.disabled(model.selectedOperation?.kind != "upload")
                Menu("更多") {
                    Button("导出待传内容…") { model.exportTask() }.disabled(model.selectedOperation?.snapshotPath == nil)
                    Button("关联已创建目录…") { model.linkFolderTask() }.disabled(model.selectedOperation?.kind != "createFolder")
                    Button("取消任务…") { model.cancelTask() }
                }
                Spacer()
                if let error = model.selectedOperation?.error { Text(error).font(.caption).foregroundStyle(.secondary).lineLimit(2).textSelection(.enabled) }
            }.controlSize(.small).disabled(model.selectedOperation == nil || model.selectedOperation?.state == "completed").padding(10)
        }
    }
}

struct LoginSheet: View {
    @ObservedObject var model: AppModel
    let existing: CloudAccount?
    @Environment(\.dismiss) private var dismiss
    @State private var server = ""
    @State private var username = ""
    @State private var password = ""
    @State private var code = ""
    @State private var status = ""
    @State private var image: NSImage?
    @State private var busy = false
    @State private var captured = ""
    private var credentials: String { server + "\u{0}" + username + "\u{0}" + password }
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(existing == nil ? "添加育网云账号" : "重新登录").font(.title2.bold())
            Form {
                TextField("服务器", text: $server, prompt: Text("http://服务器地址")).disabled(existing != nil)
                TextField("账号", text: $username).disabled(existing != nil)
                SecureField("密码", text: $password, prompt: Text(existing == nil ? "输入密码" : "留空使用钥匙串中的密码"))
                HStack {
                    if let image { Image(nsImage: image).interpolation(.none).resizable().scaledToFit().frame(width: 160, height: 55) }
                    Button("获取验证码") {
                        busy = true; status = "正在获取验证码…"; let capture = credentials
                        Task {
                            do {
                                let reply = try await model.send("beginLogin", ["server": server, "username": username, "password": password])
                                if let text = reply["image"] as? String, let data = Data(base64Encoded: text) { image = NSImage(data: data) }
                                captured = capture; code = ""; status = ""
                            } catch { status = error.localizedDescription }
                            busy = false
                        }
                    }.disabled(busy || server.isEmpty || username.isEmpty || password.isEmpty && existing == nil)
                }
                TextField("验证码", text: $code).disabled(image == nil)
            }.formStyle(.grouped)
            if !status.isEmpty { Text(status).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            HStack {
                if busy { ProgressView().controlSize(.small) }; Spacer()
                Button("取消") { Task { _ = try? await model.send("cancelLogin") }; dismiss() }.keyboardShortcut(.cancelAction)
                Button("登录") {
                    busy = true; status = "正在登录…"
                    Task {
                        do { _ = try await model.send("submitLogin", ["code": code]); dismiss() }
                        catch { status = error.localizedDescription }
                        busy = false
                    }
                }.keyboardShortcut(.defaultAction).disabled(busy || code.isEmpty || captured != credentials)
            }
        }.padding(24).frame(width: 440).interactiveDismissDisabled(busy)
        .onAppear { server = existing?.server ?? ""; username = existing?.username ?? "" }
        .onDisappear { Task { _ = try? await model.send("cancelLogin") } }
    }
}

struct CollisionSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var choices: [String: String] = [:]
    @State private var busy = false
    var conflicts: [CloudOperation] { model.state?.operations.filter { model.collisionIDs.contains($0.id) && !$0.isTerminal } ?? [] }
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("处理同名文件").font(.title2.bold())
            Text("目录已合并。请选择同名文件的处理方式；覆盖前还会重新核对云端版本。").foregroundStyle(.secondary)
            List(conflicts) { op in
                HStack {
                    Text(op.name).lineLimit(1); Spacer()
                    Picker("处理", selection: Binding(get: { choices[op.id] ?? "skip" }, set: { choices[op.id] = $0 })) {
                        Text("跳过").tag("skip"); Text("覆盖").tag("replace"); Text("改名上传").tag("rename")
                    }.labelsHidden().frame(width: 130)
                }
            }.frame(height: 250)
            HStack {
                Button("全部跳过") { for op in conflicts { choices[op.id] = "skip" } }
                Button("全部改名") { for op in conflicts { choices[op.id] = "rename" } }; Spacer()
                Button("稍后处理") { dismiss() }
                Button("应用") {
                    busy = true; let operations = conflicts
                    model.perform {
                        defer { busy = false }
                        for op in operations { _ = try await model.send("resolve", ["operationId": op.id, "choice": choices[op.id] ?? "skip"]) }
                        dismiss()
                    }
                }.keyboardShortcut(.defaultAction).disabled(busy)
            }.controlSize(.small)
        }.padding(24).frame(width: 600)
    }
}

struct MoveSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var selected: String?
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("移动到").font(.title2.bold())
            Text("选择已浏览过的同盘目录。离线时会保存到待办。").foregroundStyle(.secondary)
            List(selection: $selected) {
                ForEach((model.state?.entries ?? []).filter { $0.isDirectory && $0.diskId == model.selectedEntries.first?.diskId }) { entry in
                    Label(entry.name, systemImage: entry.isDiskRoot ? "externaldrive" : "folder").tag(entry.id)
                }
            }.frame(height: 300)
            HStack { Spacer(); Button("取消") { dismiss() }; Button("移动") { if let id = selected { model.perform { try await model.moveSelected(to: id); dismiss() } } }.keyboardShortcut(.defaultAction).disabled(selected == nil) }
        }.padding(24).frame(width: 440)
    }
}

struct SettingsView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        Form {
            Section("账号") {
                LabeledContent("当前账号", value: model.state?.activeAccount?.displayName ?? "未登录")
                HStack {
                    Button("添加账号…") { AppDelegate.shared?.showWindow(); model.loginAccount = nil; model.showLogin = true }
                    Button("重新登录…") { AppDelegate.shared?.showWindow(); model.loginAccount = model.state?.activeAccount; model.showLogin = true }.disabled(model.state?.activeAccount == nil)
                    Button("删除账号…", role: .destructive) {
                        guard let account = model.state?.activeAccount else { return }
                        model.perform { if await model.confirm("删除当前账号？", detail: "有待办或本地修改时会停止删除。", button: "删除账号") { _ = try await model.send("removeAccount", ["accountId": account.id]) } }
                    }.disabled(model.state?.activeAccount == nil)
                }
            }
            Section("同步") {
                Toggle("暂停后台同步", isOn: Binding(get: { model.state?.paused ?? false }, set: { value in model.perform { _ = try await model.send("pause", ["value": value]) } }))
                Toggle("离线模式", isOn: Binding(get: { model.state?.manualOffline ?? false }, set: { value in model.perform { _ = try await model.send("offline", ["value": value]) } }))
                LabeledContent("目录缓存", value: "30 分钟；⌘R 直接刷新云端")
                Text("关闭主窗口后继续在菜单栏运行。退出应用会保存待办。").font(.caption).foregroundStyle(.secondary)
            }
            Section("本地空间") {
                Text("普通打开缓存七天未使用后清理。主动离线下载保留至手动释放；未同步内容、冲突副本和本次打开的文件始终保留。").font(.callout).foregroundStyle(.secondary)
                Button("释放干净副本…") {
                    model.perform {
                        if await model.confirm("释放已下载的干净副本？", detail: "包括主动离线下载；未同步及本次打开的文件会保留。", button: "释放空间") {
                            let result = try await model.send("cleanup", ["allClean": true]); model.message = "已清理 \(result["count"] ?? 0) 个副本。"
                        }
                    }
                }
                Button("打开数据目录") { if let path = model.state?.dataDirectory { NSWorkspace.shared.open(URL(fileURLWithPath: path)) } }
            }
            Section { LabeledContent("版本", value: model.state?.version ?? "0.1.0"); Text("macOS 原生界面预览版").foregroundStyle(.secondary) }
        }.formStyle(.grouped).padding().frame(width: 540, height: 600)
    }
}
