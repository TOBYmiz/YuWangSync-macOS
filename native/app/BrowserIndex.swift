import Foundation

struct DirectoryRow: Equatable {
    let id: String
    let parentID: String?
    let entryID: String?
    let title: String
    let symbol: String
    let unloaded: Bool
    let isDiskRoot: Bool
}

struct DirectorySnapshot: Equatable {
    var roots: [String] = []
    var rows: [String: DirectoryRow] = [:]
    var children: [String: [String]] = [:]

    func ancestors(of id: String) -> [String] {
        var result: [String] = [], seen: Set<String> = [id], parent = rows[id]?.parentID
        while let value = parent, seen.insert(value).inserted {
            result.append(value); parent = rows[value]?.parentID
        }
        return result.reversed()
    }
    func canExpand(_ id: String) -> Bool {
        guard let row = rows[id] else { return false }
        return row.entryID == nil || row.isDiskRoot || row.unloaded || !(children[id] ?? []).isEmpty
    }
}

// Build adjacency lists once per engine snapshot. Never scan all entries per tree node.
struct BrowserIndex {
    var entries: [String: CloudEntry] = [:]
    var filesByParent: [String: [CloudEntry]] = [:]
    var directories = DirectorySnapshot()

    init(_ state: ClientState? = nil) {
        guard let state else { return }
        var disks: [UInt64: CloudEntry] = [:]
        for entry in state.entries {
            entries[entry.id] = entry
            if let parent = entry.parentId { filesByParent[parent, default: []].append(entry) }
            if entry.isDiskRoot { disks[entry.diskId] = entry }
        }
        let containers = Set(state.tree.filter { $0.nodeType == "container" }.map(\.id))
        for node in state.tree {
            let parent = node.parentId != 0 && containers.contains(node.parentId) ? "container:\(node.parentId)" : nil
            if node.nodeType == "container" {
                let id = "container:\(node.id)"
                directories.rows[id] = DirectoryRow(id: id, parentID: parent, entryID: nil, title: node.name,
                    symbol: parent == nil ? "externaldrive" : "folder", unloaded: false, isDiskRoot: false)
            } else if let entry = disks[node.id] {
                directories.rows[entry.id] = DirectoryRow(id: entry.id, parentID: parent, entryID: entry.id,
                    title: entry.name, symbol: "externaldrive", unloaded: entry.lastListed == nil, isDiskRoot: true)
            }
        }
        for entry in state.entries where entry.isDirectory && !entry.isDiskRoot {
            directories.rows[entry.id] = DirectoryRow(id: entry.id, parentID: entry.parentId, entryID: entry.id,
                title: entry.name, symbol: "folder", unloaded: entry.lastListed == nil && entry.remoteId != 0, isDiskRoot: false)
        }
        // Preserve server order for drive groups and stable name order for folder children.
        var ordered = state.tree.compactMap { node -> String? in
            node.nodeType == "container" ? "container:\(node.id)" : disks[node.id]?.id
        }
        ordered += state.entries.filter { $0.isDirectory && !$0.isDiskRoot }.map(\.id)
        var seen = Set<String>()
        for id in ordered where seen.insert(id).inserted {
            guard let row = directories.rows[id] else { continue }
            if let parent = row.parentID, directories.rows[parent] != nil {
                directories.children[parent, default: []].append(id)
            } else if row.parentID == nil { directories.roots.append(id) }
        }
    }
}
