import AppKit
import SwiftUI

/// スライド素材のキーをダブルクリックすると横に開くタブ。再生順の並び替え・再生・素材ごとの設定
struct DeckPanel: View {
    static let width: CGFloat = 360

    @ObservedObject var model: AppModel
    @ObservedObject var live: LiveState

    var body: some View {
        VStack(spacing: 0) {
            tabStrip
            Divider()
            if let code = model.activeDeckTab, let a = model.assignments[code] {
                DeckTabContent(model: model, live: live, code: code, assignment: a, info: model.mediaInfo(a))
                    .id(code)
            } else {
                Spacer()
            }
        }
        .frame(width: DeckPanel.width)
        .background(Color(white: 0.17))
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

    private var tabStrip: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 2) {
                ForEach(model.deckTabs, id: \.self) { code in
                    let active = model.activeDeckTab == code
                    HStack(spacing: 6) {
                        Text(KeyboardLayout.label(for: code))
                            .font(.system(size: 11, weight: .bold))
                            .padding(.horizontal, 5)
                            .background(RoundedRectangle(cornerRadius: 3)
                                .fill(KeyPalette.color(model.assignments[code]?.colorIndex ?? 0)))
                        Text(model.assignments[code]?.displayName ?? "")
                            .font(.caption).lineLimit(1).frame(maxWidth: 140)
                        Button {
                            model.closeDeckTab(code)
                        } label: {
                            Image(systemName: "xmark").font(.system(size: 9, weight: .bold))
                        }
                        .buttonStyle(.borderless)
                        .help("タブを閉じる")
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: 5).fill(active ? Color(white: 0.3) : Color.clear))
                    .contentShape(Rectangle())
                    .onTapGesture { model.activeDeckTab = code }
                }
            }
            .padding(6)
        }
    }
}

private struct DeckTabContent: View {
    let model: AppModel
    @ObservedObject var live: LiveState
    let code: UInt16
    let assignment: KeyAssignment
    let info: MediaInfo?

    @State private var selection: String?

    private var items: [DeckItem] { model.deckItems(code) }
    private var running: Bool { live.slidePosition[code] != nil }

