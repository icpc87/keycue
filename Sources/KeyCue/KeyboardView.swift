import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct KeyboardView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var live: LiveState

    var body: some View {
        GeometryReader { geo in
            let unit = min(geo.size.width / KeyboardLayout.unitsWide, geo.size.height / KeyboardLayout.unitsHigh)
            VStack(spacing: 0) {
                ForEach(KeyboardLayout.rows) { row in
                    HStack(spacing: 0) {
                        ForEach(row.keys) { key in
                            cell(key, unit: unit)
                                .frame(width: key.width * unit, height: row.height * unit)
                        }
                    }
                }
            }
            .frame(width: unit * KeyboardLayout.unitsWide, height: unit * KeyboardLayout.unitsHigh)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .aspectRatio(KeyboardLayout.unitsWide / KeyboardLayout.unitsHigh, contentMode: .fit)
    }

    @ViewBuilder
    private func cell(_ key: KeySpec, unit: CGFloat) -> some View {
        switch key.kind {
        case .gap:
            Color.clear
        case .pad:
            PadCell(label: key.label, unit: unit)
        case .stopAll:
            StopAllCell(unit: unit) { model.stopAll(immediate: NSEvent.modifierFlags.contains(.shift)) }
        case .key:
            let info = model.info(for: key.code)
            // 中身が変わらないキーは描き直さない（再生中の進み具合が変わったキーだけ描き直す）
            KeyCell(model: model, spec: key, unit: unit,
                    assignment: model.assignments[key.code],
                    hasVideo: info?.hasVideo == true, loading: info?.loading == true, failed: info?.error != nil,
                    playing: live.playing.contains(key.code),
                    slideText: live.slidePosition[key.code],
                    selected: model.selectedKey == key.code)
                .equatable()
        }
    }
}

private struct PadCell: View {
    let label: String
    let unit: CGFloat

    var body: some View {
        RoundedRectangle(cornerRadius: unit * 0.06)
            .fill(Color(white: 0.19))
            .overlay(Text(label).font(.system(size: unit * 0.14)).foregroundStyle(Color(white: 0.4)))
            .padding(unit * 0.025)
    }
}

private struct StopAllCell: View {
    let unit: CGFloat
    let action: () -> Void

    var body: some View {
        RoundedRectangle(cornerRadius: unit * 0.06)
            .fill(Color(white: 0.24))
            .overlay(
                VStack(spacing: 2) {
                    Text("ESC").font(.system(size: unit * 0.2, weight: .medium))
                    Text("全停止").font(.system(size: unit * 0.13))
                }
                .foregroundStyle(Color(red: 0.95, green: 0.4, blue: 0.4))
            )
            .padding(unit * 0.025)
            .contentShape(Rectangle())
            .onTapGesture(perform: action)
            .help("全キーをフェードアウト（Shift+ESC で即停止）")
    }
}

struct KeyCell: View, Equatable {
    let model: AppModel
    let spec: KeySpec
    let unit: CGFloat
    let assignment: KeyAssignment?
    let hasVideo: Bool
    let loading: Bool
    let failed: Bool
    let playing: Bool
    let slideText: String?
    let selected: Bool

