import AppKit
import SwiftUI

struct ContentView: View {
    @ObservedObject var model: AppModel
    @AppStorage(Prefs.mainViewKey) private var mainView = "keyboard"
    @AppStorage(Prefs.layerMonitorsKey) private var layerMonitors = "both"

    /// 中央（キーボードと設定）に最低限ほしい幅
    static let mainMinWidth: CGFloat = 700
    static let pagesWidth: CGFloat = 180
    static let sidebarWidth: CGFloat = 270

    var body: some View {
        GeometryReader { geo in
            // 画面に収まりきらないときは、右のモニター欄 → 左のプレイリストの順に畳む
            let deckW = model.deckTabs.isEmpty ? 0 : DeckPanel.width + 1
            let pagesW = model.showPages ? ContentView.pagesWidth + 1 : 0
            let sideW = ContentView.sidebarWidth + 1
            let showSidebar = geo.size.width - deckW - pagesW - sideW >= ContentView.mainMinWidth
            let showPages = model.showPages && geo.size.width - deckW - pagesW - (showSidebar ? sideW : 0) >= ContentView.mainMinWidth
            HStack(spacing: 0) {
                if showPages {
                    PagePanel(model: model, pageLive: model.pageLive)
                    Divider()
                }
                VStack(spacing: 8) {
                    TopBar(model: model, video: model.video)
                    if mainView == "thumbnails" {
                        ThumbnailGridView(model: model, live: model.live)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else if mainView == "layers" {
                        LayerView(model: model, live: model.live)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        KeyboardView(model: model, live: model.live)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                    DetailPanel(model: model, live: model.live)
                    StatusBar(model: model)
                }
                .padding(10)
                .frame(minWidth: 0, maxWidth: .infinity)
                if !model.deckTabs.isEmpty {
                    Divider()
                    DeckPanel(model: model, live: model.live)
                }
                if showSidebar {
                    Divider()
                    Sidebar(model: model, video: model.video)
                        .frame(width: ContentView.sidebarWidth)
                }
            }
        }
        .background(Color(white: 0.15))
        .preferredColorScheme(.dark)
        .sheet(isPresented: $model.showStartScreen) { StartView(model: model) }
        // 表示もキーマップに入るので、切り替えたら未保存に
        .onChange(of: mainView) { model.viewChanged() }
        .onChange(of: layerMonitors) { model.viewChanged() }
    }
}

/// 上部のバー。幅が足りなければ文字を減らしたアイコン中心の並びに切り替える
private struct TopBar: View {
    @ObservedObject var model: AppModel
    @ObservedObject var video: VideoOutput

    var body: some View {
        ViewThatFits(in: .horizontal) {
            TopBarContent(model: model, video: video, compact: false)
            TopBarContent(model: model, video: video, compact: true)
        }
    }
}

private struct TopBarContent: View {
    @ObservedObject var model: AppModel
    @ObservedObject var video: VideoOutput
    let compact: Bool

    var body: some View {
        HStack(spacing: compact ? 8 : 12) {
            HStack(spacing: 6) {
                Image(systemName: "hifispeaker.2").foregroundStyle(.secondary)
                Picker("", selection: $model.outputDeviceUID) {
                    Text("システム既定の出力").tag(String?.none)
                    ForEach(model.devices) { d in Text(d.label).tag(String?.some(d.uid)) }
                }
                .labelsHidden()
                .frame(width: compact ? 120 : 220)
            }
            .help("音声の出力先。複数 ch のインターフェースを選ぶと全チャンネルを使えます")

            HStack(spacing: 6) {
                if compact {
                    Image(systemName: "speaker.wave.2").foregroundStyle(.secondary)
                } else {
                    Text("マスター").fixedSize()
                }
                Slider(value: $model.masterVolume, in: 0...1.5).frame(width: compact ? 60 : 90)
            }
            .help("マスター音量")

            Divider().frame(height: 20)

            HStack(spacing: 6) {
                OutputMenu(video: video)
                    .frame(width: compact ? 120 : 200)
                Button {
                    video.isShowing ? video.hide() : video.show()
                } label: {
                    if compact {
                        Image(systemName: video.isShowing ? "rectangle.slash" : "rectangle.on.rectangle")
                    } else {
                        Text(video.isShowing ? "映像出力を閉じる" : "映像出力を開く")
                    }
                }
                .fixedSize()
                .help(video.isShowing ? "映像出力を閉じる" : "映像出力を開く")
            }

            OutputButtons(feed: model.feed, camera: model.virtualCamera, compact: compact)

            Spacer(minLength: 4)

            Button {
                model.showPages.toggle()
            } label: {
                Label("\(model.currentPage + 1). \(model.currentPageName)", systemImage: "list.bullet.rectangle")
                    .lineLimit(1)
                    .frame(maxWidth: compact ? 110 : 200)
            }
            .fixedSize()
            .help("プレイリスト（キーボードのページ）を表示／隠す。⌘→ / ⌘← でページを切り替え")

            Button {
                NSApp.sendAction(#selector(AppDelegate.showSettings(_:)), to: nil, from: nil)
            } label: {
                if compact { Image(systemName: "gearshape") } else { Label("設定", systemImage: "gearshape") }
            }
            .fixedSize()
            .help("映像・音・一般の設定（⌘,）。バックグラウンド入力もここ")

            Button {
                model.stopAll(immediate: NSEvent.modifierFlags.contains(.shift))
            } label: {
                if compact { Image(systemName: "stop.circle.fill") } else { Label("全停止", systemImage: "stop.circle.fill") }
            }
            .fixedSize()
            .tint(.red)
            .help("全停止（ESC と同じ。Shift を押しながらで即停止）")
        }
    }
}

/// プレイリスト：キーボードのページを並べて、クリックで丸ごと切り替える（ProPresenter のように）
private struct PagePanel: View {
    @ObservedObject var model: AppModel
    @ObservedObject var pageLive: PageLiveState

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("プレイリスト").font(.headline)
                Spacer()
                Button { model.newPage() } label: { Image(systemName: "plus") }
                    .buttonStyle(.borderless)
                    .help("新しいページ（⇧⌘N）")
            }
            List {
                ForEach(Array(model.pages.enumerated()), id: \.element.id) { i, page in
                    PageRow(index: i, page: page, current: i == model.currentPage,
                            keyCount: i == model.currentPage ? model.assignments.count : page.keys.count,
                            playing: pageLive.playingPages.contains(page.id))
                        .contentShape(Rectangle())
                        .onTapGesture { model.switchPage(to: i) }
                        .contextMenu {
                            Button("このページにする") { model.switchPage(to: i) }
                            Button("名前を変更…") { model.renamePage(i) }
                            Button("複製") {
                                model.switchPage(to: i)
                                model.newPage(duplicate: true)
                            }
                            Divider()
                            Button("削除…") { model.deletePage(i) }.disabled(model.pages.count <= 1)
                        }
                }
                .onMove { model.movePages(from: $0, to: $1) }
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)
            HStack {
                Button { model.stepPage(by: -1) } label: { Image(systemName: "chevron.up") }
                    .disabled(model.currentPage == 0)
                    .help("前のページ（⌘←）")
                Button { model.stepPage(by: 1) } label: { Image(systemName: "chevron.down") }
                    .disabled(model.currentPage >= model.pages.count - 1)
                    .help("次のページ（⌘→）")
                Spacer()
                Button("複製") { model.newPage(duplicate: true) }
                    .help("このページをコピーして次に入れる（⇧⌘D）")
            }
            .controlSize(.small)
            Text("クリックでキーボードの素材が丸ごと入れ替わります。鳴っている素材は止まりません。")
                .font(.caption2).foregroundStyle(.secondary)
        }
        .padding(10)
        .frame(width: ContentView.pagesWidth)
        .background(Color(white: 0.13))
    }
}

