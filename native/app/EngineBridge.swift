import Foundation

@MainActor
final class EngineBridge {
    var onState: ((ClientState) -> Void)?
    var onProgress: ((TransferState) -> Void)?
    var onFailure: ((String) -> Void)?
    private var process: Process?
    private var input: Pipe?
    private var output: Pipe?
    private var errors: Pipe?
    private var buffer = Data()
    private var nextID: Int64 = 0
    private var pending: [Int64: CheckedContinuation<[String: Any], Error>] = [:]
    private(set) var stderr = ""
    private var shuttingDown = false

    func start(demo: Bool) throws {
        guard process == nil else { return }
        let executable = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/Engine/YuWangSync.Engine")
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { throw BridgeError(message: "应用包中的同步引擎缺失，请重新下载完整应用。") }
        let child = Process(), stdin = Pipe(), stdout = Pipe(), stderrPipe = Pipe()
        child.executableURL = executable
        child.arguments = demo ? ["--demo"] : []
        child.standardInput = stdin; child.standardOutput = stdout; child.standardError = stderrPipe
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            Task { @MainActor in self?.receive(data) }
        }
        stderrPipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            Task { @MainActor in
                guard let self else { return }
                self.stderr += String(data: data, encoding: .utf8) ?? ""
                if self.stderr.count > 12000 { self.stderr = String(self.stderr.suffix(12000)) }
            }
        }
        child.terminationHandler = { [weak self] child in
            Task { @MainActor in
                guard let self else { return }
                let message = "同步引擎已退出（\(child.terminationStatus)）。待办会在下次启动恢复。"
                for continuation in self.pending.values { continuation.resume(throwing: BridgeError(message: message)) }
                self.pending.removeAll()
                if !self.shuttingDown { self.onFailure?(message) }
            }
        }
        input = stdin; output = stdout; errors = stderrPipe; process = child
        try child.run()
    }
    func request(_ method: String, _ params: [String: Any] = [:]) async throws -> [String: Any] {
        guard let process, process.isRunning, let input else { throw BridgeError(message: "同步引擎尚未启动。") }
        nextID += 1; let id = nextID
        var data = try JSONSerialization.data(withJSONObject: ["id": id, "method": method, "params": params])
        data.append(10)
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = continuation
            do { try input.fileHandleForWriting.write(contentsOf: data) }
            catch { pending.removeValue(forKey: id)?.resume(throwing: error) }
        }
    }
    private func receive(_ data: Data) {
        guard !data.isEmpty else { return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 10) {
            let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
            do {
                guard let envelope = try JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
                if let event = envelope["event"] as? String, let body = envelope["data"] {
                    let payload = try JSONSerialization.data(withJSONObject: body)
                    if event == "state" { onState?(try JSONDecoder().decode(ClientState.self, from: payload)) }
                    if event == "progress" { onProgress?(try JSONDecoder().decode(TransferState.self, from: payload)) }
                } else if let id = envelope["id"] as? NSNumber, let continuation = pending.removeValue(forKey: id.int64Value) {
                    if let error = envelope["error"] as? [String: Any] {
                        continuation.resume(throwing: BridgeError(message: error["message"] as? String ?? "操作未完成。"))
                    } else { continuation.resume(returning: envelope["result"] as? [String: Any] ?? [:]) }
                }
            } catch { onFailure?("无法读取同步状态：\(error.localizedDescription)") }
        }
    }
    func shutdown() async {
        shuttingDown = true
        _ = try? await request("shutdown")
        try? input?.fileHandleForWriting.close()
    }
}
