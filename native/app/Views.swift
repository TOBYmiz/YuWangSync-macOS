import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct RootView: View {
    @ObservedObject var model: AppModel
    @State private var dropTarget = false
    private var rows: [CloudEntry] { model.fileRows }
    var body: some View {
        // NavigationSplitView installs its own window toolbar when hosted by AppKit.
        // Keep the split content below the native title bar owned by AppDelegate.
        HSplitView { sidebar; content.frame(maxWidth: .infinity, maxHeight: .infinity) }
            .frame(minWidth: 900, minHeight: 620)
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
            VSplitView {
                NativeDirectoryTree(model: model).frame(minHeight: 120, maxHeight: .infinity)
                FavoritesPane(model: model)
                    .frame(minHeight: model.favoritesExpanded ? 80 : 36,
                           idealHeight: model.favoritesExpanded ? 180 : 36,
                           maxHeight: model.favoritesExpanded ? .infinity : 36)
            }
            Divider()
            connectionFooter
        }.frame(minWidth: 180, idealWidth: 230, maxWidth: 330, maxHeight: .infinity)
    }
    private var connectionSymbol: (name: String, color: Color) {
        guard let state = model.state else { return ("hourglass", .secondary) }
        if state.activeAccount == nil { return ("person.crop.circle.badge.questionmark", .secondary) }
        return state.offline ? ("wifi.slash", .orange) : ("checkmark.icloud", .green)
    }
    private var connectionFooter: some View {
        let status = model.state?.connectionStatus ?? "正在启动…"
        let symbol = connectionSymbol
        return VStack(alignment: .leading, spacing: 5) {
            Label {
                Text(status).lineLimit(2).fixedSize(horizontal: false, vertical: true)
            } icon: {
                Image(systemName: symbol.name).foregroundStyle(symbol.color)
            }.help(status)
            if model.state?.paused == true { Label("后台同步已暂停", systemImage: "pause.circle") }
            if model.state?.demo == true { Label("演示模式 · 不连接真实服务器", systemImage: "theatermasks") }
        }.font(.caption).foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading).padding(12)
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
        NativeFileTable(model: model)
        .overlay {
            if rows.isEmpty {
                if model.filter.isEmpty {
                    ContentUnavailableView("文件夹为空", systemImage: "folder",
                                           description: Text("将文件拖到这里上传，或使用工具栏上传、新建文件夹。"))
                        .allowsHitTesting(false)
                } else {
                    ContentUnavailableView("没有匹配“\(model.filter)”的项目", systemImage: "magnifyingglass",
                                           description: Text("筛选只查找当前文件夹，请检查名称或换个关键词。"))
                        .allowsHitTesting(false)
                }
            }
        }
        .overlay {
            if dropTarget {
                RoundedRectangle(cornerRadius: 8).strokeBorder(Color.accentColor, lineWidth: 2)
                    .background(Color.accentColor.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
                    .overlay(alignment: .bottom) {
                        Label("松开以上传到“\(model.currentFolder?.name ?? "当前文件夹")”", systemImage: "square.and.arrow.up")
                            .font(.callout).padding(.horizontal, 12).padding(.vertical, 6)
                            .background(.regularMaterial, in: Capsule()).padding(14)
                    }
                    .padding(4).allowsHitTesting(false)
            }
        }
        .onDrop(of: [UTType.fileURL], isTargeted: $dropTarget, perform: acceptDrop)
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
        HStack(spacing: 6) {
            ForEach(model.breadcrumbs) { entry in
                let current = entry.id == model.state?.folderId
                if entry.id != model.breadcrumbs.first?.id { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary) }
                Button {
                    model.perform { try await model.navigate(entry.id) }
                } label: {
                    HStack(spacing: 4) {
                        if entry.isDiskRoot { Image(systemName: "externaldrive") }
                        Text(entry.name).lineLimit(1).truncationMode(.middle)
                    }
                }.buttonStyle(.plain).foregroundStyle(current ? .primary : .secondary)
                    .fontWeight(current ? .medium : .regular).layoutPriority(current ? 1 : 0)
                    .help(current ? "当前文件夹" : "前往“\(entry.name)”")
            }
            Spacer(minLength: 8)
            if model.state?.offline == true {
                Label("离线", systemImage: "wifi.slash").font(.caption).foregroundStyle(.orange)
                    .padding(.horizontal, 8).padding(.vertical, 3).background(Color.orange.opacity(0.12), in: Capsule())
                    .help(model.state?.connectionStatus ?? "离线")
            }
        }.font(.callout).padding(.horizontal, 16).padding(.vertical, 9)
    }
    private var itemSummary: String {
        let count = model.selection.count
        if count > 0 { return "已选 \(count) 项，共 \(rows.count) 项" }
        return model.filter.isEmpty ? "\(rows.count) 项" : "筛选出 \(rows.count) 项"
    }
    private var statusBar: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 12) {
                Text(itemSummary).foregroundStyle(.secondary).monospacedDigit()
                if let transfer = model.transfer {
                    Divider().frame(height: 12)
                    ProgressView(value: transfer.percent, total: 100).frame(width: 100)
                    Text(verbatim: "\(Int(transfer.percent))%").monospacedDigit().foregroundStyle(.secondary)
                    Text("\(transfer.name) · \(transfer.stage)").lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                    Button { model.perform { _ = try await model.send("cancelTransfer") } } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help("停止当前下载").accessibilityLabel("停止当前下载")
                } else if !model.message.isEmpty {
                    Divider().frame(height: 12)
                    Text(model.message).lineLimit(1).truncationMode(.tail).foregroundStyle(.secondary).help(model.message)
                }
                Spacer()
                Button { model.showTasks.toggle() } label: {
                    Label(model.taskSummary, systemImage: model.errorCount > 0 ? "exclamationmark.circle.fill" : "list.bullet.rectangle")
                        .foregroundStyle(model.errorCount > 0 ? Color.orange : Color.secondary)
                }.buttonStyle(.plain).help(model.showTasks ? "隐藏任务面板" : "显示任务面板")
            }.font(.caption).padding(.horizontal, 16).padding(.vertical, 7)
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
                    }.frame(maxHeight: .infinity).padding(.bottom, 8)
                }
            }
        }.frame(maxWidth: .infinity, maxHeight: model.favoritesExpanded ? .infinity : nil, alignment: .top)
            .accessibilityIdentifier("sidebar-favorites")
    }
}

