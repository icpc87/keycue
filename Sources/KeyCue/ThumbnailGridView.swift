import AppKit
import AVFoundation
import SwiftUI

/// グリッド表示：素材をサムネイルで並べる（設定 › 一般 › メイン画面の表示）
struct ThumbnailGridView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var live: LiveState
    @AppStorage(Prefs.thumbSizeKey) private var size = 200.0

    /// 素材の入っているキー（空きキーへ順に入れるときと同じ並び）
    private var codes: [UInt16] {
        KeyboardLayout.fillOrder.filter { model.assignments[$0] != nil }
    }

    var body: some View {
        VStack(spacing: 6) {
            header
            if codes.isEmpty {
                empty
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: size, maximum: size * 1.4), spacing: 12)], spacing: 12) {
                        ForEach(codes, id: \.self) { code in
                            ThumbCard.make(model, live, code).equatable()
                        }
                    }
                    .padding(8)
                }
                .background(RoundedRectangle(cornerRadius: 8).fill(Color(white: 0.12)))
                .onDrop(of: [.fileURL], isTargeted: nil) { providers in dropOnFreeKey(providers) }
            }
        }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Text("\(model.pages.indices.contains(model.currentPage) ? model.pages[model.currentPage].name : "")")
                .font(.headline)
            Text("\(codes.count) 個の素材").font(.caption).foregroundStyle(.secondary)
            Spacer()
            Image(systemName: "photo").font(.caption).foregroundStyle(.secondary)
            Slider(value: $size, in: 120...360)
                .frame(width: 140)
                .help("サムネイルの大きさ")
            Image(systemName: "photo").font(.body).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 4)
    }

    private var empty: some View {
        VStack(spacing: 10) {
            Image(systemName: "square.grid.3x2").font(.system(size: 40)).foregroundStyle(.secondary)
            Text("このページにはまだ素材がありません").font(.title3)
            Text("音声・動画・画像・スライドをここにドロップすると、空いているキーに順に入ります。キーボードの表示に戻すには 設定 › 一般 › メイン画面の表示。")
                .font(.callout).foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 420)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(white: 0.12)))
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in dropOnFreeKey(providers) }
    }

    /// カードの外にドロップしたら、空いている最初のキーへ
    private func dropOnFreeKey(_ providers: [NSItemProvider]) -> Bool {
        KeyMenu.loadURLs(providers) { urls in
            if let first = KeyboardLayout.fillOrder.first(where: { model.assignments[$0] == nil }) {
                model.place(urls, on: first)
            }
        }
        return true
    }
}

/// 1 つの素材のカード：上にサムネイル、下にキー・名前の帯（キーの色）
struct ThumbCard: View, Equatable {
    let model: AppModel
    let code: UInt16
    /// レイヤー表示：ドラッグで別のレイヤーへ動かせる。クリックは離したときに（同じレイヤーの映像と入れ替えて）流す
    var layerMode = false
    let assignment: KeyAssignment
    /// サムネイルを作り直すかどうかの目印（素材・スライドの先頭が変わったら変わる）
    let thumbID: String
    let peaks: [Float]
    let loading: Bool
    let failed: Bool
    let playing: Bool
    let slideText: String?
    let selected: Bool
    /// レイヤー表示のプレビューに組まれている（黄色の枠）
    var previewed = false

    @State private var image: NSImage?
    @State private var pressed = false
    @State private var dropTargeted = false

    static func == (l: ThumbCard, r: ThumbCard) -> Bool {
        l.code == r.code && l.layerMode == r.layerMode && l.assignment == r.assignment && l.thumbID == r.thumbID && l.peaks.count == r.peaks.count
            && l.loading == r.loading && l.failed == r.failed && l.playing == r.playing
            && l.slideText == r.slideText && l.selected == r.selected && l.previewed == r.previewed
    }

