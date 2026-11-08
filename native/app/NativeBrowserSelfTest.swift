import AppKit
import SwiftUI

@MainActor
enum NativeBrowserSelfTest {
    static func find<T: NSView>(_ type: T.Type, in view: NSView?, identifier: String) -> T? {
        guard let view else { return nil }
        if let match = view as? T, match.identifier?.rawValue == identifier { return match }
        for child in view.subviews { if let match = find(type, in: child, identifier: identifier) { return match } }
        return nil
    }
    static func isRevealed(_ id: String, model: AppModel) -> Bool {
        guard let outline = find(NSOutlineView.self, in: AppDelegate.shared?.mainWindow?.contentView, identifier: "cloud-outline"), outline.selectedRow >= 0 else { return false }
        let visible = (0..<outline.numberOfRows).compactMap { outline.item(atRow: $0) as? DirectoryItem }
        return visible.contains { $0.value.id == id }
            && (outline.item(atRow: outline.selectedRow) as? DirectoryItem)?.value.id == id
            && model.browserIndex.directories.ancestors(of: id).allSatisfy { parent in
                visible.contains { $0.value.id == parent && outline.isItemExpanded($0) }
            }
    }
    // Dispatch real AppKit mouse tracking, including the up event it consumes internally.
    // No accessibility authorization or synthetic screen coordinates are needed.
    static func click(_ view: NSView, at point: NSPoint, modifiers: NSEvent.ModifierFlags = []) throws {
        guard let window = view.window else { throw BridgeError(message: "交互自检视图没有窗口。") }
        let location = view.convert(point, to: nil), time = ProcessInfo.processInfo.systemUptime
        guard let down = NSEvent.mouseEvent(with: .leftMouseDown, location: location, modifierFlags: modifiers, timestamp: time,
            windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1),
              let up = NSEvent.mouseEvent(with: .leftMouseUp, location: location, modifierFlags: modifiers, timestamp: time + 0.01,
            windowNumber: window.windowNumber, context: nil, eventNumber: 2, clickCount: 1, pressure: 0) else { throw BridgeError(message: "无法创建原生鼠标事件。") }
        // Route through the application/window so disclosure buttons and cell hit tests participate.
        NSApp.postEvent(up, atStart: true); NSApp.sendEvent(down)
    }
    static func run(model: AppModel, output: String) async throws -> [String: Bool] {
        NativeDiagnostics.write("browser interaction: entered")
        try await Task.sleep(nanoseconds: 150_000_000)
        NativeDiagnostics.write("browser interaction: initial view ready")
        guard let state = model.state, let root = model.currentFolder,
              let mainOutline = find(NSOutlineView.self, in: AppDelegate.shared?.mainWindow?.contentView, identifier: "cloud-outline") else {
            throw BridgeError(message: "原生目录树未挂载。")
        }
        var checks: [String: Bool] = [:]
        checks["initial-drive-groups-collapsed"] = mainOutline.numberOfRows == model.browserIndex.directories.roots.count
            && (0..<mainOutline.numberOfRows).allSatisfy { !mainOutline.isItemExpanded(mainOutline.item(atRow: $0)) }
        let suite = "YuWangSync.interaction-selftest." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let test = AppModel(preferences: defaults)
        // Two Windows-reference drive groups, a nested container, and overlapping type/disk IDs.
        let tree = [DiskNode(id: 7, nodeType: "container", name: "班级共享(I:)", parentId: 0),
                    DiskNode(id: 106, nodeType: "container", name: "年级共享", parentId: 7),
                    DiskNode(id: root.diskId, nodeType: "disk", name: root.name, parentId: 106),
                    DiskNode(id: 200, nodeType: "container", name: "材料收集(F:)", parentId: 0)]
        let stamp = "2026-11-08T00:00:00Z"
        var entries = [root]
        for index in 0..<10_000 {
            entries.append(CloudEntry(id: "interaction-folder-\(index)", accountId: root.accountId, parentId: root.id,
                diskId: root.diskId, remoteId: UInt64(1000 + index), name: String(format: "目录 %05d", index), isDirectory: true, isDiskRoot: false,
                size: nil, sizeLabel: "—", uploadTime: stamp, localPath: nil, lastListed: stamp, status: "云端"))
        }
        for index in 0..<10 {
            entries.append(CloudEntry(id: "interaction-file-\(index)", accountId: root.accountId, parentId: root.id,
                diskId: root.diskId, remoteId: UInt64(20_000 + index), name: String(format: "文件 %02d.txt", index), isDirectory: false, isDiskRoot: false,
                size: Int64(index), sizeLabel: "\(index) B", uploadTime: stamp, localPath: nil, lastListed: nil, status: "云端"))
        }
        func snapshot(_ entries: [CloudEntry]) -> ClientState {
            ClientState(version: state.version, demo: true, accounts: state.accounts, activeAccount: state.activeAccount,
                folderId: root.id, entries: entries, tree: tree, operations: [], offline: false, manualOffline: false, paused: false,
                connectionStatus: "交互自检", dataDirectory: state.dataDirectory, settings: state.settings, networkCost: state.networkCost)
        }
        let started = ProcessInfo.processInfo.systemUptime
        test.state = snapshot(entries); test.sidebarSelection = root.id
        NativeDiagnostics.write("browser interaction: indexed 10000 directories")
        checks["large-tree-index"] = test.browserIndex.directories.rows.count == 10_004 && ProcessInfo.processInfo.systemUptime - started < 2
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1060, height: 650), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false; window.title = "目录与选择交互自检"
        window.contentView = NSHostingView(rootView: HStack(spacing: 0) {
            NativeDirectoryTree(model: test).frame(width: 250)
            Divider()
            NativeFileTable(model: test)
        }.frame(width: 1060, height: 650))
        NativeDiagnostics.write("browser interaction: mounting native controls")
        window.center(); window.makeKeyAndOrderFront(nil)
        defer { window.close(); AppDelegate.shared?.mainWindow?.makeKeyAndOrderFront(nil) }
        try await Task.sleep(nanoseconds: 250_000_000)
        NativeDiagnostics.write("browser interaction: controls mounted")
        guard let outline = find(NSOutlineView.self, in: window.contentView, identifier: "cloud-outline"),
              let table = find(NSTableView.self, in: window.contentView, identifier: "cloud-files") else { throw BridgeError(message: "原生文件浏览控件未就绪。") }
        checks["two-drive-groups-collapsed"] = outline.numberOfRows == 2
        guard let drive = outline.item(atRow: 0) else { throw BridgeError(message: "驱动器自检行缺失。") }
        let outlineStart = ProcessInfo.processInfo.systemUptime
        for _ in 0..<40 { outline.expandItem(drive); outline.collapseItem(drive) }
        NativeDiagnostics.write("browser interaction: rapid drive collapse finished")
        checks["rapid-drive-collapse"] = outline.numberOfRows == 2 && ProcessInfo.processInfo.systemUptime - outlineStart < 3
        // Disclosure click must expand the group without starting folder navigation.
        let disclosure = outline.frameOfOutlineCell(atRow: 0)
        try click(outline, at: NSPoint(x: disclosure.midX, y: disclosure.midY))
        NativeDiagnostics.write("browser interaction: disclosure click frame=\(disclosure) rows=\(outline.numberOfRows) expanded=\(outline.isItemExpanded(drive))")
        checks["drive-disclosure-click"] = outline.isItemExpanded(drive) && outline.numberOfRows == 3
        if !outline.isItemExpanded(drive) { outline.expandItem(drive) }
        if let nested = outline.item(atRow: 1) { outline.expandItem(nested) }
        guard outline.numberOfRows >= 4, let disk = outline.item(atRow: 2) else { throw BridgeError(message: "展开后共享盘自检行缺失。") }
        let collapseStart = ProcessInfo.processInfo.systemUptime
        for _ in 0..<10 { outline.expandItem(disk); outline.collapseItem(disk) }
        NativeDiagnostics.write("browser interaction: large drive collapse finished")
        checks["large-drive-collapse"] = outline.numberOfRows == 4 && ProcessInfo.processInfo.systemUptime - collapseStart < 5
        checks["drive-collapse-no-navigation"] = test.state?.folderId == root.id && test.errorMessage == nil
        func point(_ row: Int) -> NSPoint { NSPoint(x: 65, y: table.rect(ofRow: row).midY) }
        try click(table, at: point(0))
        NativeDiagnostics.write("browser interaction: single click returned")
        checks["file-single-click-selection"] = table.selectedRowIndexes == IndexSet(integer: 0) && test.selection.count == 1
        try click(table, at: point(2), modifiers: .command)
        NativeDiagnostics.write("browser interaction: command click returned")
        checks["file-command-multiselect"] = table.selectedRowIndexes == IndexSet([0, 2]) && test.selection.count == 2
        try click(table, at: point(0))
        try click(table, at: point(3), modifiers: .shift)
        NativeDiagnostics.write("browser interaction: shift click returned")
        checks["file-shift-range-selection"] = table.selectedRowIndexes == IndexSet(integersIn: 0...3) && test.selection.count == 4
        let selected = test.selection
        // Background metadata/preload updates must neither clear selection nor expand collapsed drives.
        entries.append(CloudEntry(id: "background-folder", accountId: root.accountId, parentId: root.id, diskId: root.diskId,
            remoteId: 99_999, name: "后台新增目录", isDirectory: true, isDiskRoot: false, size: nil, sizeLabel: "—", uploadTime: stamp,
            localPath: nil, lastListed: stamp, status: "云端"))
        test.state = snapshot(entries)
        try await Task.sleep(nanoseconds: 200_000_000)
        checks["drive-collapse-no-navigation"] = test.state?.folderId == root.id && test.errorMessage == nil
        checks["file-selection-survives-refresh"] = test.selection == selected && table.selectedRowIndexes.count == 4
        checks["collapsed-drive-survives-refresh"] = !outline.isItemExpanded(disk) && outline.isItemExpanded(drive)
        let repeatStart = ProcessInfo.processInfo.systemUptime
        for index in 0..<20 { try click(table, at: point(index % 5)) }
        NativeDiagnostics.write("browser interaction: repeated clicks finished")
        checks["rapid-file-selection-responsive"] = test.selection.count == 1 && ProcessInfo.processInfo.systemUptime - repeatStart < 5
        outline.collapseItem(nil, collapseChildren: true)
        let image = URL(fileURLWithPath: output).deletingPathExtension().path + "-interaction.png"
        try NativeSelfTest.capture(window, to: URL(fileURLWithPath: image))
        return checks
    }
}
