import AppKit
import AVKit
import SwiftUI

struct DetailPanel: View {
    @ObservedObject var model: AppModel
    /// 再生位置は波形の線と再生ボタンだけが見る（設定欄全体は描き直さない）
    let live: LiveState

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(model.selectedKey.map(KeyboardLayout.label(for:)) ?? "–")
                .font(.system(size: 46, weight: .light))
                .frame(width: 90, height: 70)
                .minimumScaleFactor(0.4)
                .foregroundStyle(Color(white: 0.85))
            if let code = model.selectedKey, let a = model.assignments[code] {
                AssignedDetail(model: model, code: code, assignment: a, info: model.mediaInfo(a), live: live)
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    Text("[未割り当て]").font(.title2).foregroundStyle(.secondary)
                    Text("音声・動画・スライド（pptx / key / pdf）をキーにドラッグ＆ドロップするか、下のボタンで選びます。カメラやキャプチャ機器などのライブ入力も割り当てられます。複数ファイルをドロップすると後ろの空きキーへ順に入ります。")
                        .foregroundStyle(.secondary)
                    if let code = model.selectedKey {
                        Button("ファイルを割り当て…") { model.chooseFile(for: code) }
                        Button("ライブ入力を割り当て…") { model.chooseLiveInput(for: code) }
                            .help("Web カメラ・キャプチャ機器・オーディオインターフェースなどの入力を、このキーで出し入れします")
                    }
                }
                Spacer()
            }
        }
        .padding(10)
        // 設定が折り返して行が増えたら、その分だけ高くなる（キーボードの図が縮む）
        .frame(maxWidth: .infinity, minHeight: 250, alignment: .topLeading)
        .fixedSize(horizontal: false, vertical: true)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(white: 0.2)))
    }
}

private struct AssignedDetail: View {
    let model: AppModel
    let code: UInt16
    let assignment: KeyAssignment
    let info: MediaInfo?
    let live: LiveState

    @State private var showRouting = false

    private func binding<T>(_ keyPath: WritableKeyPath<KeyAssignment, T>) -> Binding<T> {
        Binding(get: { assignment[keyPath: keyPath] },
                set: { value in
                    var a = model.assignments[code] ?? assignment
                    a[keyPath: keyPath] = value
                    model.update(code, a)
                })
    }

