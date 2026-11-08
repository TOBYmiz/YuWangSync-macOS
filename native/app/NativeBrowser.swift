import AppKit
import SwiftUI
import UniformTypeIdentifiers

final class DirectoryItem: NSObject {
    var value: DirectoryRow
    init(_ value: DirectoryRow) { self.value = value }
}

final class BrowserOutlineView: NSOutlineView {
    var menuForRow: ((Int) -> NSMenu?)?
    override func menu(for event: NSEvent) -> NSMenu? {
        menuForRow?(row(at: convert(event.locationInWindow, from: nil)))
    }
}

@MainActor
struct NativeDirectoryTree: NSViewRepresentable {
    @ObservedObject var model: AppModel
    func makeCoordinator() -> Coordinator { Coordinator(model) }
    func makeNSView(context: Context) -> NSScrollView {
        let outline = BrowserOutlineView()
        outline.identifier = NSUserInterfaceItemIdentifier("cloud-outline")
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("directory")); column.width = 230
        outline.addTableColumn(column); outline.outlineTableColumn = column; outline.headerView = nil
        outline.style = .sourceList; outline.rowHeight = 25; outline.indentationPerLevel = 14
        outline.autoresizesOutlineColumn = true; outline.autosaveExpandedItems = false
        outline.allowsEmptySelection = true; outline.allowsMultipleSelection = false
        outline.dataSource = context.coordinator; outline.delegate = context.coordinator
        outline.menuForRow = { [weak coordinator = context.coordinator] row in coordinator?.menu(row) }
        context.coordinator.outline = outline
        let scroll = NSScrollView(); scroll.documentView = outline; scroll.hasVerticalScroller = true
        scroll.drawsBackground = false; outline.backgroundColor = .clear
        return scroll
    }
    func updateNSView(_ view: NSScrollView, context: Context) { context.coordinator.update(model) }

    @MainActor final class Coordinator: NSObject, NSOutlineViewDataSource, NSOutlineViewDelegate {
        var model: AppModel
        weak var outline: BrowserOutlineView?
        var snapshot = DirectorySnapshot()
        var items: [String: DirectoryItem] = [:]
        var expanded = Set<String>(), loading = Set<String>()
        private var accountID: String?
        private var revealRevision = -1
        private var applying = false, changingExpansion = false
        init(_ model: AppModel) { self.model = model }

        func update(_ model: AppModel) {
            self.model = model
            guard let outline else { return }
            applying = true; defer { applying = false }
            if accountID != model.state?.activeAccount?.id {
                accountID = model.state?.activeAccount?.id; expanded.removeAll(); revealRevision = -1
                outline.collapseItem(nil, collapseChildren: true)
            }
            let next = model.browserIndex.directories
            if snapshot != next {
                snapshot = next
                items = next.rows.mapValues { row in
                    let item = items[row.id] ?? DirectoryItem(row); item.value = row; return item
                }
                expanded.formIntersection(Set(items.keys))
                outline.reloadData()
                // Only visit expanded branches; collapsed descendants never produce views.
                var stack = snapshot.roots.reversed().map { $0 }
                while let id = stack.popLast() {
                    if expanded.contains(id), let item = items[id] {
                        outline.expandItem(item)
                        stack.append(contentsOf: (snapshot.children[id] ?? []).reversed())
                    }
                }
            }
            if revealRevision != model.sidebarRevealRevision, let id = model.sidebarRevealID, items[id] != nil {
                for parent in snapshot.ancestors(of: id) {
                    if let item = items[parent] { expanded.insert(parent); outline.expandItem(item) }
                }
                if let item = items[id] {
                    let row = outline.row(forItem: item)
                    if row >= 0 { revealRevision = model.sidebarRevealRevision; outline.scrollRowToVisible(row) }
                }
            }
            let row = model.sidebarSelection.flatMap { items[$0] }.map { outline.row(forItem: $0) } ?? -1
            let selection = row >= 0 ? IndexSet(integer: row) : IndexSet()
            if outline.selectedRowIndexes != selection { outline.selectRowIndexes(selection, byExtendingSelection: false) }
        }
        func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
            if let item = item as? DirectoryItem { return snapshot.children[item.value.id]?.count ?? 0 }
            return snapshot.roots.count
        }
        func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
            let ids = (item as? DirectoryItem).map { snapshot.children[$0.value.id] ?? [] } ?? snapshot.roots
            return items[ids[index]]!
        }
        func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
            (item as? DirectoryItem).map { snapshot.canExpand($0.value.id) } ?? false
        }
        func outlineView(_ outlineView: NSOutlineView, shouldSelectItem item: Any) -> Bool {
            (item as? DirectoryItem)?.value.entryID != nil
        }
        func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
            guard let item = item as? DirectoryItem else { return nil }
            let cell = browserCell(outlineView, identifier: "directory-cell")
            cell.textField?.stringValue = item.value.title
            cell.imageView?.image = NSImage(systemSymbolName: item.value.symbol, accessibilityDescription: nil)
            return cell
        }
        func outlineViewSelectionDidChange(_ notification: Notification) {
            guard !applying, !changingExpansion, let outline, outline.selectedRow >= 0,
                  let id = (outline.item(atRow: outline.selectedRow) as? DirectoryItem)?.value.entryID else { return }
            model.perform { try await self.model.navigate(id) }
        }
        private func expansionStarted() {
            guard !applying else { return }; changingExpansion = true
            DispatchQueue.main.async { [weak self] in self?.changingExpansion = false }
        }
        func outlineViewItemWillExpand(_ notification: Notification) { expansionStarted() }
        func outlineViewItemWillCollapse(_ notification: Notification) { expansionStarted() }
        func outlineViewItemDidCollapse(_ notification: Notification) {
            guard !applying, let item = notification.userInfo?["NSObject"] as? DirectoryItem else { return }
            expanded.remove(item.value.id)
        }
        func outlineViewItemDidExpand(_ notification: Notification) {
            guard !applying, let item = notification.userInfo?["NSObject"] as? DirectoryItem else { return }
            expanded.insert(item.value.id)
            guard item.value.unloaded, let id = item.value.entryID, loading.insert(id).inserted else { return }
            Task { @MainActor in
                defer { loading.remove(id) }
                do { _ = try await model.send("loadFolder", ["folderId": id]) }
                catch { model.errorMessage = error.localizedDescription }
            }
        }
        func menu(_ row: Int) -> NSMenu? {
            guard row >= 0, let item = outline?.item(atRow: row) as? DirectoryItem,
                  let id = item.value.entryID, let entry = model.browserIndex.entries[id] else { return nil }
            let menu = NSMenu()
            let action = NSMenuItem(title: model.isFavorite(entry) ? "取消收藏" : "添加到收藏", action: #selector(toggleFavorite(_:)), keyEquivalent: "")
            action.target = self; action.representedObject = id; menu.addItem(action); return menu
        }
        @objc private func toggleFavorite(_ sender: NSMenuItem) {
            if let id = sender.representedObject as? String, let entry = model.browserIndex.entries[id] { model.toggleFavorite(entry) }
        }
    }
}