private struct PageRow: View {
    let index: Int
    let page: KeyPage
    let current: Bool
    let keyCount: Int
    let playing: Bool

    var body: some View {
        HStack(spacing: 6) {
            Text("\(index + 1)")
                .font(.system(size: 13, weight: .bold).monospacedDigit())
                .frame(width: 22, height: 22)
                .background(RoundedRectangle(cornerRadius: 4).fill(current ? Color.accentColor : Color(white: 0.3)))
            VStack(alignment: .leading, spacing: 1) {
                Text(page.name).lineLimit(1).fontWeight(current ? .semibold : .regular)
                Text("\(keyCount) キー").font(.caption2).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
            if playing {
                Image(systemName: "speaker.wave.2.fill").font(.caption).foregroundStyle(.green)
                    .help("このページの素材が鳴っています")
            }
        }
        .padding(.vertical, 3)
        .padding(.horizontal, 4)
        .background(RoundedRectangle(cornerRadius: 5).fill(current ? Color.accentColor.opacity(0.25) : Color.clear))
    }
}

/// 映像を出す先（ウィンドウ・各ディスプレイ）を複数選ぶメニュー
struct OutputMenu: View {
    @ObservedObject var video: VideoOutput

    private var summary: String {
        let outs = video.outputs
        if outs.isEmpty { return "映像: 出力なし" }
        if outs.count == 1 {
            return outs[0] == VideoOutput.windowID ? "映像: ウィンドウ" : "映像: \(outs[0])"
        }
        return "映像: \(outs.count) か所に出力"
    }