    private var isSlides: Bool { assignment.isDeck }
    private var isVisual: Bool { isSlides || info?.hasVideo == true }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 14) {
                VStack(alignment: .leading, spacing: 7) {
                    header
                    if isSlides { slideRow } else { playRow }
                    audioRow
                    flowRow
                    colorRow
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .layoutPriority(1)
                if let live = assignment.liveInput {
                    if live.videoID != nil {
                        LivePreviewView(hub: model.video.captureHub, deviceID: live.videoID)
                            .frame(width: 200, height: 112)
                            .clipShape(RoundedRectangle(cornerRadius: 4))
                            .help("入力のプレビュー")
                    }
                } else if let deck = info?.deck {
                    DeckSummary(model: model, code: code, deck: deck).frame(width: 200)
                } else if info?.hasVideo == true {
                    PreviewPlayerView(url: info?.playURL ?? assignment.url)
                        .frame(width: 200, height: 112)
                        .clipShape(RoundedRectangle(cornerRadius: 4))
                        .help("プレビュー（音は消してあります）")
                }
            }
            if !isSlides && !assignment.isLive {
                WaveformView(peaks: info?.clip?.peaks ?? [], loading: info?.loading == true)
                    .equatable()
                    .overlay(PlayheadLayer(values: model.values, code: code, color: KeyPalette.color(assignment.colorIndex)).allowsHitTesting(false))
                    .frame(height: 44)
            }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                // 名前は右クリックの「名前を変更…」だけで変える（入力欄にしておくとキーが文字として入ってしまうため）
                Text(assignment.displayName)
                    .font(.system(size: 20))
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                    .contextMenu { Button("名前を変更…") { model.renameKey(code) } }
                    .help("右クリックで名前を変更")
                PlayStopButton(model: model, live: live, code: code)
                if assignment.isLive {
                    Button("入力を変更…") { model.chooseLiveInput(for: code) }
                } else {
                    Button("素材を追加…") { model.chooseFile(for: code) }
                        .help("このキーの後ろに素材を足します（押すたび順に切り替わります）")
                    Button("差し替え…") { model.replaceFile(for: code) }
                }
                Button("解除") { model.clear(code) }
            }
            Text(assignment.liveInput.map { "ライブ入力: \($0.name)" } ?? assignment.sources.map { s in
                "\(s.count) 個の素材: " + s.map { URL(fileURLWithPath: $0).lastPathComponent }.joined(separator: " → ")
            } ?? assignment.path)
                .font(.caption).foregroundStyle(.secondary)
                .lineLimit(1).truncationMode(.middle)
            Text(infoLine)
                .font(.caption.monospacedDigit())
                .foregroundStyle(info?.error == nil ? Color.secondary : Color.yellow)
                .lineLimit(2)
        }
    }

    private var infoLine: String {
        guard let info else { return "" }
        if info.loading { return info.progressText }
        if let e = info.error { return "⚠︎ \(e)" }
        let p = info.probe
        if assignment.isLive {
            var parts: [String] = []
            if p.hasVideo { parts.append("映像 \(Int(p.videoSize.width))×\(Int(p.videoSize.height))") }
            if p.hasAudio { parts.append("音声 \(p.sourceChannels)ch") }
            parts.append("押すと入力を出し、もう一度押すとフェードして止めます")
            return parts.joined(separator: " · ")
        }
        if let deck = info.deck {
            let videos = deck.items.filter { $0.kind == .video }.count
            let audios = deck.items.filter { $0.kind == .audio }.count
            if deck.isList {
                let images = deck.items.filter { $0.kind == .image }.count
                return "押すたび次へ · 素材 \(deck.items.count) 個（画像 \(images) · 動画 \(videos) · 音声 \(audios)）"
            }
            let engine = deck.manifest.engine == "pdf" ? "" : " · \(deck.manifest.engine == "powerpoint" ? "PowerPoint" : "Keynote") で変換"
            return "スライド \(deck.manifest.slideCount) 枚 · 動画 \(videos) · 音声 \(audios)\(engine)"
        }
        var parts: [String] = []
        if p.hasAudio {
            let tracks = p.audioTracks > 1 ? "音声 \(p.audioTracks) トラック 計 " : ""
            parts.append("\(tracks)\(p.sourceChannels)ch \(String(format: "%.1f", p.sourceSampleRate / 1000))kHz")
        }
        if p.hasVideo { parts.append("映像 \(Int(p.videoSize.width))×\(Int(p.videoSize.height))") }
        parts.append(AssignedDetail.time(p.duration))
        parts.append(ByteCountFormatter.string(fromByteCount: p.fileSize, countStyle: .file))
        if info.audioViaPlayer { parts.append("長尺のため音声は映像プレーヤーから出力（ルーティング無効）") }
        if info.playURL != nil { parts.append("この Mac で再生できない形式のため、透明部分を残して ProRes 4444 に変換して再生") }
        return parts.joined(separator: " · ")
    }

    static func time(_ s: Double) -> String {
        guard s.isFinite else { return "--:--" }
        let total = Int(s)
        return String(format: "%d:%02d.%02d", total / 60, total % 60, Int((s - Double(total)) * 100))
    }

    private var playRow: some View {
        WrapHStack {
            Picker("モード", selection: binding(\.mode)) {
                ForEach(KeyMode.allCases) { Text($0.label).tag($0) }
            }
            .frame(width: 320)
            field("フェード IN", binding(\.fadeIn))
            field("OUT", binding(\.fadeOut))
            if !assignment.isLive { Toggle("ループ", isOn: binding(\.loop)) }
        }
    }

    private var slideRow: some View {
        WrapHStack {
            Button {
                model.openDeckTab(code)
            } label: {
                Label("素材一覧を開く", systemImage: "sidebar.right")
            }
            .help("キーをダブルクリックしても開きます。送り方・切替・素材ごとの設定は一覧で")
            field("フェード IN", binding(\.fadeIn))
            field("OUT", binding(\.fadeOut))
            Button(assignment.isList ? "読み込み直す" : "変換し直す") { model.reload(code) }
        }
    }

    private var audioRow: some View {
        WrapHStack {
            HStack(spacing: 6) {
                Text("音量")
                Slider(value: binding(\.volume), in: 0...2).frame(width: 140)
                Text(dbText).font(.caption.monospacedDigit()).frame(width: 56, alignment: .leading)
            }
            HStack(spacing: 6) {
                Button {
                    showRouting.toggle()
                } label: {
                    Label("出力ルーティング…", systemImage: "square.grid.3x3.topleft.filled")
                }
                .popover(isPresented: $showRouting, arrowEdge: .bottom) {
                    RoutingMatrixView(model: model, code: code, sourceChannels: info?.sourceChannels ?? 2)
                }
                Text(routingSummary)
                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: 240, alignment: .leading)
            }
            if info?.hasVideo == true {
                Toggle("映像を出す", isOn: binding(\.showVideo))
            }
        }
    }

    private var routingSummary: String {
        let auto = assignment.matrix == nil && assignment.outputMap.isEmpty ? "自動: " : ""
        return auto + Routing.describe(assignment, sourceChannels: info?.sourceChannels ?? 2, outputChannels: model.outputChannels)
    }

    private var flowRow: some View {
        WrapHStack {
            Picker("次の素材が流れたら", selection: binding(\.onNext)) {
                ForEach(NextBehavior.allCases) { Text($0.label).tag($0) }
            }
            .frame(width: 330)
            .help("ほかのキーを押して新しい素材が始まったとき、このキーの再生を続けるか止めるか")
            Picker("対象", selection: binding(\.group)) {
                Text("すべてのキー").tag(0)
                ForEach(1...8, id: \.self) { Text("グループ \($0)").tag($0) }
            }
            .frame(width: 170)
            .help("グループを選ぶと、同じグループのキーが流れたときだけ止まります。グループは止める側・止められる側の両方に使います")
            if isVisual {
                Stepper(value: binding(\.layer), in: 1...9) {
                    Text("映像レイヤー \(assignment.layer)")
                }
                .fixedSize()
                .help("大きいほど前面に重なります。同じレイヤーなら後から流したものが上")
                KeyScreenMenu(model: model, video: model.video, code: code, assignment: assignment)
            }
        }
    }

    private var colorRow: some View {
        HStack(spacing: 4) {
            ForEach(KeyPalette.colors.indices, id: \.self) { i in
                Circle()
                    .fill(KeyPalette.colors[i])
                    .frame(width: 14, height: 14)
                    .overlay(Circle().strokeBorder(.white, lineWidth: assignment.colorIndex == i ? 2 : 0))
                    .onTapGesture { binding(\.colorIndex).wrappedValue = i }
            }
        }
    }

    private var dbText: String {
        assignment.volume <= 0.0001 ? "-∞ dB" : String(format: "%+.1f dB", 20 * log10(assignment.volume))
    }

    private func field(_ title: String, _ value: Binding<Double>) -> some View {
        HStack(spacing: 4) {
            Text(title)
            NumberField(value: value)
            Text("秒").foregroundStyle(.secondary)
        }
    }
}

