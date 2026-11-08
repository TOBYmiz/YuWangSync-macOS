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
        // NavigationSplitView installs its own window toolbar when hosted by AppKit.
        // Keep the split content below the native title bar owned by AppDelegate.
        HSplitView { sidebar; content.frame(maxWidth: .infinity, maxHeight: .infinity) }
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
                        Text("选择账号").tag("").disabled(true)
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
                            ForEach(node.children ?? []) { child in SidebarBranch(model: model, node: child) }
                        }
                    } else {
                        SidebarBranch(model: model, node: node)
                    }
                }
            }.listStyle(.sidebar)
            Divider()
            FavoritesPane(model: model)
            Divider()
            VStack(alignment: .leading, spacing: 5) {
                Label(model.state?.connectionStatus ?? "正在启动…", systemImage: model.state?.offline == true ? "wifi.slash" : "checkmark.icloud")
                    .font(.caption).foregroundStyle(.secondary)
                if model.state?.demo == true { Text("演示数据 · 不连接真实服务器").font(.caption2).foregroundStyle(.secondary) }
            }.frame(maxWidth: .infinity, alignment: .leading).padding(12)
        }.frame(minWidth: 180, idealWidth: 230, maxWidth: 330, maxHeight: .infinity)
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
                HStack(spacing: 6) {
                    Image(nsImage: NSWorkspace.shared.icon(for: entry.isDirectory ? UTType.folder : (UTType(filenameExtension: (entry.name as NSString).pathExtension) ?? UTType.data)))
                        .resizable().frame(width: 16, height: 16)
                    Text(entry.name)
                }.onTapGesture(count: 2) { model.open(entry) }
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
        if model.selectedEntries.count == 1, let entry = model.selectedEntries.first, entry.isDirectory {
            Button(model.isFavorite(entry) ? "取消收藏" : "添加到收藏") { model.toggleFavorite(entry) }
        }
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

struct SidebarBranch: View {
    @ObservedObject var model: AppModel
    let node: SidebarNode
    @State private var expanded = false // Drives and folders start collapsed, including preloaded branches.
    @State private var loading = false
    var body: some View {
        if node.isDiskRoot || node.unloaded || !(node.children ?? []).isEmpty {
            DisclosureGroup(isExpanded: $expanded) {
                ForEach(node.children ?? []) { child in SidebarBranch(model: model, node: child) }
                if node.unloaded {
                    if loading {
                        HStack { ProgressView().controlSize(.small); Text("正在加载…") }.foregroundStyle(.secondary)
                    } else {
                        Button("加载子目录") { load() }.buttonStyle(.plain).foregroundStyle(.secondary)
                    }
                } else if node.isDiskRoot && (node.children ?? []).isEmpty {
                    Text("没有子文件夹").font(.caption).foregroundStyle(.secondary)
                }
            } label: { label }
            .tag(node.id)
            .onChange(of: expanded) { _, value in if value && node.unloaded { load() } }
        } else {
            label.tag(node.id)
        }
    }
    private var label: some View {
        Label(node.title, systemImage: node.symbol).contextMenu {
            if let entry = model.state?.entries.first(where: { $0.id == node.id && $0.isDirectory }) {
                Button(model.isFavorite(entry) ? "取消收藏" : "添加到收藏") { model.toggleFavorite(entry) }
            }
        }
    }
    private func load() {
        guard !loading else { return }; loading = true
        Task { @MainActor in
            defer { loading = false }
            do { _ = try await model.send("loadFolder", ["folderId": node.id]) }
            catch { model.errorMessage = error.localizedDescription }
        }
    }
}