    var body: some View {
        Menu {
            Toggle("ウィンドウ", isOn: Binding(get: { video.isOn(VideoOutput.windowID) },
                                           set: { video.setOutput(VideoOutput.windowID, on: $0) }))
            Section("ディスプレイ（全画面）") {
                ForEach(video.screenNames, id: \.self) { name in
                    Toggle(name + (video.isControlScreen(name) ? "  ※操作画面" : ""),
                           isOn: Binding(get: { video.isOn(name) }, set: { video.setOutput(name, on: $0) }))
                }
            }
            Divider()
            Button("画面を識別（番号を表示）") { video.identifyScreens() }
            Button("スクリーン設定…") { AppDelegate.openSettings(tab: 1) }
        } label: {
            Label(summary, systemImage: "play.rectangle")
        }
        .help("映像を出す先。ウィンドウと複数のディスプレイに同時に出せます")
    }
}

private struct StatusBar: View {
    @ObservedObject var model: AppModel

    var body: some View {
        HStack {
            Text(model.statusMessage).lineLimit(1)
            Spacer()
            Text("インスタンス #\(model.instanceNumber)")
            Text(model.engineInfo)
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }
}

/// 出力メーター（メーターだけを見て描き直す）
private struct OutputMetersPanel: View {
    @ObservedObject var model: AppModel

    var body: some View {
        MetersView(values: model.values, count: model.visibleOutputChannels, muted: model.mutedOutputs,
                   deviceChannels: model.outputChannels) { ch in
            if model.mutedOutputs.contains(ch) { model.mutedOutputs.remove(ch) } else { model.mutedOutputs.insert(ch) }
        }
    }
}

/// 再生中リスト（素材が増えた・減ったときだけ描き直す。進み具合は層で描く）
private struct CueListView: View {
    let model: AppModel
    @ObservedObject var cueState: CueState

    var body: some View {
        VStack(spacing: 4) {
            ForEach(cueState.cues) { row in CueRowView(model: model, row: row) }
        }
    }
}

private struct Sidebar: View {
    @ObservedObject var model: AppModel
    @ObservedObject var video: VideoOutput
    @AppStorage("meterMode") private var meterMode = "source"

    private var outputMeterTitle: String {
        let shown = Routing.visibleChannels(model.outputChannels)
        return model.outputChannels < shown
            ? "出力メーター（\(shown)ch・デバイスは \(model.outputChannels)ch）"
            : "出力メーター（\(shown)ch）"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("映像モニター").font(.caption).foregroundStyle(.secondary)
            MonitorView(view: video.monitorView)
                .aspectRatio(16 / 9, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 4))
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Color(white: 0.3)))