    static func make(_ model: AppModel, _ live: LiveState, _ code: UInt16, layerMode: Bool = false) -> ThumbCard {
        let info = model.info(for: code)
        return ThumbCard(model: model, code: code, layerMode: layerMode,
                         assignment: model.assignments[code]!,
                         thumbID: thumbID(model, code, current: live.deckCurrent[code]),
                         peaks: info?.clip?.peaks ?? [],
                         loading: info?.loading == true, failed: info?.error != nil,
                         playing: live.playing.contains(code),
                         slideText: live.slidePosition[code],
                         selected: model.selectedKey == code,
                         previewed: layerMode && Prefs.layerPreviewMode && model.layerPreview[model.assignments[code]!.layer] == .some(code))
    }

    /// current: スライド・複数素材で、いま出ている素材（流れていなければ nil で先頭）
    static func thumbID(_ model: AppModel, _ code: UInt16, current: String?) -> String {
        guard let a = model.assignments[code] else { return "" }
        let info = model.info(for: code)
        let first = a.isDeck ? (current ?? model.enabledDeckItems(code).first?.id ?? "") : ""
        return "\(a.mediaKey)|\(info?.loading == true)|\(first)"
    }

    private var color: Color { KeyPalette.color(assignment.colorIndex) }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 6)
        VStack(spacing: 0) {
            preview
                .aspectRatio(16 / 9, contentMode: .fit)
                .overlay(alignment: .topLeading) { badges }
                .overlay(alignment: .bottom) {
                    if playing {
                        // 再生の進み具合（層で描くので、再生中に SwiftUI を描き直さない）
                        KeyProgressLayer(values: model.values, code: code, cornerRadius: 0)
                            .frame(height: 5)
                            .background(Color.black.opacity(0.5))
                            .allowsHitTesting(false)
                    }
                }
                .clipped()
            label
        }
        .background(Color(white: 0.2))
        .clipShape(shape)
        .overlay(shape.strokeBorder(borderColor, lineWidth: playing ? 3 : (selected || dropTargeted ? 2 : 0.5)))
        .overlay(shape.inset(by: playing ? 3 : 0).strokeBorder(Color(red: 1, green: 0.85, blue: 0.1), lineWidth: previewed ? 3 : 0))
        .scaleEffect(pressed ? 0.97 : 1)
        .animation(.easeOut(duration: 0.08), value: pressed)
        // クリックはキーを押したのと同じ（押しているあいだだけ鳴るモードにも対応）。⌘ クリックは選ぶだけ。
        // AppKit の層で受けるので、KeyCue が前面でなくても 1 回目のクリックで鳴る
        .overlay(layerMode ? AnyView(layerClicks) : AnyView(gridClicks))
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            KeyMenu.loadURLs(providers) { urls in model.place(urls, on: code) }
            return true
        }
        .contextMenu { KeyMenu(model: model, code: code, assignment: assignment) }
        .help("\(KeyboardLayout.label(for: code)): \(assignment.displayName)\nクリックで再生（キーと同じ）・⌘ クリックで選択・右クリックでメニュー" + (layerMode ? "・ドラッグで別のレイヤーへ" : ""))
        .task(id: thumbID) { image = await ThumbnailCache.image(model: model, code: code, id: thumbID) }
    }

    /// グリッド：押した瞬間に流す（押しているあいだだけ鳴るモードにも対応）
    private var gridClicks: some View {
        ClickCatcher(
            down: { modifiers in
                pressed = true
                NSApp.keyWindow?.makeFirstResponder(nil)
                if modifiers.contains(.command) {
                    model.selectedKey = code
                } else {
                    model.handleKey(code: code, down: true, shift: modifiers.contains(.shift), isRepeat: false)
                }
            },
            up: { modifiers in
                pressed = false
                if !modifiers.contains(.command) { model.handleKey(code: code, down: false, shift: false, isRepeat: false) }
            },
            name: "card-\(code)")
    }

    /// レイヤー：ドラッグと見分けるため、離したときに流す（押しているあいだだけ鳴るモードは押した瞬間に）
    private var layerClicks: some View {
        // プレビューがあるときは、クリックでプレビューに組む（「ライブへ」で出す）
        let preview = Prefs.layerPreviewMode
        let hold = assignment.mode == .hold && !assignment.isDeck && !preview
        return ClickCatcher(
            down: { modifiers in
                pressed = true
                NSApp.keyWindow?.makeFirstResponder(nil)
                if hold && !modifiers.contains(.command) { model.layerTake(code, shift: modifiers.contains(.shift)) }
            },
            up: { modifiers in
                pressed = false
                if modifiers.contains(.command) {
                    model.selectedKey = code
                } else if preview {
                    model.previewPick(code)
                } else if hold {
                    model.handleKey(code: code, down: false, shift: false, isRepeat: false)
                } else {
                    model.layerTake(code, shift: modifiers.contains(.shift))
                }
            },
            dragString: ThumbCard.dragPrefix + String(code),
            dragImage: image,
            dragStarted: {
                pressed = false
                if hold { model.handleKey(code: code, down: false, shift: false, isRepeat: false) }
            },
            name: "card-\(code)")
    }

    /// レイヤー表示でカードを運ぶときの中身
    static let dragPrefix = "keycue-key:"

    @ViewBuilder
    private var preview: some View {
        ZStack {
            Color.black
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
            } else if assignment.isLive {
                VStack(spacing: 6) {
                    Image(systemName: assignment.liveInput?.videoID != nil ? "web.camera" : "mic")
                        .font(.system(size: 30))
                    Text("ライブ入力").font(.caption)
                }
                .foregroundStyle(color)
            } else if !peaks.isEmpty {
                // 音声だけの素材は波形
                WaveformView(peaks: peaks, loading: false)
                    .equatable()
                    .padding(.vertical, 18)
                    .overlay(Image(systemName: "speaker.wave.2.fill").font(.title2).foregroundStyle(color))
            } else if loading {
                ProgressView().controlSize(.small)
            } else {
                Image(systemName: "doc").font(.largeTitle).foregroundStyle(.secondary)
            }
        }
    }

    private var badges: some View {
        HStack(spacing: 4) {
            Text(KeyboardLayout.label(for: code))
                .font(.system(size: 13, weight: .bold))
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 4).fill(color))
            if assignment.isImage && assignment.sources == nil {
                Image(systemName: "photo")
            } else if assignment.isDeck {
                Image(systemName: assignment.isList ? "list.number" : "rectangle.on.rectangle")
            } else if assignment.isLive {
                Image(systemName: "dot.radiowaves.left.and.right")
            } else if model.info(for: code)?.hasVideo == true && assignment.showVideo {
                Image(systemName: "film")
            }
            if assignment.group > 0 { Text("G\(assignment.group)").font(.caption2.bold()) }
            Spacer()
            if loading {
                ProgressView().controlSize(.mini)
            } else if failed {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
            }
            if let slideText {
                Text(slideText).font(.caption.bold().monospacedDigit())
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .background(Capsule().fill(Color.black.opacity(0.6)))
            }
        }
        .font(.caption)
        .foregroundStyle(.white)
        .shadow(color: .black.opacity(0.6), radius: 1)
        .padding(5)
    }

    private var label: some View {
        HStack(spacing: 6) {
            Text(assignment.displayName)
                .font(.system(size: 12, weight: playing ? .semibold : .regular))
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 0)
            if playing { Image(systemName: "play.fill").font(.caption2) }
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 7)
        .frame(height: 24)
        .frame(maxWidth: .infinity)
        .background(color.opacity(playing ? 1 : 0.75))
    }

    private var borderColor: Color {
        if dropTargeted { return .accentColor }
        if playing { return Color(red: 1, green: 0.45, blue: 0.2) }
        if selected { return .white }
        return Color.black.opacity(0.5)
    }
}

