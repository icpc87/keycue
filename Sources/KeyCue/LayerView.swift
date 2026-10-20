import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// レイヤー表示：映像の重なり順（レイヤー 9 が前面）ごとに素材を段に並べる（設定 › 一般 › メイン画面の表示）
struct LayerView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var live: LiveState
    @AppStorage(Prefs.thumbSizeKey) private var size = 200.0
    @AppStorage(Prefs.layerReplaceKey) private var replace = true
    @AppStorage(Prefs.layerMonitorsKey) private var monitors = "both"

    var body: some View {
        let all = KeyboardLayout.fillOrder.filter { model.assignments[$0] != nil }
        let visual = all.filter { model.isVisual(model.assignments[$0]!) }
        let audio = all.filter { !visual.contains($0) }
        let cardWidth = size * 0.8
        VStack(spacing: 6) {
            header(count: all.count)
            if monitors != "none" {
                MonitorsBar(model: model, live: live, monitors: monitors)
            }
            ScrollView {
                VStack(spacing: 6) {
                    ForEach((1...9).reversed(), id: \.self) { layer in
                        LayerRow(model: model, live: live, layer: layer,
                                 codes: visual.filter { model.assignments[$0]?.layer == layer }, cardWidth: cardWidth)
                    }
                    if !audio.isEmpty {
                        AudioRow(model: model, live: live, codes: audio, cardWidth: cardWidth)
                    }
                }
                .padding(8)
            }
            .background(RoundedRectangle(cornerRadius: 8).fill(Color(white: 0.12)))
        }
    }

    private func header(count: Int) -> some View {
        HStack(spacing: 10) {
            Text(model.pages.indices.contains(model.currentPage) ? model.pages[model.currentPage].name : "").font(.headline)
            Text("\(count) 個の素材").font(.caption).foregroundStyle(.secondary)
            Spacer()
            Picker("", selection: $monitors) {
                Text("プレビューとプログラム").tag("both")
                Text("プログラムだけ").tag("program")
                Text("プレビューだけ").tag("preview")
                Text("モニターなし").tag("none")
            }
            .labelsHidden()
            .fixedSize()
            .help("上に出すモニター。プレビューがあるときは、カードのクリックでプレビューに組み、「ライブへ」で出します")
            if !Prefs.layerPreviewMode {
                Toggle("同じレイヤーは入れ替え", isOn: $replace)
                    .toggleStyle(.checkbox)
                    .help("クリックしたとき、同じレイヤーで流れている映像を新しい素材のフェード IN の秒数で消して入れ替えます（1 つのレイヤーに 1 つずつ）")
            }
            Image(systemName: "photo").font(.caption).foregroundStyle(.secondary)
            Slider(value: $size, in: 120...360).frame(width: 120).help("サムネイルの大きさ")
            Image(systemName: "photo").font(.body).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 4)
    }
}

/// 1 つのレイヤーの段。左に番号と、いま出ている素材
private struct LayerRow: View {
    let model: AppModel
    @ObservedObject var live: LiveState
    let layer: Int
    let codes: [UInt16]
    let cardWidth: CGFloat

    @State private var targeted = false
    @ObservedObject private var sizes = LayerSizes.shared
    @State private var contentHeight: CGFloat = 0