    @State private var dropTargeted = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: unit * 0.06)
        ZStack(alignment: .bottomLeading) {
            shape.fill(background)
            if playing {
                // 進み具合の帯は層で描く（再生中に SwiftUI を描き直さない）
                KeyProgressLayer(values: model.values, code: spec.code, cornerRadius: unit * 0.06)
                    .allowsHitTesting(false)
            }
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .padding(.horizontal, unit * 0.04)
                .padding(.top, unit * 0.03)
        }
        .overlay(shape.strokeBorder(borderColor, lineWidth: selected || playing || dropTargeted ? 2 : 0.5))
        .padding(unit * 0.025)
        .contentShape(Rectangle())
        .onTapGesture {
            NSApp.keyWindow?.makeFirstResponder(nil)
            model.selectedKey = spec.code
        }
        .simultaneousGesture(TapGesture(count: 2).onEnded {
            if assignment?.isDeck == true { model.openDeckTab(spec.code) }
        })
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            // すでに素材があるキーなら「後ろに追加／差し替え」を確認する
            KeyMenu.loadURLs(providers) { urls in model.place(urls, on: spec.code) }
            return true
        }
        .contextMenu { menu }
        .help(assignment?.path ?? "ファイルをドロップして割り当て")
    }

    @ViewBuilder
    private var content: some View {
        VStack(spacing: unit * 0.02) {
            HStack(spacing: 2) {
                Text(spec.label)
                    .font(.system(size: unit * (spec.label.count > 2 ? 0.15 : 0.2), weight: .medium))
                if assignment?.isLive == true {
                    Image(systemName: assignment?.liveInput?.videoID != nil ? "web.camera" : "mic")
                        .font(.system(size: unit * 0.12))
                } else if assignment?.isDeck == true {
                    Image(systemName: assignment?.isList == true ? "list.number" : "rectangle.on.rectangle")
                        .font(.system(size: unit * 0.12))
                } else if hasVideo && assignment?.showVideo == true {
                    Image(systemName: "film").font(.system(size: unit * 0.12))
                }
                if let g = assignment?.group, g > 0 {
                    Text("G\(g)")
                        .font(.system(size: unit * 0.1, weight: .bold))
                        .padding(.horizontal, 2)
                        .background(RoundedRectangle(cornerRadius: 2).fill(Color.black.opacity(0.35)))
                }
                if loading {
                    ProgressView().controlSize(.mini)
                } else if failed {
                    Image(systemName: "exclamationmark.triangle.fill").font(.system(size: unit * 0.13)).foregroundStyle(.yellow)
                }
            }
            if let assignment {
                Text(assignment.displayName)
                    .font(.system(size: unit * 0.135))
                    .multilineTextAlignment(.center)
                    .lineLimit(slideText == nil ? 3 : 2)
                    .minimumScaleFactor(0.7)
            }
            if let slideText {
                Text(slideText).font(.system(size: unit * 0.13, weight: .bold).monospacedDigit())
            }
        }
        .foregroundStyle(assignment == nil ? Color(white: 0.85) : .white)
        .shadow(color: .black.opacity(assignment == nil ? 0 : 0.5), radius: 1)
    }

    private var background: Color {
        guard let assignment else { return Color(white: 0.28) }
        let base = KeyPalette.color(assignment.colorIndex)
        return playing ? base.opacity(1) : base.opacity(0.78)
    }

    private var borderColor: Color {
        if dropTargeted { return .accentColor }
        if playing { return Color(red: 0.4, green: 1, blue: 0.6) }
        if selected { return .white }
        return Color.black.opacity(0.4)
    }

    @ViewBuilder
    private var menu: some View { KeyMenu(model: model, code: spec.code, assignment: assignment) }
}

/// キー（サムネイル）の右クリックメニュー
struct KeyMenu: View {
    let model: AppModel
    let code: UInt16
    let assignment: KeyAssignment?

    var body: some View {
        Button(assignment == nil ? "ファイルを割り当て…" : "素材を追加…") { model.chooseFile(for: code) }
        Button("ライブ入力（カメラ・キャプチャ・音声入力）を割り当て…") { model.chooseLiveInput(for: code) }
        if let assignment {
            Button(model.isRunning(code) ? "停止" : "再生") { model.togglePlayback(code) }
            Button("名前を変更…") { model.renameKey(code) }
            if model.pages.count > 1 {
                Menu("ほかのページの同じキーにコピー") {
                    ForEach(Array(model.pages.enumerated()), id: \.element.id) { i, page in
                        if i != model.currentPage {
                            Button("\(i + 1). \(page.name)") { model.copyKey(code, toPage: i) }
                        }
                    }
                }
            }
            if assignment.isDeck {
                Button("素材一覧を開く") { model.openDeckTab(code) }
            }
            Menu("映像レイヤー（\(assignment.layer)）") {
                ForEach((1...9).reversed(), id: \.self) { n in
                    Button("レイヤー \(n)" + (n == 9 ? "（いちばん前）" : n == 1 ? "（いちばん後ろ）" : "") + (n == assignment.layer ? " ✓" : "")) {
                        model.setLayer(code, n)
                    }
                }
            }
            Button("Finder で表示") { NSWorkspace.shared.activateFileViewerSelecting([assignment.url]) }
            Menu("色") {
                ForEach(KeyPalette.colors.indices, id: \.self) { i in
                    Button("色 \(i + 1)") {
                        var a = assignment
                        a.colorIndex = i
                        model.update(code, a)
                    }
                }
            }
            Divider()
            Button("割り当てを解除") { model.clear(code) }
        }
    }

    static func loadURLs(_ providers: [NSItemProvider], completion: @escaping ([URL]) -> Void) {
        var results = [URL?](repeating: nil, count: providers.count)
        let group = DispatchGroup()
        for (i, p) in providers.enumerated() {
            group.enter()
            _ = p.loadObject(ofClass: URL.self) { url, _ in
                results[i] = url
                group.leave()
            }
        }
        group.notify(queue: .main) { completion(results.compactMap { $0 }) }
    }
}

extension KeyCell {
    /// model（参照）とドロップ中の表示（@State）は比べない
    static func == (l: KeyCell, r: KeyCell) -> Bool {
        l.spec.id == r.spec.id && l.unit == r.unit && l.assignment == r.assignment
            && l.hasVideo == r.hasVideo && l.loading == r.loading && l.failed == r.failed
            && l.playing == r.playing && l.slideText == r.slideText && l.selected == r.selected
    }
}