/// サムネイルを作って覚えておく（動画は 1 秒あたり、画像・スライドは先頭の素材）
enum ThumbnailCache {
    @MainActor private static var cache: [String: NSImage] = [:]

    @MainActor
    static func image(model: AppModel, code: UInt16, id: String) async -> NSImage? {
        if let hit = cache[id] { return hit }
        guard let a = model.assignments[code], !a.isLive, let info = model.info(for: code), !info.loading else { return nil }
        var result: NSImage?
        if let deck = info.deck {
            // スライド・画像・複数素材：いま出ている素材（id の最後）。なければ最初に流れる素材（音声なら次の画像か動画）
            let items = model.enabledDeckItems(code)
            let currentID = id.split(separator: "|").last.map(String.init) ?? ""
            let start = items.firstIndex { $0.id == currentID } ?? 0
            if let item = items[start...].first(where: { $0.kind != .audio }) ?? items.first(where: { $0.kind != .audio }) {
                result = await deck.thumbnailAsync(for: item)
            }
        } else if info.hasVideo {
            result = await videoFrame(info.playURL ?? a.url)
        }
        if let result {
            if cache.count > 300 { cache.removeAll() }
            cache[id] = result
        }
        return result
    }

    private static func videoFrame(_ url: URL) async -> NSImage? {
        let asset = AVURLAsset(url: url)
        let gen = AVAssetImageGenerator(asset: asset)
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: 640, height: 640)
        gen.requestedTimeToleranceBefore = .positiveInfinity
        gen.requestedTimeToleranceAfter = .positiveInfinity
        let duration = (try? await asset.load(.duration).seconds) ?? 0
        let t = duration.isFinite && duration > 0 ? min(1, duration * 0.1) : 0
        guard let (cg, _) = try? await gen.image(at: CMTime(seconds: t, preferredTimescale: 600)) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
    }
}