    var body: some View {
        let playing = codes.filter { live.playing.contains($0) }
        let width = cardWidth * sizes.scale(layer)
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: 8) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("レイヤー \(layer)").font(.system(size: 13, weight: .semibold))
                    if layer == 9 || layer == 1 {
                        Text(layer == 9 ? "いちばん前" : "いちばん後ろ").font(.caption2).foregroundStyle(.secondary)
                    }
                    if let first = playing.first, let a = model.assignments[first] {
                        Label(a.displayName, systemImage: "play.fill")
                            .font(.caption).lineLimit(1)
                            .foregroundStyle(Color(red: 1, green: 0.6, blue: 0.3))
                    }
                    HStack(spacing: 6) {
                        Button { sizes.stepScale(layer, -1) } label: { Image(systemName: "minus.magnifyingglass") }
                            .disabled(sizes.scale(layer) <= LayerSizes.scaleSteps.first!)
                        Button { sizes.stepScale(layer, 1) } label: { Image(systemName: "plus.magnifyingglass") }
                            .disabled(sizes.scale(layer) >= LayerSizes.scaleSteps.last!)
                    }
                    .buttonStyle(.borderless)
                    .help("このレイヤーのカードを小さく・大きくします")
                }
                .frame(width: 100, alignment: .leading)
                ClearLayerCard(model: model, layer: layer, playing: playing, width: cardWidth * 0.55)
                if codes.isEmpty {
                    Text("カードをここへドラッグすると、レイヤー \(layer) になります")
                        .font(.caption).foregroundStyle(.tertiary)
                        .frame(maxWidth: .infinity, minHeight: 30, alignment: .leading)
                } else {
                    // 段に入りきらなければ折り返して次の行へ（横にはみ出して隠れないように）。
                    // 段の高さを決めてあれば、その中で縦にスクロール
                    let grid = LazyVGrid(columns: [GridItem(.adaptive(minimum: width, maximum: width), spacing: 10, alignment: .leading)],
                                         alignment: .leading, spacing: 10) {
                        ForEach(codes, id: \.self) { code in
                            ThumbCard.make(model, live, code, layerMode: true)
                                .equatable()
                                .frame(width: width)
                        }
                    }
                    .padding(.vertical, 3)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(GeometryReader { g in
                        Color.clear
                            .onAppear { contentHeight = g.size.height }
                            .onChange(of: g.size.height) { _, h in contentHeight = h }
                    })
                    if let h = sizes.heights[layer] {
                        ScrollView(.vertical) { grid }.frame(height: CGFloat(h))
                    } else {
                        grid
                    }
                }
            }
            RowResizeHandle(sizes: sizes, layer: layer, contentHeight: max(contentHeight, 60))
        }
        .padding(.horizontal, 6)
        .padding(.top, 6)
        .background(RoundedRectangle(cornerRadius: 6).fill(background(playing: !playing.isEmpty)))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.accentColor, lineWidth: targeted ? 2 : 0))
        .onDrop(of: [.plainText, .fileURL], isTargeted: $targeted) { providers in drop(providers) }
    }

    private func background(playing: Bool) -> Color {
        if targeted { return Color.accentColor.opacity(0.2) }
        return playing ? Color(red: 0.24, green: 0.16, blue: 0.11) : Color(white: 0.16)
    }

    /// カード → このレイヤーへ。ファイル → 空いているキーに入れて、このレイヤーにする
    private func drop(_ providers: [NSItemProvider]) -> Bool {
        let files = providers.filter { $0.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) }
        if !files.isEmpty {
            KeyMenu.loadURLs(files) { urls in
                guard let first = KeyboardLayout.fillOrder.first(where: { model.assignments[$0] == nil }) else { return }
                let before = Set(model.assignments.keys)
                model.place(urls, on: first)
                for code in Set(model.assignments.keys).subtracting(before) { model.setLayer(code, layer) }
            }
            return true
        }
        for p in providers where p.canLoadObject(ofClass: NSString.self) {
            _ = p.loadObject(ofClass: NSString.self) { s, _ in
                guard let s = s as? String, s.hasPrefix(ThumbCard.dragPrefix),
                      let code = UInt16(s.dropFirst(ThumbCard.dragPrefix.count)) else { return }
                DispatchQueue.main.async { model.setLayer(code, layer) }
            }
        }
        return true
    }
}

/// 映像を出さない素材（音声だけ）の段
private struct AudioRow: View {
    let model: AppModel
    @ObservedObject var live: LiveState
    let codes: [UInt16]
    let cardWidth: CGFloat

    var body: some View {
        HStack(alignment: .center, spacing: 8) {
            VStack(alignment: .leading, spacing: 3) {
                Text("音声").font(.system(size: 13, weight: .semibold))
                Text("映像なし").font(.caption2).foregroundStyle(.secondary)
            }
            .frame(width: 100, alignment: .leading)
            ScrollView(.horizontal) {
                LazyHStack(spacing: 10) {
                    ForEach(codes, id: \.self) { code in
                        ThumbCard.make(model, live, code).equatable().frame(width: cardWidth)
                    }
                }
                .padding(.vertical, 3)
            }
        }
        .padding(6)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color(white: 0.14)))
    }
}

