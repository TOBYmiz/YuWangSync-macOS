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

// Differences between consecutive directory snapshots. The engine reader computes
// this off the main thread so a state event costs the outline O(changes), not O(tree).
struct DirectoryChanges {
    var base: UUID?
    var updated: [String] = []       // rows whose stored value changed
    var redraw: [String] = []        // rows whose title, symbol or expandability changed
    var removed: [String] = []
    var branches = Set<String>()     // parents whose child list changed
    var topBranches: [String] = []   // changed branches without a changed ancestor
    var rootsChanged = false

    init(base: UUID? = nil, from previous: DirectorySnapshot, to next: DirectorySnapshot) {
        self.base = base
        for (id, row) in next.rows {
            let old = previous.rows[id]
            guard old != row else { continue }
            updated.append(id)
            if old?.title != row.title || old?.symbol != row.symbol || previous.canExpand(id) != next.canExpand(id) { redraw.append(id) }
        }
        removed = previous.rows.keys.filter { next.rows[$0] == nil }
        rootsChanged = previous.roots != next.roots
        var changed = Set<String>()
        for id in Set(previous.children.keys).union(next.children.keys)
            where previous.children[id] != next.children[id] && next.rows[id] != nil { changed.insert(id) }
        branches = changed
        topBranches = changed.filter { id in !next.ancestors(of: id).contains(where: changed.contains) }.sorted()
    }
}

// Build adjacency lists once per engine snapshot. Never scan all entries per tree node.
struct BrowserIndex {
    let revision = UUID()
    var changes: DirectoryChanges?
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
