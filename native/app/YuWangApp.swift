import AppKit
import SwiftUI

@main
struct YuWangLauncher {
    @MainActor static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        let delegate = AppDelegate()
        application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate, NSToolbarItemValidation, NSMenuItemValidation, NSSearchFieldDelegate {
    static weak var shared: AppDelegate?
    var mainWindow: NSWindow?
    private var settingsWindow: NSWindow?
    private var statusItem: NSStatusItem?
    private var statusLine: NSMenuItem?
    private var searchItem: NSSearchToolbarItem?
    private var quitting = false
    private var readyToTerminate = false
    private let model = AppModel.shared
    override init() { super.init(); Self.shared = self; NativeDiagnostics.write("delegate init") }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NativeDiagnostics.write("did finish launching; args=\(CommandLine.arguments)")
        installMenus()
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1180, height: 800), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "育网云"
        window.contentMinSize = NSSize(width: 900, height: 620)
        window.contentViewController = NSHostingController(rootView: RootView(model: model))
        window.setContentSize(NSSize(width: 1180, height: 800))
        window.isReleasedWhenClosed = false
        window.tabbingMode = .disallowed
        window.delegate = self
        let toolbar = NSToolbar(identifier: "YuWangMainToolbar")
        toolbar.delegate = self; toolbar.displayMode = .iconOnly; toolbar.allowsUserCustomization = true; toolbar.autosavesConfiguration = true
        window.toolbar = toolbar; window.toolbarStyle = .unified
        if !window.setFrameUsingName("YuWangMainWindow") { window.center() }
        window.setFrameAutosaveName("YuWangMainWindow")
        mainWindow = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        NativeDiagnostics.write("main window created visible=\(window.isVisible)")
        installStatusItem()
        model.start()
        if model.isSelfTest { Task { await NativeSelfTest.run(model: model) } }
    }
    private func item(_ title: String, _ action: Selector?, _ key: String = "", _ modifiers: NSEvent.ModifierFlags = .command) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = modifiers
        if action != nil { item.target = self }
        return item
    }
    private func installMenus() {
        let bar = NSMenu()
        func add(_ title: String, _ menu: NSMenu) { let root = NSMenuItem(title: title, action: nil, keyEquivalent: ""); root.submenu = menu; bar.addItem(root) }
        let app = NSMenu(title: "育网云")
        app.addItem(item("关于育网云", #selector(about)))
        app.addItem(.separator()); app.addItem(item("设置…", #selector(settings), ",")); app.addItem(.separator())
        let hide = NSMenuItem(title: "隐藏育网云", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h"); app.addItem(hide)
        let hideOthers = NSMenuItem(title: "隐藏其他", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h"); hideOthers.keyEquivalentModifierMask = [.command, .option]; app.addItem(hideOthers)
        app.addItem(NSMenuItem(title: "显示全部", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: ""))
        app.addItem(.separator()); app.addItem(item("退出育网云", #selector(quit), "q")); add("育网云", app)
        let file = NSMenu(title: "文件")
        file.addItem(item("打开", #selector(openSelected), "o")); file.addItem(item("离线下载", #selector(download)))
        file.addItem(item("另存为…", #selector(exportSelected), "s", [.command, .shift])); file.addItem(.separator())
        file.addItem(item("上传文件…", #selector(uploadFiles))); file.addItem(item("上传文件夹…", #selector(uploadFolder)))
        file.addItem(item("新建文件夹…", #selector(newFolder), "n", [.command, .shift])); file.addItem(.separator())
        file.addItem(item("重命名…", #selector(renameSelected))); file.addItem(item("移动到…", #selector(moveSelected)))
        file.addItem(item("移到云端回收站…", #selector(deleteSelected))); file.addItem(.separator())
        file.addItem(NSMenuItem(title: "关闭窗口", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")); add("文件", file)
        let edit = NSMenu(title: "编辑")
        for (title, action, key) in [("撤销", "undo:", "z"), ("剪切", "cut:", "x"), ("复制", "copy:", "c"), ("粘贴", "paste:", "v"), ("全选", "selectAll:", "a")] {
            edit.addItem(NSMenuItem(title: title, action: Selector(action), keyEquivalent: key))
        }
        add("编辑", edit)
        let view = NSMenu(title: "显示")
        view.addItem(item("显示主窗口", #selector(showWindow), "0")); view.addItem(item("刷新", #selector(refresh), "r"))
        view.addItem(item("显示 / 隐藏任务", #selector(toggleTasks))); view.addItem(item("暂停 / 继续同步", #selector(togglePause))); add("显示", view)
        let windows = NSMenu(title: "窗口")
        windows.addItem(NSMenuItem(title: "最小化", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"))
        windows.addItem(NSMenuItem(title: "缩放", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: ""))
        add("窗口", windows); NSApp.windowsMenu = windows
        NSApp.mainMenu = bar
    }
    private func installStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "externaldrive.badge.icloud", accessibilityDescription: "育网云"); item.button?.image?.isTemplate = true
        let menu = NSMenu()
        menu.addItem(self.item("打开育网云", #selector(showWindow)))
        let line = NSMenuItem(title: "正在启动…", action: nil, keyEquivalent: ""); line.isEnabled = false; menu.addItem(line); statusLine = line
        menu.addItem(.separator()); menu.addItem(self.item("暂停 / 继续同步", #selector(togglePause))); menu.addItem(self.item("退出育网云", #selector(quit), "q"))
        item.menu = menu; statusItem = item
    }
    @objc func showWindow() { mainWindow?.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc private func about() { NSApp.orderFrontStandardAboutPanel(options: [.applicationName: "育网云", .applicationVersion: "0.1.0"]) }
    @objc private func settings() {
        if settingsWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 600), styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "育网云设置"; window.contentViewController = NSHostingController(rootView: SettingsView(model: model))
            window.isReleasedWhenClosed = false; window.center(); settingsWindow = window
        }
        settingsWindow?.makeKeyAndOrderFront(nil)
    }
    @objc private func togglePause() { model.perform { _ = try await self.model.send("pause", ["value": self.model.state?.paused != true]) } }
    @objc private func refresh() { model.perform { _ = try await self.model.send("refresh") } }
    @objc private func goBack() { model.goBack() }
    @objc private func goForward() { model.goForward() }
    @objc private func openSelected() { if let entry = model.selectedEntries.first { model.open(entry) } }
    @objc private func download() { model.download() }
    @objc private func uploadFiles() { model.chooseUpload(folders: false) }
    @objc private func uploadFolder() { model.chooseUpload(folders: true) }
    @objc private func newFolder() { model.newFolder() }
    @objc private func exportSelected() { model.exportSelected() }
    @objc private func renameSelected() { model.renameSelected() }
    @objc private func moveSelected() { model.showMove = true }
    @objc private func deleteSelected() { model.deleteSelected() }
    @objc private func toggleTasks() { model.showTasks.toggle() }
    @objc private func quit() {
        guard !quitting else { return }
        Task { await prepareForTermination(); NSApp.terminate(nil) }
    }
    @discardableResult func prepareForTermination() async -> Bool {
        if readyToTerminate { return true }
        quitting = true
        NativeDiagnostics.write("preparing application termination")
        let confirmed = await model.bridge.shutdown()
        readyToTerminate = true
        NativeDiagnostics.write("application ready to terminate; engine confirmed=\(confirmed)")
        return confirmed
    }
    func updateStatus() {
        let text = "\(model.pendingCount) 项待办 · \(model.errorCount) 项需处理"
        statusLine?.title = text; statusItem?.button?.toolTip = "育网云 · " + text
        mainWindow?.title = model.currentFolder?.name ?? "育网云"
        mainWindow?.subtitle = model.state?.activeAccount?.displayName ?? ""
        searchItem?.searchField.stringValue = model.filter
        mainWindow?.toolbar?.validateVisibleItems()
    }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [NSToolbarItem.Identifier("back"), NSToolbarItem.Identifier("forward"), NSToolbarItem.Identifier("refresh"), .flexibleSpace,
         NSToolbarItem.Identifier("upload"), NSToolbarItem.Identifier("download"), NSToolbarItem.Identifier("newFolder"),
         NSToolbarItem.Identifier("more"), NSToolbarItem.Identifier("tasks"), NSToolbarItem.Identifier("search")]
    }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarDefaultItemIdentifiers(toolbar) + [.space] }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier identifier: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        if identifier.rawValue == "search" {
            let item = NSSearchToolbarItem(itemIdentifier: identifier); item.searchField.placeholderString = "筛选当前目录"
            item.searchField.delegate = self; item.searchField.sendsSearchStringImmediately = true; item.label = "搜索"; searchItem = item; return item
        }
        if identifier.rawValue == "upload" || identifier.rawValue == "more" {
            let item = NSMenuToolbarItem(itemIdentifier: identifier); let menu = NSMenu()
            if identifier.rawValue == "upload" {
                item.label = "上传"; item.image = NSImage(systemSymbolName: "square.and.arrow.up", accessibilityDescription: "上传")
                menu.addItem(self.item("上传文件…", #selector(uploadFiles))); menu.addItem(self.item("上传文件夹…", #selector(uploadFolder)))
            } else {
                item.label = "更多"; item.image = NSImage(systemSymbolName: "ellipsis.circle", accessibilityDescription: "更多操作")
                menu.addItem(self.item("另存为…", #selector(exportSelected))); menu.addItem(self.item("重命名…", #selector(renameSelected)))
                menu.addItem(self.item("移动到…", #selector(moveSelected))); menu.addItem(.separator()); menu.addItem(self.item("移到云端回收站…", #selector(deleteSelected)))
                menu.addItem(.separator()); menu.addItem(self.item("设置…", #selector(settings)))
            }
            item.menu = menu; return item
        }
        let specs: [String: (String, String, Selector)] = [
            "back": ("后退", "chevron.left", #selector(goBack)), "forward": ("前进", "chevron.right", #selector(goForward)),
            "refresh": ("刷新", "arrow.clockwise", #selector(refresh)), "download": ("离线下载", "arrow.down.circle", #selector(download)),
            "newFolder": ("新建文件夹", "folder.badge.plus", #selector(newFolder)), "tasks": ("传输与待办", "list.bullet.rectangle", #selector(toggleTasks))]
        guard let spec = specs[identifier.rawValue] else { return nil }
        let item = NSToolbarItem(itemIdentifier: identifier); item.label = spec.0; item.toolTip = spec.0
        item.image = NSImage(systemSymbolName: spec.1, accessibilityDescription: spec.0); item.target = self; item.action = spec.2; return item
    }
    func validateToolbarItem(_ item: NSToolbarItem) -> Bool {
        if item.itemIdentifier.rawValue == "download" { return !model.selection.isEmpty }
        if ["newFolder", "refresh"].contains(item.itemIdentifier.rawValue) { return model.state?.folderId != nil }
        return true
    }
    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        guard let action = item.action else { return false }
        if [#selector(uploadFiles), #selector(uploadFolder), #selector(newFolder)].contains(action) { return model.state?.folderId != nil }
        if [#selector(openSelected), #selector(exportSelected), #selector(moveSelected), #selector(deleteSelected)].contains(action) { return !model.selection.isEmpty }
        if action == #selector(renameSelected) { return model.selection.count == 1 }
        return true
    }
    func controlTextDidChange(_ notification: Notification) { if let search = notification.object as? NSSearchField { model.filter = search.stringValue } }
    func windowShouldClose(_ sender: NSWindow) -> Bool { if !quitting { sender.orderOut(nil); return false }; return true }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        NativeDiagnostics.write("applicationShouldTerminate ready=\(readyToTerminate)")
        if readyToTerminate { return .terminateNow }
        if !quitting {
            quitting = true
            DispatchQueue.main.async {
                Task { await self.prepareForTermination(); NSApp.reply(toApplicationShouldTerminate: true) }
            }
        }
        return .terminateLater
    }
}

@MainActor
enum NativeSelfTest {
    private static var ran = false
    static func run(model: AppModel) async {
        guard !ran else { return }; ran = true
        NativeDiagnostics.write("self-test entered")
        var checks: [String: Bool] = [:]; var error = ""
        let arguments = CommandLine.arguments
        let index = arguments.firstIndex(of: "--output")
        let path = index.flatMap { $0 + 1 < arguments.count ? arguments[$0 + 1] : nil } ?? NSTemporaryDirectory() + "ui-macos.json"
        do {
            try FileManager.default.createDirectory(at: URL(fileURLWithPath: path).deletingLastPathComponent(), withIntermediateDirectories: true)
            for _ in 0..<300 {
                if model.state?.folderId != nil && AppDelegate.shared?.mainWindow?.isVisible == true { break }
                if let failure = model.errorMessage { throw BridgeError(message: failure) }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            guard let state = model.state, let folder = state.folderId else { throw BridgeError(message: "同步引擎未在限时内准备好演示目录。") }
            NativeDiagnostics.write("state ready; windows=\(NSApp.windows.map { "\($0.title):\($0.isVisible)" })")
            checks["native-window"] = AppDelegate.shared?.mainWindow?.isVisible == true
            checks["native-table-data"] = state.entries.contains { !$0.isDirectory }
            checks["native-sidebar-data"] = !model.sidebarNodes.isEmpty
            let result = try await model.send("createFolder", ["parentId": folder, "name": "原生界面自检目录"])
            guard let entryID = result["entryId"] as? String else { throw BridgeError(message: "未返回目录实体。") }
            for _ in 0..<50 {
                if model.state?.operations.contains(where: { $0.entryId == entryID && $0.state == "completed" }) == true { break }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            checks["native-action-to-engine"] = model.state?.operations.contains { $0.entryId == entryID && $0.state == "completed" } == true
            if let file = model.state?.entries.first(where: { !$0.isDirectory }) { model.selection = [file.id] }
            model.showTasks = true
            try await Task.sleep(nanoseconds: 400_000_000)
            if let view = AppDelegate.shared?.mainWindow?.contentView?.superview, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
                view.cacheDisplay(in: view.bounds, to: bitmap)
                let opaque = NSImage(size: view.bounds.size, flipped: false) { rectangle in
                    NSColor.windowBackgroundColor.setFill(); NSBezierPath(rect: rectangle).fill(); bitmap.draw(in: rectangle); return true
                }
                if let tiff = opaque.tiffRepresentation, let rendered = NSBitmapImageRep(data: tiff), let image = rendered.representation(using: .png, properties: [:]) {
                    try image.write(to: URL(fileURLWithPath: path).deletingPathExtension().appendingPathExtension("png"))
                }
            }
        } catch let failure { error = failure.localizedDescription + "\n" + model.bridge.stderr }
        NativeDiagnostics.write("self-test requesting engine shutdown")
        checks["engine-shutdown"] = await AppDelegate.shared?.prepareForTermination() == true
        let passed = checks.count == 5 && checks.values.allSatisfy { $0 } && error.isEmpty
        do {
            let output = URL(fileURLWithPath: path); try FileManager.default.createDirectory(at: output.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: ["passed": passed, "nativeControls": true, "checks": checks, "error": error], options: [.prettyPrinted, .sortedKeys])
            try data.write(to: output, options: .atomic)
        } catch { FileHandle.standardError.write(Data(error.localizedDescription.utf8)) }
        NSApp.terminate(nil)
    }
}

enum NativeDiagnostics {
    static func write(_ message: String) {
        guard CommandLine.arguments.contains("--ui-selftest"), let index = CommandLine.arguments.firstIndex(of: "--output"), index + 1 < CommandLine.arguments.count else { return }
        let path = URL(fileURLWithPath: CommandLine.arguments[index + 1]).deletingPathExtension().appendingPathExtension("trace.log")
        let line = Data((ISO8601DateFormatter().string(from: Date()) + " " + message + "\n").utf8)
        if !FileManager.default.fileExists(atPath: path.path) { _ = FileManager.default.createFile(atPath: path.path, contents: nil) }
        if let handle = try? FileHandle(forWritingTo: path) { defer { try? handle.close() }; _ = try? handle.seekToEnd(); try? handle.write(contentsOf: line) }
    }
}
