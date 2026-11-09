import Foundation

enum EngineOutput {
    case state(ClientState, BrowserIndex)
    case progress(TransferState)
    case reply(Int64, [String: Any], String?)
    case failure(String)
}

// Pipe callbacks enqueue bytes directly here. Neither JSON parsing nor building
// the full directory index may run in AppKit's mouse-tracking/main run loop.
final class EngineOutputReader {
    private let queue = DispatchQueue(label: "YuWangSync.engine-output", qos: .userInitiated)
    private var buffer = Data()
    private var pendingState: ClientState?
    private var pendingProgress: TransferState?
    private var scheduled = false
    private let deliver: @MainActor (EngineOutput) -> Void

    init(deliver: @escaping @MainActor (EngineOutput) -> Void) { self.deliver = deliver }

    func append(_ data: Data) {
        queue.async { [self] in
            buffer.append(data)
            while let newline = buffer.firstIndex(of: 10) {
                let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
                do {
                    let event = try JSONDecoder().decode(Envelope.self, from: line)
                    if let state = event.state { pendingState = state }
                    else if let progress = event.progress { pendingProgress = progress }
                    else if let id = event.id,
                            let reply = try JSONSerialization.jsonObject(with: line) as? [String: Any] {
                        flush()
                        emit(.reply(id, reply["result"] as? [String: Any] ?? [:],
                                    (reply["error"] as? [String: Any])?["message"] as? String))
                    }
                } catch { flush(); emit(.failure("无法读取同步状态：\(error.localizedDescription)")) }
            }
            if !scheduled && (pendingState != nil || pendingProgress != nil) {
                scheduled = true
                // Replace intermediate snapshots in a burst; replies always survive.
                queue.asyncAfter(deadline: .now() + .milliseconds(16)) { [self] in
                    scheduled = false
                    flush()
                }
            }
        }
    }

    func finish(_ completion: @escaping @MainActor () -> Void) {
        queue.async { [self] in
            flush()
            DispatchQueue.main.async { completion() }
        }
    }

    private func flush() {
        if let state = pendingState { pendingState = nil; emit(.state(state, BrowserIndex(state))) }
        if let progress = pendingProgress { pendingProgress = nil; emit(.progress(progress)) }
    }

    private func emit(_ message: EngineOutput) {
        DispatchQueue.main.async { [deliver] in deliver(message) }
    }

    private struct Envelope: Decodable {
        var state: ClientState?
        var progress: TransferState?
        var id: Int64?
        enum CodingKeys: String, CodingKey { case event, data, id }
        init(from decoder: Decoder) throws {
            let values = try decoder.container(keyedBy: CodingKeys.self)
            switch try values.decodeIfPresent(String.self, forKey: .event) {
            case "state": state = try values.decode(ClientState.self, forKey: .data)
            case "progress": progress = try values.decode(TransferState.self, forKey: .data)
            default: id = try values.decodeIfPresent(Int64.self, forKey: .id)
            }
        }
    }
}
