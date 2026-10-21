import AppKit
import SwiftUI

/// はじめの画面：起動したときと「新規キーマップ」のときに出す。表示を選んで新しく作るか、キーマップを開く
struct StartView: View {
    @ObservedObject var model: AppModel
    @AppStorage(Prefs.mainViewKey) private var mainView = "keyboard"
    @AppStorage(Prefs.layerMonitorsKey) private var layerMonitors = "both"
    @AppStorage(Prefs.launchActionKey) private var launchAction = "start"
    @State private var choice = "keyboard"
    @State private var monitors = "both"
    @State private var recents: [String] = []

    private struct Choice: Identifiable {
        let id: String
        let name: String
        let symbol: String
        let detail: String
    }

    private static let choices = [
        Choice(id: "keyboard", name: "キーボード", symbol: "keyboard",
               detail: "キーボードの図に素材を並べます。キーを押して鳴らす、基本の使い方です。"),
        Choice(id: "thumbnails", name: "グリッド", symbol: "square.grid.3x2",
               detail: "素材をサムネイルのカードで並べ、クリックで出します。"),
        Choice(id: "layers", name: "レイヤー", symbol: "square.3.layers.3d",
               detail: "映像を重なり順の段に並べ、プレビューからライブへ切り替えます。"),
    ]

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            VStack(alignment: .leading, spacing: 14) {
                Text("新しく作る").font(.title2.bold())
                Text("メイン画面の表示を選んでください。キーマップと一緒に保存され、あとから 設定 › 一般 でも変えられます。")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(alignment: .top, spacing: 10) {
                    ForEach(Self.choices) { c in card(c) }
                }
                if choice == "layers" {
                    Picker("上のモニター", selection: $monitors) {
                        Text("プレビューとプログラム").tag("both")
                        Text("プログラム（ライブ）だけ").tag("program")
                        Text("プレビューだけ").tag("preview")
                        Text("なし").tag("none")
                    }
                    .frame(width: 360)
                }
                Spacer(minLength: 0)
                HStack {
                    Toggle("起動したときにこの画面を出す", isOn: Binding(
                        get: { launchAction == "start" },
                        set: { launchAction = $0 ? "start" : "blank" }))
                        .toggleStyle(.checkbox)
                    Spacer()
                    Button("この表示で始める") { start() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                }
            }
            .padding(24)
            .frame(width: 580)

            Divider()

            VStack(alignment: .leading, spacing: 10) {
                Text("開く").font(.title2.bold())
                Button {
                    model.showStartScreen = false
                    DispatchQueue.main.async { model.onOpenPanel?() }
                } label: {
                    Label("キーマップを開く…", systemImage: "folder")
                }
                .controlSize(.large)
                Text("最近使ったキーマップ").font(.caption).foregroundStyle(.secondary).padding(.top, 8)
                if recents.isEmpty {
                    Text("まだありません").font(.callout).foregroundStyle(.tertiary)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 2) {
                            ForEach(recents, id: \.self) { path in recentRow(path) }
                        }
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(24)
            .frame(width: 290)
        }
        .frame(height: 430)
        .onAppear {
            choice = mainView
            monitors = layerMonitors
            recents = Prefs.recentKeymaps
        }
        .onExitCommand { model.showStartScreen = false }
    }

    private func card(_ c: Choice) -> some View {
        let selected = choice == c.id
        return VStack(alignment: .leading, spacing: 8) {
            Image(systemName: c.symbol)
                .font(.system(size: 30, weight: .light))
                .frame(maxWidth: .infinity, minHeight: 64)
                .foregroundStyle(selected ? Color.accentColor : .secondary)
            Text(c.name).font(.headline)
            Text(c.detail).font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(12)
        .frame(width: 166, height: 200, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(selected ? 0.10 : 0.04)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? Color.accentColor : Color.white.opacity(0.12), lineWidth: selected ? 2 : 1))
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { choice = c.id; start() }
        .onTapGesture { choice = c.id }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }

    private func recentRow(_ path: String) -> some View {
        let url = URL(fileURLWithPath: path)
        return Button {
            model.showStartScreen = false
            DispatchQueue.main.async {
                do { try model.open(url) } catch { NSAlert(error: error).runModal() }
            }
        } label: {
            VStack(alignment: .leading, spacing: 1) {
                Text(url.deletingPathExtension().lastPathComponent).lineLimit(1)
                Text(url.deletingLastPathComponent().path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 5)
            .padding(.horizontal, 6)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(path)
    }

    private func start() {
        mainView = choice
        if choice == "layers" { layerMonitors = monitors }
        model.showStartScreen = false
    }
}