/// 横に並べて、幅が足りなくなったら次の行へ折り返す
struct WrapHStack: Layout {
    var spacing: CGFloat = 14
    var lineSpacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width ?? .infinity, subviews: subviews)
        let width = rows.map(\.width).max() ?? 0
        let height = rows.reduce(0) { $0 + $1.height } + lineSpacing * CGFloat(max(rows.count - 1, 0))
        return CGSize(width: min(width, proposal.width ?? width), height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(width: bounds.width, subviews: subviews) {
            var x = bounds.minX
            for (index, size) in row.items {
                // 行の中では縦の真ん中にそろえる
                subviews[index].place(at: CGPoint(x: x, y: y + (row.height - size.height) / 2),
                                      proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + lineSpacing
        }
    }

    private struct Row {
        var items: [(Int, CGSize)] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func arrange(width maxWidth: CGFloat, subviews: Subviews) -> [Row] {
        var rows: [Row] = []
        var row = Row()
        for (i, view) in subviews.enumerated() {
            var size = view.sizeThatFits(.unspecified)
            size.width = min(size.width, maxWidth)
            let needed = row.items.isEmpty ? size.width : row.width + spacing + size.width
            if !row.items.isEmpty && needed > maxWidth {
                rows.append(row)
                row = Row()
            }
            row.width = row.items.isEmpty ? size.width : row.width + spacing + size.width
            row.height = max(row.height, size.height)
            row.items.append((i, size))
        }
        if !row.items.isEmpty { rows.append(row) }
        return rows
    }
}

/// このキーの映像を出す画面（何も選ばなければ、選んでいるすべての出力）
private struct KeyScreenMenu: View {
    let model: AppModel
    @ObservedObject var video: VideoOutput
    let code: UInt16
    let assignment: KeyAssignment

    private var label: String {
        guard let s = assignment.screens else { return "出す画面: すべて" }
        if s.isEmpty { return "出す画面: なし" }
        if s.count == 1 { return "出す画面: " + (s[0] == VideoOutput.windowID ? "ウィンドウ" : String(s[0].prefix(while: { $0 != ":" }))) }
        return "出す画面: \(s.count) か所"
    }

    private func set(_ screens: [String]?) {
        var a = model.assignments[code] ?? assignment
        a.screens = screens
        model.update(code, a)
    }

    var body: some View {
        Menu {
            Toggle("すべての出力", isOn: Binding(get: { assignment.screens == nil }, set: { if $0 { set(nil) } }))
            Divider()
            ForEach(video.outputs, id: \.self) { id in
                let on = assignment.screens?.contains(id) ?? true
                Toggle(id == VideoOutput.windowID ? "ウィンドウ" : id, isOn: Binding(get: { on }, set: { newValue in
                    var list = assignment.screens ?? video.outputs
                    if newValue { if !list.contains(id) { list.append(id) } } else { list.removeAll { $0 == id } }
                    set(Set(list) == Set(video.outputs) ? nil : list)
                }))
            }
        } label: {
            Text(label)
        }
        .fixedSize()
        .help("このキーの映像をどの出力に出すか。新しく流したときから反映されます")
    }
}

/// 元チャンネル × 出力チャンネルの表。マスをクリックしてつなぐ／外す
struct RoutingMatrixView: View {
    @ObservedObject var model: AppModel
    let code: UInt16
    let sourceChannels: Int
    @State private var quick = ""

    /// 表に出す出力の列数（デバイスが 8ch 未満でも 8）
    private var outputs: Int { model.visibleOutputChannels }
    /// 出力デバイスの実際の ch 数（自動ルーティングの折り返しに使う）
    private var deviceOutputs: Int { model.outputChannels }

    private func sourceName(_ s: Int) -> String {
        switch sourceChannels {
        case 1: return "モノラル"
        case 2: return s == 0 ? "L（ch 1）" : "R（ch 2）"
        default: return "ch \(s + 1)"
        }
    }

    var body: some View {
        let a = model.assignments[code]
        let m = a.map { Routing.matrix(for: $0, sourceChannels: sourceChannels, outputChannels: deviceOutputs) } ?? []
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("出力ルーティング").font(.headline)
                if a?.matrix == nil && (a?.outputMap.isEmpty ?? true) {
                    Text("自動").font(.caption).padding(.horizontal, 6).background(Capsule().fill(Color(white: 0.3)))
                }
                Spacer()
                Text("\(model.outputDeviceName)（\(deviceOutputs)ch）").foregroundStyle(.secondary)
            }
            ScrollView([.horizontal, .vertical]) {
                Grid(horizontalSpacing: 3, verticalSpacing: 3) {
                    GridRow {
                        Text("元 ＼ 出力").font(.caption).foregroundStyle(.secondary).frame(width: 84, alignment: .leading)
                        ForEach(0..<outputs, id: \.self) { o in outputHeader(o) }
                    }
                    ForEach(0..<sourceChannels, id: \.self) { s in
                        GridRow {
                            Text(sourceName(s)).font(.callout).frame(width: 84, alignment: .leading)
                            ForEach(0..<outputs, id: \.self) { o in
                                let on = s < m.count && m[s].contains(o)
                                let connected = o < deviceOutputs
                                Rectangle()
                                    .fill(on ? Color(red: 0.1, green: 0.45, blue: 1).opacity(connected ? 1 : 0.55)
                                             : Color(white: connected ? 0.42 : 0.28))
                                    .frame(width: 34, height: 28)
                                    .contentShape(Rectangle())
                                    .onTapGesture { toggle(source: s, output: o, current: m) }
                            }
                        }
                    }
                }
                .padding(2)
            }
            .frame(maxWidth: 760, maxHeight: 420)
            .fixedSize(horizontal: outputs <= 18, vertical: sourceChannels <= 10)

            HStack {
                TextField("まとめて指定（例: 3 / 1,2 / 5-8）", text: $quick)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 220)
                    .onSubmit(applyQuick)
                Button("適用", action: applyQuick)
                Spacer()
                Button("自動に戻す") { setMatrix(nil, map: "") }
                Button("全キーに適用") { model.applyRoutingToAll(from: code) }
            }
            Text("M = その出力をミュート、T = 1kHz のテストトーン（どちらも全キー共通・マスター音量に従う）")
                .font(.caption).foregroundStyle(.secondary)
            if deviceOutputs < outputs {
                Text("暗い列（\(deviceOutputs + 1)〜\(outputs)）はいまの出力デバイスにない ch です。つないでおくと右のメーターで確認でき、多 ch のインターフェースに切り替えるとそのまま鳴ります。")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(14)
        .frame(minWidth: 420)
        .background(ClearInitialFocus())
        .onDisappear { model.testToneChannel = nil }
    }

    private func outputHeader(_ o: Int) -> some View {
        let muted = model.mutedOutputs.contains(o)
        let tone = model.testToneChannel == o
        return VStack(spacing: 2) {
            Text("\(o + 1)").font(.caption.monospacedDigit())
                .foregroundStyle(o < deviceOutputs ? Color.primary : Color.secondary)
            HStack(spacing: 1) {
                smallToggle("M", on: muted, color: .red) {
                    if muted { model.mutedOutputs.remove(o) } else { model.mutedOutputs.insert(o) }
                }
                smallToggle("T", on: tone, color: .orange) { model.testToneChannel = tone ? nil : o }
            }
        }
        .frame(width: 34)
    }

    private func smallToggle(_ title: String, on: Bool, color: Color, action: @escaping () -> Void) -> some View {
        Text(title)
            .font(.system(size: 9, weight: .bold))
            .frame(width: 16, height: 14)
            .background(RoundedRectangle(cornerRadius: 2).fill(on ? color : Color(white: 0.3)))
            .onTapGesture(perform: action)
    }

    private func toggle(source s: Int, output o: Int, current: [[Int]]) {
        var m = current
        while m.count < sourceChannels { m.append([]) }
        if let i = m[s].firstIndex(of: o) { m[s].remove(at: i) } else { m[s].append(o); m[s].sort() }
        setMatrix(m, map: "")
    }

    private func applyQuick() {
        guard var a = model.assignments[code] else { return }
        a.matrix = nil
        a.outputMap = quick
        let m = Routing.matrix(for: a, sourceChannels: sourceChannels, outputChannels: deviceOutputs)
        setMatrix(m, map: quick)
    }

    private func setMatrix(_ m: [[Int]]?, map: String) {
        guard var a = model.assignments[code] else { return }
        a.matrix = m
        a.outputMap = map
        model.update(code, a)
    }
}

/// スライド素材の概要（先頭の素材と数）
struct DeckSummary: View {
    let model: AppModel
    let code: UInt16
    let deck: Deck

    var body: some View {
        let items = model.enabledDeckItems(code)
        VStack(spacing: 4) {
            ZStack {
                Color.black
                if let first = items.first(where: { $0.kind != .audio }), let image = deck.thumbnail(for: first) {
                    Image(nsImage: image).resizable().aspectRatio(contentMode: .fit)
                }
            }
            .frame(height: 112)
            .clipShape(RoundedRectangle(cornerRadius: 4))
            .onTapGesture(count: 2) { model.openDeckTab(code) }
            Text("流す素材 \(items.count) 個（ダブルクリックで一覧）").font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// 波形（素材が変わったときだけ描く。再生位置は上に重ねた Playhead が動く）
struct WaveformView: View, Equatable {
    let peaks: [Float]
    let loading: Bool

    var body: some View {
        Canvas { ctx, size in
            ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(Color(red: 0.08, green: 0.14, blue: 0.13)))
            guard !peaks.isEmpty else { return }
            let mid = size.height / 2
            let columns = max(Int(size.width), 1)
            var path = Path()
            for x in 0..<columns {
                let a = x * peaks.count / columns
                let b = max(a + 1, (x + 1) * peaks.count / columns)
                let peak = peaks[a..<min(b, peaks.count)].max() ?? 0
                let h = CGFloat(peak) * (mid - 1)
                path.addRect(CGRect(x: CGFloat(x), y: mid - h, width: 1, height: max(h * 2, 0.5)))
            }
            ctx.fill(path, with: .color(Color(white: 0.75)))
        }
        .overlay {
            if loading { ProgressView().controlSize(.small) }
        }
        .clipShape(RoundedRectangle(cornerRadius: 4))
    }
}

/// 割り当てた動画のプレビュー（スクラブ可・ミュート）
/// ライブ入力のプレビュー（出力と同じキャプチャをまとめて使う。使用が許可されているときだけ映す）
struct LivePreviewView: NSViewRepresentable {
    let hub: VideoCaptureHub
    let deviceID: String?

    final class Coordinator {
        var deviceID: String?
        weak var hub: VideoCaptureHub?
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let v = NSView()
        v.wantsLayer = true
        v.layer?.backgroundColor = NSColor.black.cgColor
        return v
    }

    func updateNSView(_ v: NSView, context: Context) {
        guard context.coordinator.deviceID != deviceID else { return }
        Self.detach(v, context.coordinator)
        guard AVCaptureDevice.authorizationStatus(for: .video) == .authorized,
              let device = CaptureDevices.device(deviceID), let session = hub.acquire(device) else { return }
        context.coordinator.deviceID = deviceID
        context.coordinator.hub = hub
        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspect
        layer.frame = v.bounds
        layer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        v.layer?.addSublayer(layer)
    }

    private static func detach(_ v: NSView, _ c: Coordinator) {
        v.layer?.sublayers?.forEach { $0.removeFromSuperlayer() }
        if let id = c.deviceID { c.hub?.release(id) }
        c.deviceID = nil
    }

    static func dismantleNSView(_ v: NSView, coordinator: Coordinator) { detach(v, coordinator) }
}

struct PreviewPlayerView: NSViewRepresentable {
    let url: URL

    final class Coordinator { var url: URL? }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> AVPlayerView {
        let v = AVPlayerView()
        v.controlsStyle = .inline
        v.showsFullScreenToggleButton = false
        v.videoGravity = .resizeAspect
        return v
    }

    func updateNSView(_ v: AVPlayerView, context: Context) {
        guard context.coordinator.url != url else { return }
        context.coordinator.url = url
        v.player?.pause()
        let player = AVPlayer(url: url)
        player.isMuted = true
        v.player = player
    }

    static func dismantleNSView(_ v: AVPlayerView, coordinator: Coordinator) {
        v.player?.pause()
        v.player = nil
    }
}

/// ポップオーバーを開いたとき入力欄に自動でカーソルが入らないようにする（キーで再生・停止できるように）
struct ClearInitialFocus: NSViewRepresentable {
    final class View: NSView {
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window else { return }
            DispatchQueue.main.async { window.makeFirstResponder(nil) }
        }
    }

    func makeNSView(context: Context) -> View { View() }
    func updateNSView(_ nsView: View, context: Context) {}
}

/// 再生／停止ボタン（鳴っているかどうかが変わったときだけ描き直す）
private struct PlayStopButton: View {
    let model: AppModel
    @ObservedObject var live: LiveState
    let code: UInt16

    var body: some View {
        let playing = live.playing.contains(code)
        Button {
            model.togglePlayback(code)
        } label: {
            Label(playing ? "停止" : "再生", systemImage: playing ? "stop.fill" : "play.fill")
        }
    }
}