            HStack {
                Text(meterMode == "output" ? outputMeterTitle : "素材メーター")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Picker("", selection: $meterMode) {
                    Text("素材").tag("source")
                    Text("出力").tag("output")
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .controlSize(.small)
                .frame(width: 110)
                .help("素材 = 選んでいるキーの素材のチャンネルごと / 出力 = 出力チャンネルごと")
            }
            if meterMode == "output" {
                OutputMetersPanel(model: model)
                    .frame(height: 90)
            } else {
                SourceMetersView(model: model, cueState: model.cueState)
                    .frame(height: 112)
            }

            OutputStatusList(feed: model.feed, camera: model.virtualCamera)

            Text("再生中").font(.caption).foregroundStyle(.secondary)
            ScrollView {
                CueListView(model: model, cueState: model.cueState)
            }
            .frame(maxHeight: .infinity)

            HStack {
                Text("全停止のフェード")
                NumberField(value: $model.stopAllFade, width: 50)
                Text("秒").foregroundStyle(.secondary)
            }
            .font(.caption)

            Text("ESC: 全停止 / Shift+ESC: 即停止\nShift+キー: そのキーを即停止\nスライド: 同じキーで次へ／ダブルクリックで一覧\n⌥⌘N: 別インスタンスを起動")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .padding(10)
    }
}

private struct CueRowView: View {
    let model: AppModel
    let row: CueRow

    var body: some View {
        HStack(spacing: 6) {
            Text(KeyboardLayout.label(for: row.key))
                .font(.system(size: 11, weight: .bold))
                .frame(width: 36, height: 20)
                .background(RoundedRectangle(cornerRadius: 3).fill(KeyPalette.color(model.assignments[row.key]?.colorIndex ?? 0)))
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 3) {
                    if row.hasVideo { Image(systemName: "film").font(.system(size: 9)) }
                    if row.slideText != nil { Image(systemName: "rectangle.on.rectangle").font(.system(size: 9)) }
                    Text(row.name).font(.caption).lineLimit(1)
                    if let t = row.slideText { Text(t).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                }
                if let page = row.pageName {
                    Text("（\(page) で鳴らした素材）").font(.caption2).foregroundStyle(.orange).lineLimit(1)
                }
                if let item = row.itemName {
                    Text(item).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                if row.seekable || row.slideText == nil {
                    SeekBarLayer(values: model.values, cueID: row.id) { fraction in
                        model.seek(cueID: row.id, to: fraction)
                    }
                    .frame(height: 26)
                } else {
                    ProgressView(value: row.progress).controlSize(.mini)
                }
            }
            .opacity(row.stopping ? 0.5 : 1)
            // 素材のチャンネルごとの小さなメーター
            // 素材のチャンネルごとの小さなメーター
            if row.channels > 0 {
                MeterBars(values: model.values, source: .material(row.key), count: min(row.channels, 16), spacing: 1)
                    .frame(width: CGFloat(min(row.channels, 16)) * 4, height: 20)
                    .help("素材のチャンネルごとの音量")
            }
            if row.slideText != nil {
                Button { model.stepDeck(cueID: row.id, by: -1) } label: { Image(systemName: "chevron.left") }
                    .buttonStyle(.borderless)
                Button { model.stepDeck(cueID: row.id, by: 1) } label: { Image(systemName: "chevron.right") }
                    .buttonStyle(.borderless)
            }
            Button {
                model.stopCue(id: row.id)
            } label: {
                Image(systemName: "stop.fill")
            }
            .buttonStyle(.borderless)
            .disabled(row.stopping)
        }
    }
}

private struct MetersView: View {
    let values: LiveValues
    let count: Int
    let muted: Set<Int>
    /// これ以降の ch はいまのデバイスにない（メーターのみ）
    let deviceChannels: Int
    let toggleMute: (Int) -> Void

    var body: some View {
        let n = max(count, 1)
        let spacing: CGFloat = n > 16 ? 1 : 3
        VStack(spacing: 2) {
            // 棒は層で描く（毎回 SwiftUI を描き直さない）。番号は変わったときだけ
            MeterBars(values: values, source: .output, count: count, spacing: spacing, deviceChannels: deviceChannels, muted: muted)
            if n <= 24 {
                HStack(spacing: spacing) {
                    ForEach(0..<count, id: \.self) { i in
                        Text(muted.contains(i) ? "M" : "\(i + 1)")
                            .font(.system(size: 9, weight: muted.contains(i) ? .bold : .regular).monospacedDigit())
                            .foregroundStyle(muted.contains(i) ? Color.red : (i < deviceChannels ? Color.secondary : Color(white: 0.35)))
                            .frame(maxWidth: .infinity)
                            .frame(height: 12)
                            .contentShape(Rectangle())
                            .onTapGesture { toggleMute(i) }
                            .help(i < deviceChannels
                                  ? "クリックで出力 \(i + 1) をミュート／解除"
                                  : "出力 \(i + 1) はいまの出力デバイスにありません（メーターのみ・音は出ません）")
                    }
                }
            }
        }
    }
}

