import AppKit
import SwiftUI

struct AboutView: View {
    private let notices: String = {
        guard let url = Bundle.main.url(forResource: "OpenSourceNotices", withExtension: "txt"),
              let text = try? String(contentsOf: url, encoding: .utf8) else { return "许可文件缺失，请重新下载完整应用包。" }
        return text
    }()
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 16) {
                Image(nsImage: NSApplication.shared.applicationIconImage).resizable().frame(width: 64, height: 64)
                VStack(alignment: .leading, spacing: 4) {
                    Text("育网云 macOS").font(.title2.bold())
                    Text(Bundle.main.object(forInfoDictionaryKey: "YuWangReleaseVersion") as? String ?? "0.1.0-preview.2").foregroundStyle(.secondary)
                    Text("作者：TOBYmiz")
                }
                Spacer()
            }
            Link("项目主页", destination: URL(string: "https://github.com/TOBYmiz/YuWangSync-macOS")!)
            Text("开源许可").font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 7) {
                GridRow { Text("YuWangSync-macOS"); Text("MIT") }
                GridRow { Text(".NET Runtime / Microsoft.Data.Sqlite"); Text("MIT") }
                GridRow { Text("SQLitePCLRaw"); Text("Apache-2.0") }
                GridRow { Text("SQLite"); Text("Public Domain") }
            }.font(.callout)
            Divider()
            ScrollView {
                Text(notices).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(8)
            }.background(.background).border(Color(nsColor: .separatorColor))
        }.padding(24).frame(minWidth: 520, minHeight: 480)
    }
}