struct FavoritesPane: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Button { model.favoritesExpanded.toggle() } label: {
                    HStack(spacing: 6) {
                        Image(systemName: model.favoritesExpanded ? "chevron.down" : "chevron.right").font(.caption2)
                            .frame(width: 10)
                        Label("收藏", systemImage: "star")
                        Spacer(minLength: 0)
                    }.contentShape(Rectangle())
                }.buttonStyle(.plain).accessibilityLabel(model.favoritesExpanded ? "收起收藏" : "展开收藏")
                    .accessibilityValue(model.favoritesExpanded ? "已展开" : "已收起")
                Button {
                    if let entry = model.currentFolder { model.toggleFavorite(entry) }
                } label: {
                    Image(systemName: model.currentFolder.map { model.isFavorite($0) } == true ? "star.fill" : "plus")
                }.buttonStyle(.borderless).disabled(model.currentFolder == nil)
                    .help(model.currentFolder.map { model.isFavorite($0) } == true ? "取消收藏当前文件夹" : "收藏当前文件夹")
                    .accessibilityLabel(model.currentFolder.map { model.isFavorite($0) } == true ? "取消收藏当前文件夹" : "收藏当前文件夹")
            }.font(.callout).foregroundStyle(.secondary).padding(.horizontal, 12).padding(.vertical, 10)
            if model.favoritesExpanded {
                if model.visibleFavorites.isEmpty {
                    Text("点 + 收藏当前文件夹，也可右键添加。")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 12).padding(.bottom, 12)
                } else {
                    ScrollView {
                        LazyVStack(spacing: 2) {
                            ForEach(model.visibleFavorites) { favorite in
                                let entry = model.favoriteEntry(favorite)
                                Button { model.openFavorite(favorite) } label: {
                                    HStack(spacing: 7) {
                                        Image(systemName: entry == nil ? "folder.badge.questionmark" : (entry?.isDiskRoot == true ? "externaldrive" : "folder"))
                                            .foregroundStyle(.secondary)
                                        Text(entry?.name ?? favorite.name).lineLimit(1).truncationMode(.middle)
                                        Spacer(minLength: 0)
                                    }.padding(.horizontal, 8).padding(.vertical, 6).contentShape(Rectangle())
                                        .background(model.state?.folderId == favorite.entryId ? Color.accentColor.opacity(0.16) : Color.clear, in: RoundedRectangle(cornerRadius: 5))
                                }.buttonStyle(.plain)
                                    .help(entry == nil ? "目录暂不可用，可右键移除收藏。" : model.favoritePath(favorite))
                                    .accessibilityLabel((entry?.name ?? favorite.name) + (entry == nil ? "，目录暂不可用" : "，收藏文件夹"))
                                    .contextMenu { Button("移除收藏") { model.removeFavorite(favorite) } }
                            }
                        }.padding(.horizontal, 6)
                    }.frame(height: min(CGFloat(model.visibleFavorites.count) * 32, 180)).padding(.bottom, 8)
                }
            }
        }.accessibilityIdentifier("sidebar-favorites")
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
                    Button("立即下载校验") { model.verifyTaskNow() }.disabled(model.selectedOperation?.state != "awaitingVerification")
                    Button("导出待传内容…") { model.exportTask() }.disabled(model.selectedOperation?.snapshotPath == nil)
                    Button("关联已创建目录…") { model.linkFolderTask() }.disabled(model.selectedOperation?.kind != "createFolder")
                    Button("取消任务…") { model.cancelTask() }
                }.fixedSize()
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
                Toggle("上传文件后下载校验", isOn: Binding(get: { model.state?.settings.verifyAfterUpload ?? true }, set: { value in
                    model.perform { _ = try await model.send("setVerification", ["verifyAfterUpload": value]) }
                }))
                Toggle("使用数据流量/热点时启用", isOn: Binding(get: { model.state?.settings.verifyOnExpensiveNetwork ?? false }, set: { value in
                    model.perform { _ = try await model.send("setVerification", ["verifyOnExpensiveNetwork": value]) }
                })).padding(.leading, 20).disabled(model.state?.settings.verifyAfterUpload != true)
                Text("校验在空闲时进行，遇到新传输会暂停。覆盖备份及未确认结果保留至必要校验完成。").font(.caption).foregroundStyle(.secondary)
                Text(model.state?.networkCost == "expensive" ? "当前网络：系统识别为数据流量/热点" : model.state?.networkCost == "unmetered" ? "当前网络：普通网络" : "当前网络类型未知，暂缓自动校验")
                    .font(.caption).foregroundStyle(.secondary)
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
            Section { LabeledContent("版本", value: model.state?.version ?? "0.1.0-preview.2"); Text("macOS 原生界面预览版").foregroundStyle(.secondary) }
        }.formStyle(.grouped).padding().frame(width: 540, height: 680)
    }
}