    private func binding<T>(_ keyPath: WritableKeyPath<KeyAssignment, T>) -> Binding<T> {
        Binding(get: { assignment[keyPath: keyPath] },
                set: { value in
                    var a = model.assignments[code] ?? assignment
                    a[keyPath: keyPath] = value
                    model.update(code, a)
                })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            header
            settings
            if let deck = info?.deck {
                list(deck)
                if let id = selection, let item = items.first(where: { $0.id == id }) {
                    Divider()
                    DeckItemInspector(model: model, code: code, item: item, deck: deck)
                }
            } else if let info, info.loading {
                HStack { ProgressView().controlSize(.small); Text(info.progressText) }.padding()
                Spacer()
            } else {
                Text(info?.error ?? "読み込めませんでした").foregroundStyle(.yellow).padding()
                Spacer()
            }
        }
        .padding(10)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(assignment.displayName).font(.headline).lineLimit(1)
                .contextMenu { Button("名前を変更…") { model.renameKey(code) } }
            HStack(spacing: 6) {
                if running {
                    Button { model.deckStep(code: code, by: -1) } label: { Image(systemName: "backward.fill") }
                        .help("前の素材")
                    Button { model.stopKey(code, fade: assignment.fadeOut) } label: { Label("停止", systemImage: "stop.fill") }
                    Button { model.deckStep(code: code, by: 1) } label: { Image(systemName: "forward.fill") }
                        .help("次の素材（キーを押すのと同じ）")
                } else {
                    Button { model.startKey(code) } label: { Label("最初から再生", systemImage: "play.fill") }
                }
                if let p = live.slidePosition[code] {
                    Text(p).font(.callout.monospacedDigit()).foregroundStyle(.green)
                }
                Spacer()
                Button {
                    model.addFiles(to: code)
                } label: {
                    Label("素材を追加…", systemImage: "plus")
                }
                .help("このキーの最後に素材を足します（押すたび順に切り替わります）")
                Menu {
                    if let deck = info?.deck, !deck.isList {
                        Button("書き出したフォルダを Finder で表示") { NSWorkspace.shared.open(deck.folder) }
                    }
                    Button("並びと素材の設定を元に戻す") { model.resetDeckItems(code) }
                    Button(assignment.isList ? "読み込み直す" : "変換し直す") { model.reload(code) }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
        }
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 10) {
                Toggle("画像を自動で送る", isOn: binding(\.slideAuto))
                if assignment.slideAuto {
                    numberField(binding(\.slideInterval))
                    Text("秒ごと").foregroundStyle(.secondary)
                }
            }
            HStack(spacing: 8) {
                Picker("切替", selection: binding(\.slideTransitionStyle)) {
                    ForEach(TransitionStyle.allCases) { Text($0.shortLabel).tag($0) }
                }
                .fixedSize()
                .help("素材を切り替えるときの効果（PowerPoint・Keynote のフェードに相当）。素材ごとにも変えられます")
                if assignment.slideTransitionStyle != .cut {
                    numberField(binding(\.slideTransition))
                    Text("秒").foregroundStyle(.secondary)
                }
            }
            Toggle("最後の次は先頭へ", isOn: binding(\.slideLoop))
            Toggle("一覧の素材をクリックしただけで切り替え", isOn: binding(\.clickToTake))
                .help("オンにすると一覧の素材をクリックしただけで出力が切り替わります。オフのときはダブルクリックか ▶")
        }
        .font(.callout)
    }

    private func list(_ deck: Deck) -> some View {
        let current = live.deckCurrent[code]
        let numbers = enabledNumbers
        return List(selection: $selection) {
            ForEach(items) { item in
                DeckItemRow(item: item, deck: deck, number: numbers[item.id], playing: current == item.id,
                            defaultAdvance: assignment.slideAuto ? assignment.slideInterval : 0,
                            takeOnClick: assignment.clickToTake) {
                    model.deckJump(code, itemID: item.id)
                }
                .tag(item.id)
            }
            .onMove { from, to in model.moveDeckItems(code, from: from, to: to) }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .overlay {
            if items.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "tray.and.arrow.down").font(.largeTitle)
                    Text("ここに音声・動画・画像・pptx / key / pdf をドロップ\nキーを右クリック →「プレイリストに追加」でも入ります")
                        .multilineTextAlignment(.center)
                }
                .foregroundStyle(.secondary)
                .allowsHitTesting(false)
            }
        }
        // 一覧にファイルを落とすと最後に追加
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            DeckPanel.loadURLs(providers) { urls in
                let files = urls.filter(AppModel.isSupported)
                if !files.isEmpty { model.appendToKey(files, code: code) }
            }
            return true
        }
        .contextMenu(forSelectionType: String.self) { ids in
            if let id = ids.first, let item = items.first(where: { $0.id == id }) {
                Button("ここから再生") { model.deckJump(code, itemID: id) }
                Button("名前を変更…") { model.renameDeckItem(code, itemID: id) }
                Button(item.enabled ? "無効にする（飛ばす）" : "有効にする") {
                    var changed = item
                    changed.enabled.toggle()
                    model.updateDeckItem(code, changed)
                }
                if assignment.sources != nil {
                    Divider()
                    Button(id.hasPrefix("d") ? "この資料ごとキーから外す" : "キーから外す") {
                        model.removeFromList(code, itemID: id)
                    }
                }
            }
        } primaryAction: { ids in
            if let id = ids.first { model.deckJump(code, itemID: id) }
        }
    }

    /// 有効な素材だけに振る再生順の番号
    private var enabledNumbers: [String: Int] {
        var out: [String: Int] = [:]
        var n = 0
        for item in items where item.enabled {
            n += 1
            out[item.id] = n
        }
        return out
    }

    private func numberField(_ value: Binding<Double>) -> some View {
        NumberField(value: value)
    }
}

private struct DeckItemRow: View {
    let item: DeckItem
    let deck: Deck
    let number: Int?
    let playing: Bool
    let defaultAdvance: Double
    /// クリックしただけで切り替える
    let takeOnClick: Bool
    let play: () -> Void

    @State private var thumb: NSImage?

