import Foundation

@MainActor
final class EngineBridge {
    var onState: ((ClientState, BrowserIndex) -> Void)?
    var onProgress: ((TransferState) -> Void)?
    var onFailure: ((String) -> Void)?
    private var process: Process?
    private var input: Pipe?
    private var output: Pipe?
    private var errors: Pipe?
    private var reader: EngineOutputReader?
    private var nextID: Int64 = 0
    private var pending: [Int64: CheckedContinuation<[String: Any], Error>] = [:]
    private(set) var stderr = ""
    private var shuttingDown = false
    private var outputEnded = false
    private var exitStatus: Int32?

    func start(demo: Bool) throws {
        guard process == nil else { return }
        let executable = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/Engine/YuWangSync.Engine")
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { throw BridgeError(message: "应用包中的同步引擎缺失，请重新下载完整应用。") }
        let child = Process(), stdin = Pipe(), stdout = Pipe(), stderrPipe = Pipe()
        child.executableURL = executable
        child.arguments = demo ? ["--demo"] : []
        child.standardInput = stdin; child.standardOutput = stdout; child.standardError = stderrPipe
        let reader = EngineOutputReader { [weak self] message in self?.receive(message) }
        self.reader = reader
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
                reader.finish { [weak self] in self?.outputEnded = true; self?.completeTermination() }
                return
            }
            reader.append(data)
        }
        stderrPipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil; return }
            Task { @MainActor in
                guard let self else { return }
                self.stderr += String(data: data, encoding: .utf8) ?? ""
                if self.stderr.count > 12000 { self.stderr = String(self.stderr.suffix(12000)) }
            }
        }
        child.terminationHandler = { [weak self] child in
            Task { @MainActor in
                guard let self else { return }
                self.exitStatus = child.terminationStatus
                self.completeTermination()
            }
        }
        input = stdin; output = stdout; errors = stderrPipe; process = child
        try child.run()
        NativeDiagnostics.write("engine launched pid=\(child.processIdentifier)")
    }
    private func completeTermination() {
        // A process can exit before the decoding queue has delivered its last
        // reply. Drain stdout through EOF before failing remaining requests.
        guard outputEnded, let exitStatus else { return }
        let message = "同步引擎已退出（\(exitStatus)）。待办会在下次启动恢复。"
        for continuation in pending.values { continuation.resume(throwing: BridgeError(message: message)) }
        pending.removeAll()
        if !shuttingDown { onFailure?(message) }
    }
    func request(_ method: String, _ params: [String: Any] = [:]) async throws -> [String: Any] {
        guard let process, process.isRunning, let input else { throw BridgeError(message: "同步引擎尚未启动。") }
        nextID += 1; let id = nextID
        var data = try JSONSerialization.data(withJSONObject: ["id": id, "method": method, "params": params])
        data.append(10)
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = continuation
            if method == "shutdown" {
                Task { @MainActor [weak self] in
                    try? await Task.sleep(nanoseconds: 10_000_000_000)
                    self?.pending.removeValue(forKey: id)?.resume(throwing: BridgeError(message: "引擎退出等待超时；下次启动将核对未完成事务。"))
                }
            }
            do { try input.fileHandleForWriting.write(contentsOf: data) }
            catch { pending.removeValue(forKey: id)?.resume(throwing: error) }
        }
    }
    private func receive(_ message: EngineOutput) {
        switch message {
        case let .state(state, index): onState?(state, index)
        case let .progress(progress): onProgress?(progress)
        case let .failure(message): onFailure?(message)
        case let .reply(id, result, error):
            guard let continuation = pending.removeValue(forKey: id) else { return }
            if let error { continuation.resume(throwing: BridgeError(message: error)) }
            else { continuation.resume(returning: result) }
        }
    }
    @discardableResult func shutdown() async -> Bool {
        if shuttingDown { return process?.isRunning != true }
        shuttingDown = true
        var confirmed = false
        do { _ = try await request("shutdown"); confirmed = true }
        catch { NativeDiagnostics.write("shutdown: \(error.localizedDescription)") }
        try? input?.fileHandleForWriting.close()
        if !confirmed && process?.isRunning == true { process?.terminate() }
        return confirmed
    }
}