/// 左クリックだけを受け取る透明な層（右クリックとファイルのドロップは下の SwiftUI に通す）
struct ClickCatcher: NSViewRepresentable {
    let down: (NSEvent.ModifierFlags) -> Void
    let up: (NSEvent.ModifierFlags) -> Void
    /// あれば、押したまま動かすとこの文字列を運ぶドラッグになる（そのときは up を呼ばない）
    var dragString: String? = nil
    var dragImage: NSImage? = nil
    var dragStarted: () -> Void = {}
    /// 自動テストでカードを探すための名前
    var name: String? = nil

    final class View: NSView, NSDraggingSource {
        var down: (NSEvent.ModifierFlags) -> Void = { _ in }
        var up: (NSEvent.ModifierFlags) -> Void = { _ in }
        var dragString: String?
        var dragImage: NSImage?
        var dragStarted: () -> Void = {}
        private var isDown = false
        private var downPoint = NSPoint.zero

        func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
            context == .withinApplication ? .move : []
        }

        override func mouseDragged(with event: NSEvent) {
            guard isDown, let dragString else { return }
            let p = event.locationInWindow
            guard hypot(p.x - downPoint.x, p.y - downPoint.y) > 5 else { return }
            isDown = false
            dragStarted()
            let item = NSDraggingItem(pasteboardWriter: dragString as NSString)
            let w = min(bounds.width, 180), h = w * 9 / 16
            let local = convert(p, from: nil)
            item.setDraggingFrame(NSRect(x: local.x - w / 2, y: local.y - h / 2, width: w, height: h),
                                  contents: dragImage ?? NSImage(systemSymbolName: "rectangle.on.rectangle", accessibilityDescription: nil))
            beginDraggingSession(with: [item], event: event, source: self)
        }

        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func hitTest(_ point: NSPoint) -> NSView? {
            // control クリックはメニュー用。左ボタンの操作だけ受ける
            guard let e = NSApp.currentEvent,
                  [.leftMouseDown, .leftMouseDragged, .leftMouseUp].contains(e.type),
                  !e.modifierFlags.contains(.control) else { return nil }
            return super.hitTest(point)
        }

        override func mouseDown(with event: NSEvent) {
            isDown = true
            downPoint = event.locationInWindow
            down(event.modifierFlags)
        }

        override func mouseUp(with event: NSEvent) {
            guard isDown else { return }
            isDown = false
            up(event.modifierFlags)
        }
    }

    func makeNSView(context: Context) -> View { View() }

    func updateNSView(_ v: View, context: Context) {
        v.down = down
        v.up = up
        v.dragString = dragString
        v.dragImage = dragImage
        v.dragStarted = dragStarted
        v.identifier = name.map { NSUserInterfaceItemIdentifier(rawValue: $0) }
    }
}