    var body: some View {
        HStack(spacing: 8) {
            Text(number.map(String.init) ?? "–")
                .font(.caption.monospacedDigit())
                .frame(width: 22, alignment: .trailing)
                .foregroundStyle(.secondary)
            ZStack {
                Color.black
                if let thumb {
                    Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fit)
                } else {
                    Image(systemName: item.symbol).foregroundStyle(.secondary)
                }
            }
            .frame(width: 96, height: 54)
            .clipShape(RoundedRectangle(cornerRadius: 3))
            .overlay(RoundedRectangle(cornerRadius: 3).strokeBorder(playing ? Color.green : Color.clear, lineWidth: 2))
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Image(systemName: item.symbol).font(.caption)
                    Text(item.title).lineLimit(1)
                }
                Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
            if playing {
                Image(systemName: "play.fill").foregroundStyle(.green)
            } else {
                Button(action: play) { Image(systemName: "play.circle") }
                    .buttonStyle(.borderless)
                    .help("ここから再生")
                    .disabled(!item.enabled)
            }
        }
        .opacity(item.enabled ? 1 : 0.4)
        .contentShape(Rectangle())
        .simultaneousGesture(TapGesture().onEnded {
            if takeOnClick && item.enabled { play() }
        })
        .padding(.vertical, 2)
        .task(id: item.id) { thumb = await deck.thumbnailAsync(for: item) }
    }

    private var detail: String {
        if !item.enabled { return "無効（飛ばす）" }
        switch item.kind {
        case .image:
            let after = item.advanceAfter ?? defaultAdvance
            return after > 0 ? "\(DeckItemRow.seconds(after)) 秒で次へ" : "キーで次へ"
        case .video, .audio:
            var parts: [String] = []
            if let d = deck.probes[item.id]?.duration, d.isFinite { parts.append(DeckItemRow.clock(d)) }
            parts.append("終わったら: \(item.endAction.label)")
            if item.volume != 1 { parts.append(String(format: "%+.1f dB", 20 * log10(max(item.volume, 0.0001)))) }
            return parts.joined(separator: " · ")
        }
    }

    static func seconds(_ v: Double) -> String { String(format: v == v.rounded() ? "%.0f" : "%.1f", v) }

    static func clock(_ s: Double) -> String { String(format: "%d:%02d", Int(s) / 60, Int(s) % 60) }
}

/// 選んだ素材の設定
private struct DeckItemInspector: View {
    let model: AppModel
    let code: UInt16
    let item: DeckItem
    let deck: Deck

    private enum Advance: Hashable { case inherit, manual, seconds }

    private func binding<T>(_ keyPath: WritableKeyPath<DeckItem, T>) -> Binding<T> {
        Binding(get: { item[keyPath: keyPath] },
                set: { value in
                    var changed = model.deckItems(code).first { $0.id == item.id } ?? item
                    changed[keyPath: keyPath] = value
                    model.updateDeckItem(code, changed)
                })
    }

    private var advanceMode: Binding<Advance> {
        Binding(get: {
            switch item.advanceAfter {
            case nil: return .inherit
            case let v? where v <= 0: return .manual
            default: return .seconds
            }
        }, set: { mode in
            switch mode {
            case .inherit: binding(\.advanceAfter).wrappedValue = nil
            case .manual: binding(\.advanceAfter).wrappedValue = 0
            case .seconds: binding(\.advanceAfter).wrappedValue = max(item.advanceAfter ?? 5, 1)
            }
        })
    }

    /// "inherit"（おまかせ）か、効果の名前
    private var transitionChoice: Binding<String> {
        Binding(get: { item.transitionStyle?.rawValue ?? "inherit" },
                set: { raw in
                    var it = item
                    if let style = TransitionStyle(rawValue: raw) {
                        let current = inherited.seconds
                        it.transitionStyle = style
                        it.transition = it.transition ?? (current > 0 ? current : 0.5)
                    } else {
                        it.transitionStyle = nil
                        it.transition = nil
                    }
                    model.updateDeckItem(code, it)
                })
    }

    /// 個別に決めなかったときの切り替え（スライドの設定 → キーの設定）
    private var inherited: (seconds: Double, style: TransitionStyle) {
        var plain = item
        plain.transition = nil
        plain.transitionStyle = nil
        guard let a = model.assignments[code] else { return (0.3, .dissolve) }
        return model.transition(into: plain, a)
    }