final class BrowserTableView: NSTableView {
    var menuForRow: ((Int) -> NSMenu?)?
    var openSelection: (() -> Void)?
    var trackingFinished: (() -> Void)?
    private(set) var trackingMouse = false
    override func mouseDown(with event: NSEvent) {
        let testing = CommandLine.arguments.contains("--ui-selftest")
        trackingMouse = true
        defer {
            trackingMouse = false; trackingFinished?()
            if testing { NativeDiagnostics.write("file mouse selected-after=\(Array(selectedRowIndexes))") }
        }
        if testing { NativeDiagnostics.write("file mouse flags=\(event.modifierFlags.rawValue) selected-before=\(Array(selectedRowIndexes))") }
        super.mouseDown(with: event)
    }
    override func menu(for event: NSEvent) -> NSMenu? {
        let row = row(at: convert(event.locationInWindow, from: nil))
        if row >= 0 && !selectedRowIndexes.contains(row) { selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false) }
        return menuForRow?(row)
    }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 36 || event.keyCode == 76 { openSelection?() }
        else { super.keyDown(with: event) }
    }
}

@MainActor
struct NativeFileTable: NSViewRepresentable {
    @ObservedObject var model: AppModel
    func makeCoordinator() -> Coordinator { Coordinator(model) }
    func makeNSView(context: Context) -> NSScrollView {
        let table = BrowserTableView(); table.identifier = NSUserInterfaceItemIdentifier("cloud-files")
        for (key, title, width) in [("name", "名称", 300.0), ("size", "大小", 90.0), ("date", "上传时间", 145.0), ("status", "状态", 180.0)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(key)); column.title = title; column.width = width
            column.minWidth = key == "name" ? 160 : 65
            column.sortDescriptorPrototype = NSSortDescriptor(key: key, ascending: true)
            table.addTableColumn(column)
        }
        table.sortDescriptors = [NSSortDescriptor(key: "name", ascending: true)]
        table.allowsMultipleSelection = true; table.allowsEmptySelection = true
        table.usesAlternatingRowBackgroundColors = true; table.rowHeight = 25; table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        table.dataSource = context.coordinator; table.delegate = context.coordinator
        table.target = context.coordinator; table.doubleAction = #selector(Coordinator.openClicked)
        table.openSelection = { [weak coordinator = context.coordinator] in coordinator?.openSelected() }
        table.trackingFinished = { [weak coordinator = context.coordinator] in
            if let coordinator { coordinator.update(coordinator.model) }
        }
        table.menuForRow = { [weak coordinator = context.coordinator] row in coordinator?.menu(row) }
        context.coordinator.table = table
        let scroll = NSScrollView(); scroll.documentView = table; scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true
        return scroll
    }
    func updateNSView(_ view: NSScrollView, context: Context) { context.coordinator.update(model) }

    @MainActor final class Coordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate {
        var model: AppModel
        weak var table: BrowserTableView?
        var rows: [CloudEntry] = [], source: [CloudEntry] = []
        private var rowByID: [String: Int] = [:]
        private var folderID: String?
        private var applying = false
        private var icons: [String: NSImage] = [:]
        init(_ model: AppModel) { self.model = model }
        func update(_ model: AppModel) {
            self.model = model
            guard let table, !table.trackingMouse else { return }
            applying = true; defer { applying = false }
            let entries = model.fileRows
            let changedFolder = folderID != model.state?.folderId
            folderID = model.state?.folderId
            if entries != source {
                source = entries; sortRows(); table.reloadData()
                if changedFolder && !rows.isEmpty { table.scrollRowToVisible(0) }
            }
            restoreSelection()
        }
        private func sortRows() {
            let order = table?.sortDescriptors ?? []
            rows = source.sorted { a, b in
                if a.isDirectory != b.isDirectory { return a.isDirectory }
                for descriptor in order {
                    let result: ComparisonResult
                    switch descriptor.key {
                    case "size": result = (a.size ?? -1) == (b.size ?? -1) ? .orderedSame : ((a.size ?? -1) < (b.size ?? -1) ? .orderedAscending : .orderedDescending)
                    case "date": result = (a.uploadTime ?? "").compare(b.uploadTime ?? "")
                    case "status": result = a.status.localizedStandardCompare(b.status)
                    default: result = a.name.localizedStandardCompare(b.name)
                    }
                    if result != .orderedSame { return descriptor.ascending ? result == .orderedAscending : result == .orderedDescending }
                }
                return a.id < b.id
            }
            rowByID.removeAll(keepingCapacity: true)
            for (index, entry) in rows.enumerated() { rowByID[entry.id] = index }
        }
        private func restoreSelection() {
            guard let table else { return }
            let selected = IndexSet(model.selection.compactMap { rowByID[$0] })
            if selected != table.selectedRowIndexes { table.selectRowIndexes(selected, byExtendingSelection: false) }
        }
        func numberOfRows(in tableView: NSTableView) -> Int { rows.count }
        func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
            guard rows.indices.contains(row), let column = tableColumn else { return nil }
            let entry = rows[row], key = column.identifier.rawValue
            if key == "name" {
                let cell = browserCell(tableView, identifier: "file-name-cell"); cell.textField?.stringValue = entry.name
                let ext = (entry.name as NSString).pathExtension.lowercased(), iconKey = entry.isDirectory ? "<folder>" : ext
                if icons[iconKey] == nil { icons[iconKey] = NSWorkspace.shared.icon(for: entry.isDirectory ? UTType.folder : (UTType(filenameExtension: ext) ?? .data)) }
                cell.imageView?.image = icons[iconKey]; return cell
            }
            let cell = browserCell(tableView, identifier: "file-" + key, icon: false)
            cell.textField?.stringValue = key == "size" ? entry.sizeLabel : (key == "date" ? entry.dateLabel : entry.status)
            cell.textField?.textColor = .secondaryLabelColor; return cell
        }
        func tableViewSelectionDidChange(_ notification: Notification) {
            guard !applying, let table else { return }
            let selected = Set(table.selectedRowIndexes.compactMap { rows.indices.contains($0) ? rows[$0].id : nil })
            if selected != model.selection { model.selection = selected }
        }
        func tableView(_ tableView: NSTableView, sortDescriptorsDidChange oldDescriptors: [NSSortDescriptor]) {
            applying = true; defer { applying = false }; sortRows(); tableView.reloadData(); restoreSelection()
        }
        @objc func openClicked() {
            guard let row = table?.clickedRow, rows.indices.contains(row) else { return }; model.open(rows[row])
        }
        func openSelected() {
            guard let row = table?.selectedRow, rows.indices.contains(row) else { return }; model.open(rows[row])
        }
        func menu(_ row: Int) -> NSMenu? {
            guard rows.indices.contains(row) else { return nil }
            let menu = NSMenu()
            func add(_ title: String, _ actionName: String, _ enabled: Bool = true) {
                let item = NSMenuItem(title: title, action: #selector(command(_:)), keyEquivalent: "")
                item.target = self; item.representedObject = actionName; item.isEnabled = enabled; menu.addItem(item)
            }
            menu.autoenablesItems = false
            add("打开", "open"); add("离线下载", "download"); add("另存为…", "export")
            add("在访达中显示", "reveal", model.selectedEntries.first?.localPath != nil)
            if model.selection.count == 1, let entry = model.selectedEntries.first, entry.isDirectory { add(model.isFavorite(entry) ? "取消收藏" : "添加到收藏", "favorite") }
            menu.addItem(.separator()); add("重命名…", "rename", model.selection.count == 1)
            add("移动到…", "move"); add("移到云端回收站…", "delete"); return menu
        }
        @objc private func command(_ sender: NSMenuItem) {
            switch sender.representedObject as? String {
            case "open": openSelected()
            case "download": model.download()
            case "export": model.exportSelected()
            case "reveal": model.revealSelected()
            case "favorite": if let entry = model.selectedEntries.first { model.toggleFavorite(entry) }
            case "rename": model.renameSelected()
            case "move": model.showMove = true
            case "delete": model.deleteSelected()
            default: break
            }
        }
    }
}

@MainActor
private func browserCell(_ table: NSTableView, identifier: String, icon: Bool = true) -> NSTableCellView {
    let id = NSUserInterfaceItemIdentifier(identifier)
    if let cell = table.makeView(withIdentifier: id, owner: nil) as? NSTableCellView { return cell }
    let cell = NSTableCellView(); cell.identifier = id
    let text = NSTextField(labelWithString: ""); text.lineBreakMode = .byTruncatingMiddle; text.translatesAutoresizingMaskIntoConstraints = false
    cell.addSubview(text); cell.textField = text
    if icon {
        let image = NSImageView(); image.translatesAutoresizingMaskIntoConstraints = false; cell.addSubview(image); cell.imageView = image
        NSLayoutConstraint.activate([image.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 3), image.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            image.widthAnchor.constraint(equalToConstant: 16), image.heightAnchor.constraint(equalToConstant: 16), text.leadingAnchor.constraint(equalTo: image.trailingAnchor, constant: 6)])
    } else { text.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 3).isActive = true }
    NSLayoutConstraint.activate([text.centerYAnchor.constraint(equalTo: cell.centerYAnchor), text.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -3)])
    return cell
}
