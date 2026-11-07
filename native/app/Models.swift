import Foundation

struct CloudAccount: Decodable, Identifiable, Hashable {
    let id: String
    let server: String
    let username: String
    let displayName: String
    var label: String { displayName + " · " + (URL(string: server)?.host ?? server) }
}
struct CloudEntry: Decodable, Identifiable, Hashable {
    let id: String
    let accountId: String
    let parentId: String?
    let diskId: UInt64
    let remoteId: UInt64
    let name: String
    let isDirectory: Bool
    let isDiskRoot: Bool
    let size: Int64?
    let sizeLabel: String
    let uploadTime: String?
    let localPath: String?
    let status: String
    var dateLabel: String { String((uploadTime ?? "—").prefix(16)).replacingOccurrences(of: "T", with: " ") }
}
struct DiskNode: Decodable {
    let id: UInt64
    let nodeType: String
    let name: String
    let parentId: UInt64
}
struct CloudOperation: Decodable, Identifiable {
    let id: String
    let entryId: String
    let name: String
    let kind: String
    let state: String
    let kindLabel: String
    let stateLabel: String
    let isTerminal: Bool
    let error: String?
    let snapshotPath: String?
    let mutationStarted: Bool
}
struct ClientState: Decodable {
    let version: String
    let demo: Bool
    let accounts: [CloudAccount]
    let activeAccount: CloudAccount?
    let folderId: String?
    let entries: [CloudEntry]
    let tree: [DiskNode]
    let operations: [CloudOperation]
    let offline: Bool
    let manualOffline: Bool
    let paused: Bool
    let connectionStatus: String
    let dataDirectory: String
}
struct TransferState: Decodable {
    let name: String
    let stage: String
    let completed: Int64
    let total: Int64
    let percent: Double
}
struct SidebarNode: Identifiable {
    let id: String
    let title: String
    let symbol: String
    let children: [SidebarNode]?
}
struct BridgeError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