    private var inheritedLabel: String {
        let t = inherited
        let from = item.fileTransitionStyle != nil ? "スライドの設定" : "キーの設定"
        let time = t.style == .cut ? "" : " \(String(format: "%.2g", t.seconds)) 秒"
        return "おまかせ（\(from)：\(t.style.shortLabel)\(time)）"
    }

    private var customFadeOut: Binding<Bool> {
        Binding(get: { item.fadeOut != nil },
                set: { on in binding(\.fadeOut).wrappedValue = on ? (model.assignments[code]?.fadeOut ?? 1) : nil })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack {
                Image(systemName: item.symbol)
                // 名前は右クリックの「名前を変更…」だけで変える
                Text(item.title)
                    .font(.headline)
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                    .contextMenu { Button("名前を変更…") { model.renameDeckItem(code, itemID: item.id) } }
                    .help("右クリックで名前を変更")
            }
            HStack(spacing: 12) {
                Toggle("流す", isOn: binding(\.enabled))
                Button("ここから再生") { model.deckJump(code, itemID: item.id) }
                    .disabled(!item.enabled)
                Spacer()
                Text("元のスライド \(item.slide)").font(.caption).foregroundStyle(.secondary)
            }
            if item.kind == .image {
                HStack(spacing: 8) {
                    Picker("次へ", selection: advanceMode) {
                        Text("キーの設定に従う").tag(Advance.inherit)
                        Text("キーを押すまで待つ").tag(Advance.manual)
                        Text("秒数で自動").tag(Advance.seconds)
                    }
                    .frame(width: 230)
                    if advanceMode.wrappedValue == .seconds {
                        numberField(Binding(get: { item.advanceAfter ?? 5 }, set: { binding(\.advanceAfter).wrappedValue = max($0, 0.3) }))
                        Text("秒").foregroundStyle(.secondary)
                    }
                }
            } else {
                HStack(spacing: 8) {
                    Text("音量")
                    Slider(value: binding(\.volume), in: 0...2).frame(width: 120)
                    Text(item.volume <= 0.0001 ? "-∞ dB" : String(format: "%+.1f dB", 20 * log10(item.volume)))
                        .font(.caption.monospacedDigit())
                }
                Picker("終わったら", selection: binding(\.endAction)) {
                    ForEach(DeckItem.EndAction.allCases) { Text($0.label).tag($0) }
                }
                .frame(width: 260)
                if deck.clips[item.id] == nil && deck.probes[item.id]?.hasAudio == true {
                    Text("音が長すぎるため映像プレーヤーから出します（ルーティング無効）").font(.caption).foregroundStyle(.yellow)
                }
            }
            Divider()
            Text("この素材に入るとき（フェード）").font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 8) {
                Picker("", selection: transitionChoice) {
                    Text(inheritedLabel).tag("inherit")
                    Divider()
                    ForEach(TransitionStyle.allCases) { Text($0.label).tag($0.rawValue) }
                }
                .labelsHidden()
                .fixedSize()
                if let style = item.transitionStyle, style != .cut {
                    numberField(Binding(get: { item.transition ?? inherited.seconds },
                                        set: { binding(\.transition).wrappedValue = max($0, 0) }))
                    Text("秒").foregroundStyle(.secondary)
                }
            }
            .help("前の素材からこの素材へ切り替わるとき、またキーを押してこの素材から始まるときの効果（おまかせなら、始まるときはキーのフェード IN）。ディゾルブは重ねて入れ替え、黒・白を経由はいったんその色にしてから出します")
            HStack(spacing: 8) {
                Toggle("止めるときのフェードアウトを指定", isOn: customFadeOut)
                if let f = item.fadeOut {
                    numberField(Binding(get: { f }, set: { binding(\.fadeOut).wrappedValue = max($0, 0) }))
                    Text("秒").foregroundStyle(.secondary)
                }
            }
            .help("この素材が出ているときにキーを止めた（最後まで行った）ときのフェードアウト。指定しなければキーの OUT の秒数")
        }
        .font(.callout)
    }

    private func numberField(_ value: Binding<Double>) -> some View {
        NumberField(value: value)
    }
}
