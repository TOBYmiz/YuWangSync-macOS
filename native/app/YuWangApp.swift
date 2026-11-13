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
    private var aboutWindow: NSWindow?
    private var statusItem: NSStatusItem?
    private var statusLine: NSMenuItem?
    private var searchItem: NSSearchToolbarItem?
    private var toolbarSymbols: [String: (symbol: String, description: String)] = [:]
    private var appearanceObservation: NSKeyValueObservation?
    private var colorObservation: NSObjectProtocol?
    private var quitting = false
    private var readyToTerminate = false
    private let model = AppModel.shared
    override init() { super.init(); Self.shared = self; NativeDiagnostics.write("delegate init") }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NativeDiagnostics.write("did finish launching; args=\(CommandLine.arguments)")
        installMenus()
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1180, height: 800), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        // Leave title-bar geometry and traffic-light placement to AppKit. Content
        // must not extend into this area through fullSizeContentView.
        window.titlebarAppearsTransparent = false
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
        let titleSpacing = NSTitlebarAccessoryViewController()
        titleSpacing.layoutAttribute = .left
        titleSpacing.view = NSView(frame: NSRect(x: 0, y: 0, width: 8, height: 20))
        window.addTitlebarAccessoryViewController(titleSpacing)
        if !window.setFrameUsingName("YuWangMainWindow") { window.center() }
        window.setFrameAutosaveName("YuWangMainWindow")
        mainWindow = window
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            Task { @MainActor in self?.refreshToolbarAppearance() }
        }
        colorObservation = NotificationCenter.default.addObserver(forName: NSColor.systemColorsDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.refreshToolbarAppearance() }
        }
        window.makeKeyAndOrderFront(nil)
        removeLegacyToolbarGap(toolbar)
        refreshToolbarAppearance()
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
    @objc private func about() { _ = showAboutWindow() }
    func showAboutWindow() -> NSWindow {
        if aboutWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 620), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            window.title = "关于育网云"; window.contentViewController = NSHostingController(rootView: AboutView())
            window.contentMinSize = NSSize(width: 520, height: 480)
            window.isReleasedWhenClosed = false; window.center(); aboutWindow = window
        }
        aboutWindow!.makeKeyAndOrderFront(nil); return aboutWindow!
    }
    @objc private func settings() { _ = showSettingsWindow() }
    func showSettingsWindow() -> NSWindow {
        if settingsWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 680), styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "育网云设置"; window.contentViewController = NSHostingController(rootView: SettingsView(model: model))
            window.isReleasedWhenClosed = false; window.center(); settingsWindow = window
        }
        settingsWindow!.makeKeyAndOrderFront(nil); return settingsWindow!
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
        model.stopMonitoring()
        NativeDiagnostics.write("preparing application termination")
        let confirmed = await model.bridge.shutdown()
        readyToTerminate = true
        NativeDiagnostics.write("application ready to terminate; engine confirmed=\(confirmed)")
        return confirmed
    }
    func updateStatus() {
        let text = (model.state?.paused == true ? "同步已暂停 · " : "") + model.taskSummary
        statusLine?.title = text; statusItem?.button?.toolTip = "育网云 · " + text
        // The sidebar picker shows the account; keep the native title on one centered line.
        mainWindow?.title = model.currentFolder?.name ?? "育网云"
        if searchItem?.searchField.stringValue != model.filter { searchItem?.searchField.stringValue = model.filter }
        mainWindow?.toolbar?.validateVisibleItems()
    }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.flexibleSpace, NSToolbarItem.Identifier("back"), NSToolbarItem.Identifier("forward"), NSToolbarItem.Identifier("refresh"),
         NSToolbarItem.Identifier("upload"), NSToolbarItem.Identifier("download"), NSToolbarItem.Identifier("newFolder"),
         NSToolbarItem.Identifier("more"), .space, NSToolbarItem.Identifier("search")]
    }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        toolbarDefaultItemIdentifiers(toolbar).filter { $0 != .space } + [.space]
    }
    private func removeLegacyToolbarGap(_ toolbar: NSToolbar) {
        // Upgrade the old saved layout too, without resetting the user's other customizations.
        for index in toolbar.items.indices.reversed() where toolbar.items[index].itemIdentifier.rawValue == "tasks" {
            toolbar.removeItem(at: index)
        }
        let items = toolbar.items
        if items.count >= 3 {
            for index in (1..<(items.count - 1)).reversed() {
                if items[index].itemIdentifier == .space,
                   items[index - 1].itemIdentifier.rawValue == "newFolder",
                   items[index + 1].itemIdentifier.rawValue == "more" { toolbar.removeItem(at: index) }
            }
        }
        let oldPrefix = ["back", "forward", "refresh", NSToolbarItem.Identifier.flexibleSpace.rawValue]
        if toolbar.items.prefix(4).map({ $0.itemIdentifier.rawValue }) == oldPrefix {
            toolbar.removeItem(at: 3)
            toolbar.insertItem(withItemIdentifier: .flexibleSpace, at: 0)
        }
    }
    private func toolbarImage(_ symbol: String, description: String, primary: Bool) -> NSImage? {
        guard let symbolImage = NSImage(systemSymbolName: symbol, accessibilityDescription: description)?
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 16, weight: .medium, scale: .medium)) else { return nil }
        let source = croppedToolbarSymbol(symbolImage)
        guard source.size.width > 0, source.size.height > 0 else { return nil }
        // Equal canvases give the native buttons consistent sizes; fit and center each symbol inside.
        let canvas = NSSize(width: 20, height: 20)
        let image = NSImage(size: canvas, flipped: false) { rect in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            context.saveGState(); context.beginTransparencyLayer(auxiliaryInfo: nil)
            let box = rect.insetBy(dx: 1, dy: 1)
            let scale = min(1, min(box.width / source.size.width, box.height / source.size.height))
            let size = NSSize(width: source.size.width * scale, height: source.size.height * scale)
            source.draw(in: NSRect(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2, width: size.width, height: size.height))
            (primary ? NSColor.controlAccentColor : NSColor.controlTextColor).setFill(); rect.fill(using: .sourceIn)
            context.endTransparencyLayer(); context.restoreGState()
            return true
        }
        image.alignmentRect = NSRect(origin: .zero, size: canvas)
        image.isTemplate = false; image.accessibilityDescription = description
        return image
    }
    private func croppedToolbarSymbol(_ image: NSImage) -> NSImage {
        // Symbol canvases contain different optical margins. Measure the visible mask at 4x resolution.
        let factor: CGFloat = 4
        let width = max(1, Int(ceil(image.size.width * factor))), height = max(1, Int(ceil(image.size.height * factor)))
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8,
            samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bitmapFormat: [], bytesPerRow: 0, bitsPerPixel: 0),
              let context = NSGraphicsContext(bitmapImageRep: bitmap), let pixels = bitmap.bitmapData else { return image }
        let previous = NSGraphicsContext.current
        NSGraphicsContext.current = context
        let bounds = NSRect(x: 0, y: 0, width: width, height: height)
        context.cgContext.clear(bounds); image.draw(in: bounds)
        NSGraphicsContext.current = previous
        var minX = width, minY = height, maxX = -1, maxY = -1
        for y in 0..<height {
            for x in 0..<width where pixels[y * bitmap.bytesPerRow + x * 4 + 3] > 0 {
                minX = min(minX, x); minY = min(minY, y); maxX = max(maxX, x); maxY = max(maxY, y)
            }
        }
        guard maxX >= minX, maxY >= minY,
              let cropped = bitmap.cgImage?.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1)) else { return image }
        return NSImage(cgImage: cropped, size: NSSize(width: CGFloat(cropped.width) / factor, height: CGFloat(cropped.height) / factor))
    }
    private func setToolbarImage(_ item: NSToolbarItem, symbol: String, description: String) {
        toolbarSymbols[item.itemIdentifier.rawValue] = (symbol, description)
        item.image = toolbarImage(symbol, description: description, primary: item.itemIdentifier.rawValue == "upload")
    }
    func refreshToolbarAppearance() {
        for item in mainWindow?.toolbar?.items ?? [] {
            if let spec = toolbarSymbols[item.itemIdentifier.rawValue] {
                item.image = toolbarImage(spec.symbol, description: spec.description, primary: item.itemIdentifier.rawValue == "upload")
            }
        }
        if let window = mainWindow {
            window.effectiveAppearance.performAsCurrentDrawingAppearance {
                searchItem?.searchField.layer?.borderColor = NSColor.controlTextColor.withAlphaComponent(0.22).cgColor
            }
        }
        mainWindow?.toolbar?.validateVisibleItems()
    }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier identifier: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        if identifier.rawValue == "search" {
            let item = NSSearchToolbarItem(itemIdentifier: identifier); item.searchField.placeholderString = "筛选当前目录"
            item.preferredWidthForSearchField = 180
            item.searchField.wantsLayer = true
            item.searchField.layer?.cornerRadius = 6
            item.searchField.layer?.borderWidth = 1
            item.searchField.layer?.borderColor = NSColor.controlTextColor.withAlphaComponent(0.22).cgColor
            // Bound the native field so it does not consume the flexible space
            // separating navigation from file actions.
            NSLayoutConstraint.activate([
                item.searchField.widthAnchor.constraint(greaterThanOrEqualToConstant: 160),
                item.searchField.widthAnchor.constraint(lessThanOrEqualToConstant: 180)
            ])
            item.searchField.delegate = self; item.searchField.sendsSearchStringImmediately = true; item.label = "搜索"; searchItem = item; return item
        }
        if identifier.rawValue == "upload" || identifier.rawValue == "more" {
            let item = NSMenuToolbarItem(itemIdentifier: identifier); let menu = NSMenu()
            item.showsIndicator = false; item.isBordered = true
            if identifier.rawValue == "upload" {
                item.label = "上传"; setToolbarImage(item, symbol: "square.and.arrow.up", description: "上传")
                menu.addItem(self.item("上传文件…", #selector(uploadFiles))); menu.addItem(self.item("上传文件夹…", #selector(uploadFolder)))
            } else {
                item.label = "更多"; setToolbarImage(item, symbol: "ellipsis.circle", description: "更多操作")
                menu.addItem(self.item("另存为…", #selector(exportSelected))); menu.addItem(self.item("重命名…", #selector(renameSelected)))
                menu.addItem(self.item("移动到…", #selector(moveSelected))); menu.addItem(.separator()); menu.addItem(self.item("移到云端回收站…", #selector(deleteSelected)))
                menu.addItem(.separator()); menu.addItem(self.item("设置…", #selector(settings)))
            }
            item.toolTip = item.label; item.menu = menu; return item
        }
        let specs: [String: (String, String, Selector)] = [
            "back": ("后退", "chevron.left", #selector(goBack)), "forward": ("前进", "chevron.right", #selector(goForward)),
            "refresh": ("刷新", "arrow.clockwise", #selector(refresh)), "download": ("离线下载", "square.and.arrow.down", #selector(download)),
            "newFolder": ("新建文件夹", "folder.badge.plus", #selector(newFolder))]
        guard let spec = specs[identifier.rawValue] else { return nil }
        let item = NSToolbarItem(itemIdentifier: identifier); item.label = spec.0; item.toolTip = spec.0
        item.isBordered = true
        setToolbarImage(item, symbol: spec.1, description: spec.0); item.target = self; item.action = spec.2; return item
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
        var checks: [String: Bool] = [:]; var chrome: [String: Any] = [:]; var error = ""
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
            checks["native-sidebar-data"] = !model.browserIndex.directories.roots.isEmpty
            let disks = model.browserIndex.directories.rows.values.filter(\.isDiskRoot)
            checks["sidebar-drives-collapsible"] = !disks.isEmpty && disks.count == state.entries.filter(\.isDiskRoot).count
            let interaction = try await NativeBrowserSelfTest.run(model: model, output: path)
            checks.merge(interaction) { _, value in value }
            // Test saved folder references with an isolated domain, never real preferences.
            guard let favoriteTarget = state.entries.first(where: { $0.isDirectory && !$0.isDiskRoot && $0.id != folder }) else {
                throw BridgeError(message: "收藏自检缺少演示目录。")
            }
            let suiteName = "YuWangSync.favorites-selftest." + UUID().uuidString
            let defaults = UserDefaults(suiteName: suiteName)!
            defer { defaults.removePersistentDomain(forName: suiteName) }
            let preferences = AppModel(preferences: defaults); preferences.state = state
            checks["favorites-default-expanded"] = preferences.favoritesExpanded && preferences.visibleFavorites.isEmpty
            preferences.toggleFavorite(favoriteTarget)
            let favorite = preferences.visibleFavorites.first!
            checks["favorites-height-default"] = preferences.favoritesHeight == AppModel.defaultFavoritesHeight
            preferences.favoritesExpanded = false
            preferences.favoritesHeight = 260
            let restored = AppModel(preferences: defaults); restored.state = state
            checks["favorites-persist"] = restored.visibleFavorites == [favorite] && !restored.favoritesExpanded
            checks["favorites-height-persist"] = restored.favoritesHeight == 260
            var otherState = try await model.send("state")
            otherState["activeAccount"] = ["id": "other-account", "server": "https://example.invalid", "username": "other", "displayName": "其他账号"]
            restored.state = try JSONDecoder().decode(ClientState.self, from: JSONSerialization.data(withJSONObject: otherState))
            restored.toggleFavorite(favoriteTarget)
            checks["favorites-account-isolation"] = restored.visibleFavorites.isEmpty && restored.favoriteFolders == [favorite] && restored.favoriteEntry(favorite) == nil
            restored.state = state
            restored.toggleFavorite(favoriteTarget)
            checks["favorites-remove"] = restored.visibleFavorites.isEmpty && AppModel(preferences: defaults).favoriteFolders.isEmpty
            restored.toggleFavorite(favoriteTarget)
            var unavailableState = try await model.send("state")
            unavailableState["entries"] = (unavailableState["entries"] as? [[String: Any]] ?? []).filter { $0["id"] as? String != favorite.entryId }
            restored.state = try JSONDecoder().decode(ClientState.self, from: JSONSerialization.data(withJSONObject: unavailableState))
            checks["favorites-unavailable-retained"] = restored.visibleFavorites == [favorite] && restored.favoriteEntry(favorite) == nil
            if model.isFavorite(favoriteTarget) { model.toggleFavorite(favoriteTarget) }
            model.toggleFavorite(favoriteTarget)
            model.openFavorite(model.visibleFavorites.first { $0.entryId == favoriteTarget.id }!)
            for _ in 0..<50 {
                if model.state?.folderId == favoriteTarget.id { break }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            checks["favorites-engine-navigation"] = model.state?.folderId == favoriteTarget.id && model.sidebarSelection == favoriteTarget.id
            try await Task.sleep(nanoseconds: 150_000_000)
            checks["favorites-reveal-ancestors"] = NativeBrowserSelfTest.isRevealed(favoriteTarget.id, model: model)
            try await model.navigate(folder)
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
            guard let window = AppDelegate.shared?.mainWindow else { throw BridgeError(message: "原生主窗口丢失。") }
            let screenshot = URL(fileURLWithPath: path).deletingPathExtension()
            model.favoritesExpanded = false
            try await Task.sleep(nanoseconds: 200_000_000)
            try capture(window, to: URL(fileURLWithPath: screenshot.path + "-favorites-collapsed.png"))
            checks["favorites-collapsed-screenshot"] = !model.favoritesExpanded
            model.favoritesExpanded = true
            try await Task.sleep(nanoseconds: 200_000_000)
            chrome["default"] = inspectChrome(window, suffix: "default", checks: &checks)
            try capture(window, to: screenshot.appendingPathExtension("png"))
            checks["full-window-screenshot"] = true
            let originalFrame = window.frame
            window.setContentSize(window.contentMinSize)
            try await Task.sleep(nanoseconds: 300_000_000)
            chrome["minimum"] = inspectChrome(window, suffix: "minimum", checks: &checks)
            try capture(window, to: URL(fileURLWithPath: screenshot.path + "-minimum.png"))
            checks["minimum-window-screenshot"] = true
            window.setFrame(originalFrame, display: true)
            let originalAppearance = window.appearance
            window.appearance = NSAppearance(named: .darkAqua)
            AppDelegate.shared?.refreshToolbarAppearance()
            try await Task.sleep(nanoseconds: 300_000_000)
            try capture(window, to: URL(fileURLWithPath: screenshot.path + "-dark.png"))
            window.appearance = originalAppearance
            AppDelegate.shared?.refreshToolbarAppearance()
            let settings = AppDelegate.shared!.showSettingsWindow()
            try await Task.sleep(nanoseconds: 300_000_000)
            checks["native-settings-window"] = settings.isVisible
            try capture(settings, to: URL(fileURLWithPath: screenshot.path + "-settings.png")); settings.orderOut(nil)
            let about = AppDelegate.shared!.showAboutWindow()
            try await Task.sleep(nanoseconds: 300_000_000)
            checks["native-about-window"] = about.isVisible
            try capture(about, to: URL(fileURLWithPath: screenshot.path + "-about.png")); about.orderOut(nil)
            let notices = Bundle.main.url(forResource: "OpenSourceNotices", withExtension: "txt").flatMap { try? String(contentsOf: $0, encoding: .utf8) } ?? ""
            checks["bundled-open-source-notices"] = notices.contains("TOBYmiz") && notices.contains("Apache License") && notices.contains(".NET Runtime third-party notices")
        } catch let failure { error = failure.localizedDescription + "\n" + model.bridge.stderr }
        NativeDiagnostics.write("self-test requesting engine shutdown")
        checks["engine-shutdown"] = await AppDelegate.shared?.prepareForTermination() == true
        let passed = checks.count == 48 && checks.values.allSatisfy { $0 } && error.isEmpty
        do {
            let output = URL(fileURLWithPath: path); try FileManager.default.createDirectory(at: output.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: ["passed": passed, "nativeControls": true, "checks": checks, "windowChrome": chrome, "error": error], options: [.prettyPrinted, .sortedKeys])
            try data.write(to: output, options: .atomic)
        } catch { FileHandle.standardError.write(Data(error.localizedDescription.utf8)) }
        NSApp.terminate(nil)
    }
    private static func inspectChrome(_ window: NSWindow, suffix: String, checks: inout [String: Bool]) -> [String: Any] {
        window.contentView?.superview?.layoutSubtreeIfNeeded()
        let kinds: [NSWindow.ButtonType] = [.closeButton, .miniaturizeButton, .zoomButton]
        let buttons = kinds.compactMap { window.standardWindowButton($0) }
        let frames = buttons.map { $0.convert($0.bounds, to: nil) }
        let content = window.contentView.map { $0.convert($0.bounds, to: nil) } ?? .zero
        let visible = buttons.count == 3 && buttons.allSatisfy {
            $0.window === window && !$0.isHiddenOrHasHiddenAncestor && $0.isEnabled && !$0.visibleRect.isEmpty
        }
        checks["native-traffic-lights-" + suffix] = visible
        checks["titlebar-content-clearance-" + suffix] = visible && !content.isEmpty && frames.allSatisfy {
            !$0.isEmpty && !$0.intersects(content) && $0.minY >= window.contentLayoutRect.maxY
        }
        let identifiers = window.toolbar?.items.map { $0.itemIdentifier.rawValue } ?? []
        let required: Set<String> = ["back", "forward", "refresh", "upload", "download", "newFolder", "more", "search"]
        checks["native-file-toolbar-" + suffix] = window.toolbar?.identifier == "YuWangMainToolbar"
            && window.toolbar?.isVisible == true && required.isSubset(of: Set(identifiers))
        return ["windowFrame": NSStringFromRect(window.frame), "content": NSStringFromRect(content),
                "contentLayout": NSStringFromRect(window.contentLayoutRect), "trafficLights": frames.map { NSStringFromRect($0) },
                "toolbarItems": identifiers]
    }
    static func capture(_ window: NSWindow, to output: URL) throws {
        guard let view = window.contentView?.superview,
              let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            throw BridgeError(message: "无法创建完整窗口截图。")
        }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        let opaque = NSImage(size: view.bounds.size, flipped: false) { rectangle in
            NSColor.windowBackgroundColor.setFill(); NSBezierPath(rect: rectangle).fill(); bitmap.draw(in: rectangle); return true
        }
        guard let tiff = opaque.tiffRepresentation, let rendered = NSBitmapImageRep(data: tiff),
              let image = rendered.representation(using: .png, properties: [:]) else {
            throw BridgeError(message: "无法导出完整窗口截图。")
        }
        try image.write(to: output)
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