/// 上のモニター：プレビュー（次に出すもの）・ライブへ・プログラム（いま出ているもの）
private struct MonitorsBar: View {
    @ObservedObject var model: AppModel
    @ObservedObject var live: LiveState
    let monitors: String
    @AppStorage(Prefs.takeStyleKey) private var takeStyle = "smooth"
    @AppStorage(Prefs.takeDurationKey) private var takeDuration = 0.5

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            if monitors == "both" || monitors == "preview" {
                monitor(title: "プレビュー", color: .green) { PreviewComposite(model: model, live: live) }
                controls
            }
            if monitors == "both" || monitors == "program" {
                monitor(title: "ライブ", color: .red) { MonitorView(view: model.video.programView) }
            }
        }
        .frame(height: 210)
        .padding(.horizontal, 4)
    }

    private func monitor<Content: View>(title: String, color: Color, @ViewBuilder content: () -> Content) -> some View {
        VStack(spacing: 4) {
            content()
                .aspectRatio(16 / 9, contentMode: .fit)
                .background(Color.black)
                .clipShape(RoundedRectangle(cornerRadius: 4))
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(color.opacity(0.7), lineWidth: 1.5))
            HStack(spacing: 4) {
                Circle().fill(color).frame(width: 7, height: 7)
                Text(title).font(.caption)
            }
        }
        .frame(maxWidth: .infinity)
    }

    private var controls: some View {
        VStack(spacing: 8) {
            Picker("", selection: $takeStyle) {
                Text("カット").tag("cut")
                Text("スムーズ").tag("smooth")
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 140)
            .help("「ライブへ」の切り替え方。スムーズはフェード")
            if takeStyle == "smooth" {
                HStack(spacing: 4) {
                    NumberField(value: $takeDuration, width: 44)
                    Text("秒").foregroundStyle(.secondary)
                }
            }
            Button {
                model.take(smooth: takeStyle == "smooth")
            } label: {
                Label("ライブへ", systemImage: "arrow.right.circle.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .padding(.horizontal, 6)
            }
            .controlSize(.large)
            .disabled(!model.hasPendingPreview)
            .help("プレビューに組んだものをライブ（出力）に出します。カードのクリックでレイヤーごとに組みます")
        }
        .frame(width: 150)
    }
}

/// プレビューに組んだものを重ねて見せる（各素材の 1 枚目の絵。レイヤー 1 が奥）
private struct PreviewComposite: View {
    @ObservedObject var model: AppModel
    @ObservedObject var live: LiveState

    var body: some View {
        ZStack {
            Color.black
            ForEach(1...9, id: \.self) { layer in
                if let code = model.previewKey(layer: layer), model.assignments[code] != nil {
                    PreviewLayerImage(model: model, code: code, thumbID: ThumbCard.thumbID(model, code, current: live.deckCurrent[code]))
                }
            }
            if !model.hasPendingPreview {
                Text("カードをクリックして組む").font(.caption).foregroundStyle(.secondary)
                    .padding(4).background(Capsule().fill(Color.black.opacity(0.5)))
                    .frame(maxHeight: .infinity, alignment: .bottom).padding(6)
            }
        }
    }
}

private struct PreviewLayerImage: View {
    let model: AppModel
    let code: UInt16
    let thumbID: String
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fit)
            } else if model.assignments[code]?.isLive == true {
                Image(systemName: "web.camera").font(.largeTitle).foregroundStyle(.secondary)
            } else {
                // 中身がないと読み込みが動かないので、透明な面を置く
                Color.clear
            }
        }
        .task(id: thumbID) { image = await ThumbnailCache.image(model: model, code: code, id: thumbID) }
    }
}

/// 段の先頭の「レイヤーを消去」：プレビューでそのレイヤーを空にする（プレビューがなければすぐ止める）
private struct ClearLayerCard: View {
    @ObservedObject var model: AppModel
    let layer: Int
    let playing: [UInt16]
    let width: CGFloat