enum MeterStyle {
    /// -60dB〜0dB を 0〜1 に
    static func level(_ peak: Float) -> CGFloat {
        guard peak > 0 else { return 0 }
        let db = 20 * log10(peak)
        return CGFloat(min(max((db + 60) / 60, 0), 1))
    }

    static func color(_ peak: Float) -> Color {
        if peak >= 0.99 { return .red }
        if peak >= 0.5 { return .yellow }
        return .green
    }
}

/// 選んでいるキー（なければ最後に鳴らしたキー）の素材を、素材のチャンネルごとに表示する
private struct SourceMetersView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var cueState: CueState

    private var key: UInt16? {
        if let k = model.selectedKey, model.assignments[k] != nil { return k }
        return cueState.cues.last?.key
    }

    var body: some View {
        if let key, let a = model.assignments[key] {
            let info = model.mediaInfo(a)
            let count = info?.clip?.channelCount ?? info?.deck?.sourceChannels ?? (info?.probe.hasAudio == true ? info?.probe.sourceChannels ?? 0 : 0)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 4) {
                    Text(KeyboardLayout.label(for: key))
                        .font(.system(size: 10, weight: .bold))
                        .padding(.horizontal, 4)
                        .background(RoundedRectangle(cornerRadius: 3).fill(KeyPalette.color(a.colorIndex)))
                    Text("\(a.displayName)（\(count)ch）").font(.caption).lineLimit(1)
                }
                if count == 0 {
                    Text("この素材には音声がありません").font(.caption).foregroundStyle(.secondary)
                    Spacer()
                } else {
                    bars(key: key, count: count, destinations: destinations(a, count: count))
                }
            }
        } else {
            Text("キーを選ぶか鳴らすと、その素材のチャンネルごとの音量が出ます")
                .font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    private func bars(key: UInt16, count: Int, destinations: [String]) -> some View {
        let spacing: CGFloat = count > 16 ? 1 : 3
        let showLabels = count <= 16
        return VStack(spacing: 1) {
            // 棒は層で描く（毎回 SwiftUI を描き直さない）
            MeterBars(values: model.values, source: .material(key), count: count, spacing: spacing)
            if showLabels {
                HStack(spacing: spacing) {
                    ForEach(0..<count, id: \.self) { i in
                        VStack(spacing: 1) {
                            Text(channelName(i, count: count)).font(.system(size: 9, weight: .medium).monospacedDigit())
                            Text(destinations[i]).font(.system(size: 8).monospacedDigit()).foregroundStyle(.secondary)
                                .lineLimit(1).minimumScaleFactor(0.6)
                        }
                        .frame(maxWidth: .infinity)
                        .help("素材の ch \(i + 1) の出力先 \(destinations[i])（括弧付きはいまの出力デバイスにない ch で、音は出ません）")
                    }
                }
            }
        }
    }

    private func channelName(_ i: Int, count: Int) -> String {
        switch count {
        case 1: return "M"
        case 2: return i == 0 ? "L" : "R"
        default: return "\(i + 1)"
        }
    }

    /// 素材の各チャンネルがどの出力へ行くか（"→1" "→1,2" "–"）。いまのデバイスにない出力は (3) のように括弧付き
    private func destinations(_ a: KeyAssignment, count: Int) -> [String] {
        let device = model.outputChannels
        var outs = [[Int]](repeating: [], count: count)
        for (s, d) in Routing.routes(for: a, sourceChannels: count, outputChannels: device) where s < count {
            outs[s].append(d)
        }
        return outs.map { list in
            list.isEmpty ? "–" : "→" + list.map { $0 < device ? "\($0 + 1)" : "(\($0 + 1))" }.joined(separator: ",")
        }
    }
}

struct MonitorView: NSViewRepresentable {
    let view: VideoSurfaceView

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        view.frame = container.bounds
        view.autoresizingMask = [.width, .height]
        container.addSubview(view)
        return container
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}
