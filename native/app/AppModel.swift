import AppKit
import Combine
import SwiftUI
import Network

@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()
    let bridge = EngineBridge()
    @Published var state: ClientState? { didSet { browserIndex = preparedIndex ?? BrowserIndex(state) } }
    private var preparedIndex: BrowserIndex?
    private(set) var browserIndex = BrowserIndex()
    @Published private(set) var sidebarRevealRevision = 0
    private(set) var sidebarRevealID: String?
    @Published var transfer: TransferState?
    @Published var selection = Set<String>() { didSet { if oldValue != selection { scheduleStatusUpdate() } } }
    @Published var taskSelection: String?
    @Published var sidebarSelection: String?
    @Published var filter = ""
    @Published var message = ""
    @Published var errorMessage: String?
    @Published var showLogin = false
    @Published var loginAccount: CloudAccount?
    @Published var showTasks = false
    @Published var collisionIDs: [String] = []
    @Published var showCollisions = false
    @Published var showMove = false
    @Published private(set) var favoriteFolders: [FavoriteFolder]
    @Published var favoritesExpanded: Bool {
        didSet { preferences.set(favoritesExpanded, forKey: "sidebar.favoritesExpanded") }
    }
    private let preferences: UserDefaults
    private var started = false
    private let networkMonitor = NWPathMonitor()
    private var networkCost = "unknown"
    private var reportedNetworkCost: String?
    private var back: [String] = [], forward: [String] = []
    private var statusUpdateScheduled = false
    var isSelfTest: Bool { CommandLine.arguments.contains("--ui-selftest") }
    var isDemo: Bool { isSelfTest || CommandLine.arguments.contains("--demo") }
    var currentFolder: CloudEntry? { guard let id = state?.folderId else { return nil }; return browserIndex.entries[id] }
    var selectedEntries: [CloudEntry] { selection.compactMap { browserIndex.entries[$0] } }
    var fileRows: [CloudEntry] {
        let entries = browserIndex.filesByParent[state?.folderId ?? ""] ?? []
        return filter.isEmpty ? entries : entries.filter { $0.name.localizedCaseInsensitiveContains(filter) }
    }
    var selectedOperation: CloudOperation? { state?.operations.first { $0.id == taskSelection } }
    var pendingCount: Int { state?.operations.filter { !$0.isTerminal }.count ?? 0 }
    var errorCount: Int { state?.operations.filter { ["blocked", "failed", "uncertain"].contains($0.state) }.count ?? 0 }
    var taskSummary: String {
        if pendingCount == 0 { return "没有待办任务" }
        return errorCount > 0 ? "\(pendingCount) 项任务 · \(errorCount) 项需处理" : "\(pendingCount) 项任务"
    }

    init(preferences: UserDefaults? = nil) {
        // Demo and CI must never read or change the user's saved sidebar.
        let demo = CommandLine.arguments.contains("--demo") || CommandLine.arguments.contains("--ui-selftest")
        let defaults = preferences ?? (demo ? UserDefaults(suiteName: "io.github.TOBYmiz.YuWangSync-macOS.demo")! : .standard)
        self.preferences = defaults
        favoriteFolders = defaults.data(forKey: "sidebar.favorites").flatMap { try? JSONDecoder().decode([FavoriteFolder].self, from: $0) } ?? []
        favoritesExpanded = defaults.object(forKey: "sidebar.favoritesExpanded") as? Bool ?? true
    }
    var visibleFavorites: [FavoriteFolder] { favoriteFolders.filter { $0.accountId == state?.activeAccount?.id } }
    func favoriteEntry(_ favorite: FavoriteFolder) -> CloudEntry? {
        guard favorite.accountId == state?.activeAccount?.id else { return nil }
        guard let entry = browserIndex.entries[favorite.entryId], entry.accountId == favorite.accountId, entry.isDirectory else { return nil }
        return entry
    }
    func isFavorite(_ entry: CloudEntry) -> Bool {
        favoriteFolders.contains { $0.accountId == entry.accountId && $0.entryId == entry.id }
    }
    func toggleFavorite(_ entry: CloudEntry) {
        guard entry.isDirectory, entry.accountId == state?.activeAccount?.id else { return }
        if isFavorite(entry) { favoriteFolders.removeAll { $0.accountId == entry.accountId && $0.entryId == entry.id } }
        else { favoriteFolders.append(FavoriteFolder(accountId: entry.accountId, entryId: entry.id, name: entry.name)); favoritesExpanded = true }
        saveFavorites()
    }
    func removeFavorite(_ favorite: FavoriteFolder) {
        favoriteFolders.removeAll { $0.id == favorite.id }; saveFavorites()
    }
    private func saveFavorites() {
        if let data = try? JSONEncoder().encode(favoriteFolders) { preferences.set(data, forKey: "sidebar.favorites") }
    }
    func openFavorite(_ favorite: FavoriteFolder) {
        guard let entry = favoriteEntry(favorite) else { return }
        // Use normal navigation: the engine owns disk/folder session context and locking.
        perform { try await self.navigate(entry.id) }
    }
    func favoritePath(_ favorite: FavoriteFolder) -> String {
        var names: [String] = [], seen = Set<String>(), entry = favoriteEntry(favorite)
        while let current = entry, seen.insert(current.id).inserted {
            names.append(current.name)
            entry = current.parentId.flatMap { browserIndex.entries[$0] }
        }
        return names.reversed().joined(separator: " / ")
    }

    func start() {
        guard !started else { return }; started = true
        NativeDiagnostics.write("model start")
        bridge.onState = { [weak self] state, index in
            guard let self else { return }
            let previous = self.state?.folderId
            if self.state?.activeAccount?.id != state.activeAccount?.id { self.sidebarRevealID = nil }
            self.apply(state, index: index)
            self.reportNetworkCost()
            if let operation = self.transfer?.operationId, !state.operations.contains(where: { $0.id == operation && $0.state == "running" }) { self.transfer = nil }
            NativeDiagnostics.write("state received entries=\(state.entries.count) operations=\(state.operations.count)")
            if previous != state.folderId { self.sidebarSelection = state.folderId; self.selection.removeAll() }
            let valid = self.selection.intersection(Set(self.fileRows.map(\.id)))
            if valid != self.selection { self.selection = valid }
            self.scheduleStatusUpdate()
        }
        bridge.onProgress = { [weak self] progress in self?.transfer = progress }
        bridge.onFailure = { [weak self] error in self?.errorMessage = error }
        do { try bridge.start(demo: isDemo) }
        catch { errorMessage = error.localizedDescription }
        networkMonitor.pathUpdateHandler = { [weak self] path in
            let cost = path.status == .satisfied ? (path.isExpensive || path.usesInterfaceType(.cellular) ? "expensive" : "unmetered") : "unknown"
            Task { @MainActor in self?.networkCost = cost; self?.reportNetworkCost() }
        }
        networkMonitor.start(queue: DispatchQueue(label: "YuWangSync.network-cost"))
    }
    func apply(_ state: ClientState, index: BrowserIndex) {
        preparedIndex = index
        self.state = state
        preparedIndex = nil
    }
    func stopMonitoring() { networkMonitor.cancel() }
    private func reportNetworkCost() {
        guard state != nil, reportedNetworkCost != networkCost else { return }
        reportedNetworkCost = networkCost
        Task { _ = try? await send("networkCost", ["value": networkCost]) }
    }
    func perform(_ action: @escaping @MainActor () async throws -> Void) {
        Task { do { try await action() } catch { errorMessage = error.localizedDescription } }
    }
    private func scheduleStatusUpdate() {
        guard !statusUpdateScheduled else { return }; statusUpdateScheduled = true
        DispatchQueue.main.async { [weak self] in
            self?.statusUpdateScheduled = false
            AppDelegate.shared?.updateStatus()
        }
    }
    @discardableResult
    func send(_ method: String, _ params: [String: Any] = [:]) async throws -> [String: Any] { try await bridge.request(method, params) }
    func navigate(_ id: String, history: Bool = true) async throws {
        sidebarRevealID = id; sidebarRevealRevision += 1
        sidebarSelection = id
        if state?.folderId == id { return }
        if history, let current = state?.folderId { back.append(current); forward.removeAll() }
        filter = ""; selection.removeAll(); _ = try await send("browse", ["folderId": id])
    }
    func goBack() { guard let id = back.popLast() else { return }; if let current = state?.folderId { forward.append(current) }; perform { try await self.navigate(id, history: false) } }
    func goForward() { guard let id = forward.popLast() else { return }; if let current = state?.folderId { back.append(current) }; perform { try await self.navigate(id, history: false) } }
    func goUp() { if let parent = currentFolder?.parentId { perform { try await self.navigate(parent) } } }
    func activate(_ account: String) { guard !account.isEmpty, account != state?.activeAccount?.id else { return }; back.removeAll(); forward.removeAll(); perform { _ = try await self.send("activate", ["accountId": account]) } }
    func open(_ entry: CloudEntry) {
        perform {
            if entry.isDirectory { try await self.navigate(entry.id) }
            else if let path = try await self.send("open", ["entryId": entry.id])["path"] as? String { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
        }
    }
    func download() {
        let ids = Array(selection); guard !ids.isEmpty else { return }
        perform {
            let result = try await self.send("download", ["ids": ids])
            let errors = result["errors"] as? [String] ?? []
            self.message = errors.isEmpty ? "已下载 \(ids.count) 项，可离线使用；云端之后的更新不会自动同步。" : "\(errors.count) 项下载未完成：" + errors.prefix(3).joined(separator: "；")
        }
    }
    func chooseUpload(folders: Bool) {
        guard state?.folderId != nil else { return }
        perform {
            let panel = NSOpenPanel(); panel.canChooseDirectories = folders; panel.canChooseFiles = !folders; panel.allowsMultipleSelection = true
            panel.prompt = "上传"
            let target = self.currentFolder.map { "“\($0.name)”" } ?? "当前文件夹"
            panel.message = folders ? "上传到\(target)。同名文件夹会合并，同名文件会逐项询问处理方式。" : "选择要上传到\(target)的文件。"
            if await self.present(panel) == .OK { try await self.importPaths(panel.urls.map(\.path)) }
        }
    }
    func importPaths(_ paths: [String]) async throws {
        guard let parent = state?.folderId else { return }
        let result = try await send("import", ["parentId": parent, "paths": paths])
        let ids = result["operationIds"] as? [String] ?? []
        let snapshot = try await send("state")
        let current = try JSONDecoder().decode(ClientState.self, from: JSONSerialization.data(withJSONObject: snapshot))
        state = current; collisionIDs = current.operations.filter { ids.contains($0.id) && $0.state == "blocked" }.map(\.id)
        showTasks = true; showCollisions = !collisionIDs.isEmpty
        message = ids.isEmpty ? "没有需要上传的文件。" : "已加入 \(ids.count) 项上传任务，文件快照已保存到本地。"
    }
    func exportSelected() {
        guard let entry = selectedEntries.first else { return }
        perform {
            let panel = NSSavePanel(); panel.nameFieldStringValue = entry.name; panel.prompt = "导出"
            panel.message = "导出的是独立副本，之后的修改不会自动上传。"
            if await self.present(panel) == .OK, let url = panel.url {
                _ = try await self.send("export", ["entryId": entry.id, "destination": url.path, "overwrite": !entry.isDirectory])
                self.message = "已导出“\(entry.name)”。"
            }
        }
    }
    func newFolder() {
        guard let parent = state?.folderId else { return }
        perform { if let name = await self.prompt("新建文件夹", value: "新建文件夹", detail: "在“\(self.currentFolder?.name ?? "当前文件夹")”中创建。") { _ = try await self.send("createFolder", ["parentId": parent, "name": name]); self.showTasks = true } }
    }
    func renameSelected() {
        guard let entry = selectedEntries.first else { return }
        perform { if let name = await self.prompt("重命名“\(entry.name)”", value: entry.name) { _ = try await self.send("rename", ["entryId": entry.id, "name": name]); self.showTasks = true } }
    }
    func deleteSelected() {
        let ids = Array(selection); guard !ids.isEmpty else { return }
        perform {
            if await self.confirm(ids.count == 1 ? "将“\(self.selectedEntries.first?.name ?? "所选项目")”移到云端回收站？" : "将 \(ids.count) 项移到云端回收站？", detail: "离线时会排队，重新连接后会再次检查权限和云端变化。", button: "移到回收站") {
                _ = try await self.send("delete", ["ids": ids]); self.showTasks = true
            }
        }
    }
    func moveSelected(to parent: String) async throws { for entry in selectedEntries { _ = try await send("move", ["entryId": entry.id, "parentId": parent]) }; showTasks = true }
    func revealSelected() { if let path = selectedEntries.first?.localPath { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) } }
    func retryTask() { guard let op = selectedOperation else { return }; perform { _ = try await self.send("retry", ["operationId": op.id]) } }
    func cancelTask() {
        guard let op = selectedOperation else { return }
        perform { if await self.confirm("取消任务“\(op.name)”？", detail: "依赖它的未发送任务会一并取消。已发送的请求会保留记录以便核对结果，本地修改不会被删除。", button: "取消任务") { _ = try await self.send("cancel", ["operationId": op.id]) } }
    }
    func verifyTaskNow() {
        guard let op = selectedOperation, op.state == "awaitingVerification" else { return }
        perform { _ = try await self.send("verifyUpload", ["operationId": op.id]) }
    }
    func overwriteTask() {
        guard let op = selectedOperation else { return }
        perform {
            if await self.confirm("按当前云端状态继续“\(op.name)”？", detail: "会先重新核对云端。上传将覆盖云端当前版本；删除或移动文件夹将以云端当前内容为准。", button: "核对并继续") {
                _ = try await self.send("resolve", ["operationId": op.id, "choice": "replace"])
            }
        }
    }
    func keepBothTask() {
        guard let op = selectedOperation else { return }
        perform {
            let url = URL(fileURLWithPath: op.name); let ext = url.pathExtension
            let name = url.deletingPathExtension().lastPathComponent + "_本地副本" + (ext.isEmpty ? "" : "." + ext)
            if let value = await self.prompt("另存云端副本", value: name) { _ = try await self.send("resolve", ["operationId": op.id, "choice": "rename", "name": value]) }
        }
    }
    func exportTask() {
        guard let op = selectedOperation else { return }
        perform {
            let panel = NSSavePanel(); panel.nameFieldStringValue = op.name; panel.prompt = "导出"
            if await self.present(panel) == .OK, let url = panel.url {
                _ = try await self.send("exportTask", ["operationId": op.id, "destination": url.path, "overwrite": true])
                self.message = "已导出“\(op.name)”的待传内容。"
            }
        }
    }
    func linkFolderTask() {
        guard let op = selectedOperation else { return }
        perform {
            if let text = await self.prompt("关联已创建目录", value: "", detail: "仅在确认云端目录由本次任务创建后填写目录 ID。"), let id = UInt64(text) {
                _ = try await self.send("linkFolder", ["operationId": op.id, "remoteId": id])
            }
        }
    }
    func present(_ panel: NSSavePanel) async -> NSApplication.ModalResponse {
        await withCheckedContinuation { continuation in
            if let window = AppDelegate.shared?.mainWindow { panel.beginSheetModal(for: window) { continuation.resume(returning: $0) } }
            else { panel.begin { continuation.resume(returning: $0) } }
        }
    }
    func confirm(_ title: String, detail: String, button: String) async -> Bool {
        let alert = NSAlert(); alert.messageText = title; alert.informativeText = detail; alert.alertStyle = .warning
        alert.addButton(withTitle: button); alert.addButton(withTitle: "取消")
        return await presentAlert(alert) == .alertFirstButtonReturn
    }
    func prompt(_ title: String, value: String, detail: String = "") async -> String? {
        let alert = NSAlert(); alert.messageText = title; alert.informativeText = detail
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24)); field.stringValue = value; alert.accessoryView = field
        alert.addButton(withTitle: "确定"); alert.addButton(withTitle: "取消"); alert.window.initialFirstResponder = field
        guard await presentAlert(alert) == .alertFirstButtonReturn else { return nil }
        let result = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines); return result.isEmpty ? nil : result
    }
    private func presentAlert(_ alert: NSAlert) async -> NSApplication.ModalResponse {
        await withCheckedContinuation { continuation in
            if let window = AppDelegate.shared?.mainWindow { alert.beginSheetModal(for: window) { continuation.resume(returning: $0) } }
            else { continuation.resume(returning: alert.runModal()) }
        }
    }
    var breadcrumbs: [CloudEntry] {
        guard state != nil else { return [] }; var output: [CloudEntry] = [], seen = Set<String>(); var item = currentFolder
        while let current = item, seen.insert(current.id).inserted { output.append(current); item = current.parentId.flatMap { browserIndex.entries[$0] } }
        return output.reversed()
    }
}
