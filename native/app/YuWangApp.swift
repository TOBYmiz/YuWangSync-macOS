import AppKit
import SwiftUI

@main
struct YuWangApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var model = AppModel.shared
    var body: some Scene {
        WindowGroup("育网云", id: "main") { RootView(model: model) }
            .defaultSize(width: 1180, height: 800)
            .commands {
                CommandGroup(replacing: .newItem) { Button("显示育网云") { delegate.showWindow() }.keyboardShortcut("0", modifiers: .command) }
                CommandMenu("文件") {
                    Button("打开") { if let entry = model.selectedEntries.first { model.open(entry) } }.keyboardShortcut("o", modifiers: .command)
                    Button("离线下载") { model.download() }
                    Button("另存为…") { model.exportSelected() }.keyboardShortcut("s", modifiers: [.command, .shift])
                    Divider()
                    Button("上传文件…") { model.chooseUpload(folders: false) }
                    Button("新建文件夹…") { model.newFolder() }.keyboardShortcut("n", modifiers: [.command, .shift])
                }
            }
        Settings { SettingsView(model: model) }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    static weak var shared: AppDelegate?
    weak var mainWindow: NSWindow?
    private var statusItem: NSStatusItem?
    private var statusLine: NSMenuItem?
    private var quitting = false
    override init() { super.init(); Self.shared = self; NativeDiagnostics.write("delegate init") }
    func applicationDidFinishLaunching(_ notification: Notification) {
        Self.shared = self
        NativeDiagnostics.write("did finish launching; args=\(CommandLine.arguments)")
        NSApp.setActivationPolicy(.regular)
        AppModel.shared.start()
        NSApp.activate(ignoringOtherApps: true)
        if AppModel.shared.isSelfTest { Task { await NativeSelfTest.run(model: AppModel.shared) } }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "externaldrive.badge.icloud", accessibilityDescription: "育网云")
        item.button?.image?.isTemplate = true
        let menu = NSMenu()
        menu.addItem(withTitle: "打开育网云", action: #selector(showWindow), keyEquivalent: "").target = self
        let status = NSMenuItem(title: "正在启动…", action: nil, keyEquivalent: ""); status.isEnabled = false; menu.addItem(status); statusLine = status
        menu.addItem(.separator())
        menu.addItem(withTitle: "暂停 / 继续同步", action: #selector(togglePause), keyEquivalent: "").target = self
        menu.addItem(withTitle: "退出育网云", action: #selector(quit), keyEquivalent: "q").target = self
        item.menu = menu; statusItem = item
    }
    @objc func showWindow() { mainWindow?.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc private func togglePause() { let model = AppModel.shared; model.perform { _ = try await model.send("pause", ["value": model.state?.paused != true]) } }
    @objc private func quit() { NSApp.terminate(nil) }
    func updateStatus() {
        let model = AppModel.shared; let text = "\(model.pendingCount) 项待办 · \(model.errorCount) 项需处理"
        statusLine?.title = text; statusItem?.button?.toolTip = "育网云 · " + text
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool { if !quitting { sender.orderOut(nil); return false }; return true }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if quitting { return .terminateNow }; quitting = true
        Task { await AppModel.shared.bridge.shutdown(); NSApp.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
}

struct WindowCapture: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { let view = NSView(); capture(view); return view }
    func updateNSView(_ nsView: NSView, context: Context) { capture(nsView) }
    private func capture(_ view: NSView) {
        DispatchQueue.main.async {
            if let window = view.window, let delegate = AppDelegate.shared {
                delegate.mainWindow = window; window.delegate = delegate; window.tabbingMode = .disallowed
                NativeDiagnostics.write("window captured visible=\(window.isVisible)")
            }
        }
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
            model.showTasks = true
            try await Task.sleep(nanoseconds: 400_000_000)
            if let view = AppDelegate.shared?.mainWindow?.contentView, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
                view.cacheDisplay(in: view.bounds, to: bitmap)
                if let image = bitmap.representation(using: .png, properties: [:]) { try image.write(to: URL(fileURLWithPath: path).deletingPathExtension().appendingPathExtension("png")) }
            }
        } catch let failure { error = failure.localizedDescription + "\n" + model.bridge.stderr }
        NativeDiagnostics.write("self-test requesting engine shutdown")
        checks["engine-shutdown"] = await model.bridge.shutdown()
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
        if !FileManager.default.fileExists(atPath: path.path) { FileManager.default.createFile(atPath: path.path, contents: nil) }
        if let handle = try? FileHandle(forWritingTo: path) { defer { try? handle.close() }; _ = try? handle.seekToEnd(); try? handle.write(contentsOf: line) }
    }
}