struct TaskPane: View {
    @ObservedObject var model: AppModel
    private var operations: [CloudOperation] { model.state?.operations ?? [] }
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text("传输与任务").font(.headline)
                Text(model.taskSummary).font(.caption).foregroundStyle(model.errorCount > 0 ? Color.orange : Color.secondary)
                Spacer()
                Button { model.showTasks = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless).foregroundStyle(.secondary).help("隐藏任务面板").accessibilityLabel("隐藏任务面板")
            }.padding(.horizontal, 14).padding(.vertical, 9)
            Table(operations, selection: $model.taskSelection) {
                TableColumn("操作") { Text($0.kindLabel) }.width(70)
                TableColumn("文件") { Text($0.name).lineLimit(1).truncationMode(.middle).help($0.name) }.width(min: 160, ideal: 250)
                TableColumn("状态") { op in
                    let style = TaskPane.style(op.state)
                    Label { Text(op.stateLabel) } icon: { Image(systemName: style.symbol).foregroundStyle(style.color) }
                }.width(min: 110, ideal: 130)
                TableColumn("说明") { Text($0.error ?? "").foregroundStyle(.secondary).lineLimit(1).help($0.error ?? "") }
            }
            .overlay {
                if operations.isEmpty {
                    ContentUnavailableView("没有任务", systemImage: "checkmark.circle",
                                           description: Text("上传、新建、改名、移动和删除会在这里排队执行。"))
                        .allowsHitTesting(false)
                }
            }
            HStack(spacing: 10) {
                Button("重试 / 核对") { model.retryTask() }.help("重新执行任务；结果不明确时先核对云端")
                Button("按云端状态继续…") { model.overwriteTask() }.help("重新核对云端后继续，必要时覆盖云端版本")
                Button("另存云端副本…") { model.keepBothTask() }.disabled(model.selectedOperation?.kind != "upload")
                    .help("以新名称上传本地内容，保留云端现有文件")
                Menu("更多") {
                    Button("立即下载校验") { model.verifyTaskNow() }.disabled(model.selectedOperation?.state != "awaitingVerification")
                    Button("导出待传内容…") { model.exportTask() }.disabled(model.selectedOperation?.snapshotPath == nil)
                    Button("关联已创建目录…") { model.linkFolderTask() }.disabled(model.selectedOperation?.kind != "createFolder")
                    Divider()
                    Button("取消任务…", role: .destructive) { model.cancelTask() }
                }.fixedSize()
                Spacer()
                if model.selectedOperation == nil {
                    Text("选择一项任务以处理").font(.caption).foregroundStyle(.tertiary)
                } else if let error = model.selectedOperation?.error {
                    Text(error).font(.caption).foregroundStyle(.secondary).lineLimit(2).textSelection(.enabled)
                }
            }.controlSize(.small).disabled(model.selectedOperation == nil || model.selectedOperation?.state == "completed").padding(10)
        }
    }
    static func style(_ state: String) -> (symbol: String, color: Color) {
        switch state {
        case "running": return ("arrow.triangle.2.circlepath", .accentColor)
        case "queued": return ("clock", .secondary)
        case "awaitingVerification": return ("checkmark.shield", .secondary)
        case "blocked", "uncertain": return ("exclamationmark.triangle.fill", .orange)
        case "failed": return ("xmark.octagon.fill", .red)
        case "completed": return ("checkmark.circle.fill", .green)
        default: return ("minus.circle", .secondary)
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
    @State private var failed = false
    private var credentials: String { server + "\u{0}" + username + "\u{0}" + password }
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 6) {
                Text(existing == nil ? "添加育网云账号" : "重新登录").font(.title2.bold())
                Text(existing == nil ? "填写服务器、账号和密码，获取验证码后登录。密码保存在钥匙串中。"
                                     : "重新获取验证码以恢复与服务器的会话。")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Form {
                TextField("服务器", text: $server, prompt: Text("例如 http://10.0.0.1")).disabled(existing != nil)
                TextField("账号", text: $username).disabled(existing != nil)
                SecureField("密码", text: $password, prompt: Text(existing == nil ? "输入密码" : "留空使用钥匙串中的密码"))
                HStack {
                    if let image { Image(nsImage: image).interpolation(.none).resizable().scaledToFit().frame(width: 160, height: 55) }
                    Button(image == nil ? "获取验证码" : "换一张") {
                        busy = true; failed = false; status = "正在获取验证码…"; let capture = credentials
                        Task {
                            do {
                                let reply = try await model.send("beginLogin", ["server": server, "username": username, "password": password])
                                if let text = reply["image"] as? String, let data = Data(base64Encoded: text) { image = NSImage(data: data) }
                                captured = capture; code = ""; status = ""
                            } catch { status = error.localizedDescription; failed = true }
                            busy = false
                        }
                    }.help(image == nil ? "向服务器请求验证码图片" : "看不清时换一张验证码").disabled(busy || server.isEmpty || username.isEmpty || password.isEmpty && existing == nil)
                }
                TextField("验证码", text: $code, prompt: Text(image == nil ? "请先获取验证码" : "输入图片中的字符")).disabled(image == nil)
            }.formStyle(.grouped)
            if !status.isEmpty {
                Label(status, systemImage: failed ? "exclamationmark.triangle.fill" : "info.circle")
                    .font(.callout).foregroundStyle(failed ? Color.red : Color.secondary)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            } else if image != nil && captured != credentials {
                Label("账号信息已修改，请重新获取验证码。", systemImage: "arrow.clockwise")
                    .font(.callout).foregroundStyle(.secondary)
            }
            HStack {
                if busy { ProgressView().controlSize(.small) }; Spacer()
                Button("取消") { Task { _ = try? await model.send("cancelLogin") }; dismiss() }.keyboardShortcut(.cancelAction)
                Button("登录") {
                    busy = true; failed = false; status = "正在登录…"
                    Task {
                        do { _ = try await model.send("submitLogin", ["code": code]); dismiss() }
                        catch { status = error.localizedDescription; failed = true }
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
            Text(conflicts.isEmpty ? "处理同名文件" : "处理 \(conflicts.count) 个同名文件").font(.title2.bold())
            Text("同名文件夹已合并。请为每个同名文件选择处理方式；覆盖前会再次核对云端版本。")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            List(conflicts) { op in
                HStack {
                    Image(systemName: "doc").foregroundStyle(.secondary)
                    Text(op.name).lineLimit(1).truncationMode(.middle).help(op.name); Spacer()
                    Picker("处理", selection: Binding(get: { choices[op.id] ?? "skip" }, set: { choices[op.id] = $0 })) {
                        Text("跳过").tag("skip"); Text("覆盖").tag("replace"); Text("改名上传").tag("rename")
                    }.labelsHidden().frame(width: 130)
                }
            }.frame(height: 250)
            .overlay {
                if conflicts.isEmpty {
                    ContentUnavailableView("没有待处理的同名文件", systemImage: "checkmark.circle").allowsHitTesting(false)
                }
            }
            HStack {
                Button("全部跳过") { for op in conflicts { choices[op.id] = "skip" } }
                Button("全部改名") { for op in conflicts { choices[op.id] = "rename" } }; Spacer()
                Button("稍后处理") { dismiss() }.keyboardShortcut(.cancelAction).help("保留在任务列表中，稍后再选择")
                Button("应用") {
                    busy = true; let operations = conflicts
                    model.perform {
                        defer { busy = false }
                        for op in operations { _ = try await model.send("resolve", ["operationId": op.id, "choice": choices[op.id] ?? "skip"]) }
                        dismiss()
                    }
                }.keyboardShortcut(.defaultAction).disabled(busy || conflicts.isEmpty)
            }.controlSize(.small)
        }.padding(24).frame(width: 600)
    }
}

struct MoveSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var selected: String?
    private var title: String {
        let items = model.selectedEntries
        return items.count == 1 ? "移动“\(items[0].name)”" : "移动 \(items.count) 项"
    }
    private var targets: [CloudEntry] {
        let moving = model.selection
        let disk = model.selectedEntries.first?.diskId
        return (model.state?.entries ?? []).filter { $0.isDirectory && $0.diskId == disk && !moving.contains($0.id) }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(title).font(.title2.bold()).lineLimit(1).truncationMode(.middle)
            Text("只能移动到同一共享盘中已浏览过的文件夹。离线时会加入待办，联网后执行。")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            List(selection: $selected) {
                ForEach(targets) { entry in
                    Label(entry.name, systemImage: entry.isDiskRoot ? "externaldrive" : "folder").tag(entry.id)
                }
            }.frame(height: 300)
            .overlay {
                if targets.isEmpty {
                    ContentUnavailableView("没有可用的目标文件夹", systemImage: "folder.badge.questionmark",
                                           description: Text("先在侧边栏展开或浏览目标文件夹。"))
                        .allowsHitTesting(false)
                }
            }
            HStack {
                Spacer()
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("移动到此处") { if let id = selected { model.perform { try await model.moveSelected(to: id); dismiss() } } }
                    .keyboardShortcut(.defaultAction).disabled(selected == nil || selected == model.state?.folderId)
            }
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
                        model.perform { if await model.confirm("删除账号“\(account.displayName)”？", detail: "会移除此账号的本地记录和钥匙串中保存的密码。如有未完成任务或本地修改，将停止删除。", button: "删除账号") { _ = try await model.send("removeAccount", ["accountId": account.id]) } }
                    }.disabled(model.state?.activeAccount == nil)
                }
            }
            Section("上传校验") {
                Toggle("上传文件后下载校验", isOn: Binding(get: { model.state?.settings.verifyAfterUpload ?? true }, set: { value in
                    model.perform { _ = try await model.send("setVerification", ["verifyAfterUpload": value]) }
                }))
                Toggle("使用数据流量/热点时启用", isOn: Binding(get: { model.state?.settings.verifyOnExpensiveNetwork ?? false }, set: { value in
                    model.perform { _ = try await model.send("setVerification", ["verifyOnExpensiveNetwork": value]) }
                })).padding(.leading, 20).disabled(model.state?.settings.verifyAfterUpload != true)
                Text("校验在空闲时进行，有新传输时自动暂停。覆盖前的备份和结果不明确的上传会保留到校验完成。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                LabeledContent("当前网络") {
                    Label(networkLabel.text, systemImage: networkLabel.symbol).foregroundStyle(.secondary)
                }
            }
            Section("后台同步") {
                Toggle("暂停后台同步", isOn: Binding(get: { model.state?.paused ?? false }, set: { value in model.perform { _ = try await model.send("pause", ["value": value]) } }))
                    .help("暂停后，待办任务保留在队列中，恢复后继续执行")
                Toggle("离线模式", isOn: Binding(get: { model.state?.manualOffline ?? false }, set: { value in model.perform { _ = try await model.send("offline", ["value": value]) } }))
                    .help("不连接服务器，只浏览本地缓存；修改会加入待办")
                LabeledContent("目录缓存", value: "30 分钟（按 ⌘R 立即刷新）")
                Text("关闭主窗口后，育网云会继续在菜单栏运行；退出应用时会保存待办。").font(.caption).foregroundStyle(.secondary)
            }
            Section("本地空间") {
                Text("双击打开产生的缓存超过七天未使用会自动清理；主动离线下载会保留到手动释放。未同步的修改、冲突副本和本次打开的文件始终保留。")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Button("释放已下载副本…") {
                    model.perform {
                        if await model.confirm("释放已下载的副本？", detail: "会删除未修改过的本地副本，包括主动离线下载的内容；未同步的修改和本次打开的文件会保留。", button: "释放空间") {
                            let result = try await model.send("cleanup", ["allClean": true]); model.message = "已释放 \(result["count"] ?? 0) 个本地副本。"
                        }
                    }
                }
                Button("打开数据目录") { if let path = model.state?.dataDirectory { NSWorkspace.shared.open(URL(fileURLWithPath: path)) } }
            }
            Section("关于") {
                LabeledContent("版本", value: model.state?.version ?? "0.1.0-preview.3")
                Text("预览版：部分功能仍在验证中，重要文件请保留备份。").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).padding().frame(width: 540, height: 720)
    }
    private var networkLabel: (text: String, symbol: String) {
        switch model.state?.networkCost ?? "" {
        case "expensive": return ("数据流量 / 个人热点", "antenna.radiowaves.left.and.right")
        case "unmetered": return ("普通网络", "wifi")
        default: return ("未知（暂缓自动校验）", "questionmark.circle")
        }
    }
}