    var body: some View {
        let picked = model.layerPreview[layer] == .some(nil)
        VStack(spacing: 0) {
            Color.black.aspectRatio(16 / 9, contentMode: .fit)
            Text("レイヤーを消去").font(.caption2).foregroundStyle(.white)
                .frame(maxWidth: .infinity).frame(height: 20).background(Color(white: 0.25))
        }
        .frame(width: width)
        .clipShape(RoundedRectangle(cornerRadius: 5))
        .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(picked ? Color(red: 1, green: 0.85, blue: 0.1) : Color(white: 0.35), lineWidth: picked ? 3 : 1))
        .contentShape(Rectangle())
        .onTapGesture {
            if Prefs.layerPreviewMode {
                model.previewClear(layer: layer)
            } else {
                for c in playing { model.stopKey(c, fade: model.assignments[c]?.fadeOut ?? 1) }
            }
        }
        .help(Prefs.layerPreviewMode ? "プレビューでこのレイヤーを空にします（「ライブへ」で消えます）" : "このレイヤーで流れている素材を止めます")
    }
}

/// レイヤーごとの段の高さとカードの大きさ（全インスタンス共通で覚えておく）
final class LayerSizes: ObservableObject {
    static let shared = LayerSizes()
    private static let heightsKey = "pref.layerHeights"
    private static let scalesKey = "pref.layerCardScales"
    static let scaleSteps: [Double] = [0.5, 0.65, 0.8, 1, 1.25, 1.5, 1.75, 2]

    /// 段の高さ（入っていなければ、カードが全部見える高さ）
    @Published private(set) var heights: [Int: Double]
    /// カードの大きさ（表示全体の大きさに掛ける）
    @Published private(set) var scales: [Int: Double]

    private init() {
        func load(_ key: String) -> [Int: Double] {
            let d = UserDefaults.standard.dictionary(forKey: key) as? [String: Double] ?? [:]
            return Dictionary(uniqueKeysWithValues: d.compactMap { k, v in Int(k).map { ($0, v) } })
        }
        heights = load(LayerSizes.heightsKey)
        scales = load(LayerSizes.scalesKey)
    }

    func scale(_ layer: Int) -> Double { scales[layer] ?? 1 }

    func setHeight(_ layer: Int, _ h: Double?) {
        heights[layer] = h.map { max(50, $0) }
        save(heights, LayerSizes.heightsKey)
    }

    /// カードを 1 段階大きく（+1）・小さく（-1）
    func stepScale(_ layer: Int, _ by: Int) {
        let steps = LayerSizes.scaleSteps
        let i = steps.firstIndex { $0 >= scale(layer) - 0.001 } ?? 3
        let v = steps[min(max(i + by, 0), steps.count - 1)]
        scales[layer] = v == 1 ? nil : v
        save(scales, LayerSizes.scalesKey)
    }

    private func save(_ d: [Int: Double], _ key: String) {
        UserDefaults.standard.set(Dictionary(uniqueKeysWithValues: d.map { (String($0.key), $0.value) }), forKey: key)
    }
}

/// 段の下のふち：ドラッグで段の高さを変える（ダブルクリックで自動に戻す）
private struct RowResizeHandle: View {
    @ObservedObject var sizes: LayerSizes
    let layer: Int
    let contentHeight: CGFloat
    @State private var start: CGFloat?
    @State private var hover = false

    var body: some View {
        RoundedRectangle(cornerRadius: 2)
            .fill(Color.white.opacity(hover || start != nil ? 0.35 : 0.1))
            .frame(width: 48, height: 4)
            .frame(maxWidth: .infinity, minHeight: 9)
            .contentShape(Rectangle())
            .onHover { inside in
                hover = inside
                if inside { NSCursor.resizeUpDown.push() } else { NSCursor.pop() }
            }
            .gesture(DragGesture(minimumDistance: 1)
                .onChanged { v in
                    if start == nil { start = CGFloat(sizes.heights[layer] ?? Double(contentHeight)) }
                    sizes.setHeight(layer, Double(start! + v.translation.height))
                }
                .onEnded { _ in start = nil })
            .onTapGesture(count: 2) { sizes.setHeight(layer, nil) }
            .help("ドラッグでこの段の高さを変えます（ダブルクリックで、カードが全部見える高さに戻します）")
    }
}
