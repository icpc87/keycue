import AppKit
import AVFoundation
import Carbon.HIToolbox
import Combine
import SwiftUI
import UniformTypeIdentifiers

enum KeyMode: String, Codable, CaseIterable, Identifiable {
    case toggle, retrigger, hold

    var id: String { rawValue }

    var label: String {
        switch self {
        case .toggle: return "トグル（もう一度押すとフェード停止）"
        case .retrigger: return "リトリガー（押すたび頭から）"
        case .hold: return "ホールド（押している間だけ）"
        }
    }
}

/// 別の素材が流れ始めたとき、このキーの再生をどうするか
enum NextBehavior: String, Codable, CaseIterable, Identifiable {
    case keep, fade, cut

    var id: String { rawValue }

    var label: String {
        switch self {
        case .keep: return "重ねて流し続ける"
        case .fade: return "フェードアウトして止める"
        case .cut: return "すぐ止める"
        }
    }
}

struct KeyAssignment: Codable, Equatable {
    var path: String
    var relativePath: String?
    var title = ""
    var mode = KeyMode.toggle
    var fadeIn = 0.0
    var fadeOut = 1.0
    var volume = 1.0
    var loop = false
    /// 旧形式・まとめ入力用。matrix があればそちらを使う
    var outputMap = ""
    /// 元チャンネルごとの出力先（0 始まり）。nil なら自動
    var matrix: [[Int]]?
    var colorIndex = 0
    var showVideo = true
    var onNext = NextBehavior.keep
    /// 0 = どのキーが流れても反応、1...8 = 同じグループのキーにだけ反応
    var group = 0
    /// 映像の重なり順（大きいほど前面）。同じなら後から流したものが上
    var layer = 5
    var slideAuto = false
    var slideInterval = 5.0
    var slideLoop = false
    var slideTransition = 0.3
    /// 素材を切り替えるときの効果（素材ごとに変えなければこれ）
    var slideTransitionStyle = TransitionStyle.dissolve
    /// スライド素材の並び・素材ごとの設定。nil なら変換したままの順
    var deckItems: [DeckItem]?
    /// 1 つのキーに複数の素材を順に入れたとき、そのファイル（入れた順）。nil なら 1 ファイルだけ
    var sources: [String]?
    var relativeSources: [String]?
    /// 素材一覧タブで、素材をクリックしただけで切り替える
    var clickToTake = false
    /// 映像を出す出力（nil = 選んでいるすべての出力）
    var screens: [String]?
    /// ファイルではなく、カメラ・キャプチャ機器・オーディオ入力
    var liveInput: LiveInput?

    var isLive: Bool { liveInput != nil }

    var screenSet: Set<String>? { screens.map(Set.init) }

    init(path: String, colorIndex: Int) {
        self.path = path
        self.colorIndex = colorIndex
    }

    var url: URL { URL(fileURLWithPath: path) }

    var displayName: String {
        guard title.isEmpty else { return title }
        if let live = liveInput { return live.name }
        let stem = url.deletingPathExtension().lastPathComponent
        if let s = sources, s.count > 1 { return "\(stem) ほか \(s.count - 1)" }
        return stem
    }

    var isImage: Bool { !isLive && UTType(filenameExtension: url.pathExtension)?.conforms(to: .image) == true }

    /// 素材リストとして流すファイル（複数入れたキー、または画像 1 枚）
    var listSources: [String]? {
        if let s = sources, !s.isEmpty { return s }
        return isImage ? [path] : nil
    }

    var isList: Bool { listSources != nil }

    /// 押すたび次の素材へ進むキー（素材リスト、または pptx / key / pdf）
    var isDeck: Bool { !isLive && (isList || SlideConverter.isDeck(url)) }

    /// 読み込んだ中身を共有するための鍵（同じファイル・同じ並びなら同じ）
    var mediaKey: String {
        if let l = liveInput { return "live:\(l.videoID ?? "")|\(l.audioID ?? "")" }
        if let s = sources, !s.isEmpty { return "list:" + s.joined(separator: "\n") }
        return path
    }

    private enum CodingKeys: String, CodingKey {
        case path, relativePath, title, mode, fadeIn, fadeOut, volume, loop, outputMap, matrix, colorIndex, showVideo
        case onNext, group, layer, slideAuto, slideInterval, slideLoop, slideTransition, slideTransitionStyle, deckItems, sources, relativeSources
        case clickToTake, screens, liveInput
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        path = try c.decode(String.self, forKey: .path)
        relativePath = try c.decodeIfPresent(String.self, forKey: .relativePath)
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? ""
        mode = try c.decodeIfPresent(KeyMode.self, forKey: .mode) ?? .toggle
        fadeIn = try c.decodeIfPresent(Double.self, forKey: .fadeIn) ?? 0
        fadeOut = try c.decodeIfPresent(Double.self, forKey: .fadeOut) ?? 1
        volume = try c.decodeIfPresent(Double.self, forKey: .volume) ?? 1
        loop = try c.decodeIfPresent(Bool.self, forKey: .loop) ?? false
        outputMap = try c.decodeIfPresent(String.self, forKey: .outputMap) ?? ""
        matrix = try c.decodeIfPresent([[Int]].self, forKey: .matrix)
        colorIndex = try c.decodeIfPresent(Int.self, forKey: .colorIndex) ?? 0
        showVideo = try c.decodeIfPresent(Bool.self, forKey: .showVideo) ?? true
        onNext = try c.decodeIfPresent(NextBehavior.self, forKey: .onNext) ?? .keep
        group = try c.decodeIfPresent(Int.self, forKey: .group) ?? 0
        layer = try c.decodeIfPresent(Int.self, forKey: .layer) ?? 5
        slideAuto = try c.decodeIfPresent(Bool.self, forKey: .slideAuto) ?? false
        slideInterval = try c.decodeIfPresent(Double.self, forKey: .slideInterval) ?? 5
        slideLoop = try c.decodeIfPresent(Bool.self, forKey: .slideLoop) ?? false
        slideTransition = try c.decodeIfPresent(Double.self, forKey: .slideTransition) ?? 0.3
        slideTransitionStyle = try c.decodeIfPresent(TransitionStyle.self, forKey: .slideTransitionStyle) ?? .dissolve
        deckItems = try c.decodeIfPresent([DeckItem].self, forKey: .deckItems)
        sources = try c.decodeIfPresent([String].self, forKey: .sources)
        relativeSources = try c.decodeIfPresent([String].self, forKey: .relativeSources)
        clickToTake = try c.decodeIfPresent(Bool.self, forKey: .clickToTake) ?? false
        screens = try c.decodeIfPresent([String].self, forKey: .screens)
        liveInput = try c.decodeIfPresent(LiveInput.self, forKey: .liveInput)
    }
}

struct KeymapDocument: Codable {
    var format = "keycue"
    var version = 1
    var keys: [String: KeyAssignment] = [:]
    var outputDeviceUID: String?
    var masterVolume = 1.0
    var videoTarget = "window"
    /// 映像を出す出力（複数）。古いキーマップでは videoTarget だけ
    var videoOutputs: [String]?
    var stopAllFade = 1.0
    var mutedOutputs: [Int]?
    /// プレイリスト（キーボードのページ）。古いキーマップは keys だけの 1 ページ
    var pages: [PageDocument]?
    var currentPage: Int?
    /// メイン画面の表示（keyboard / thumbnails / layers）とレイヤー表示の上のモニター。古いキーマップにはない
    var mainView: String?
    var layerMonitors: String?
}

struct MediaInfo {
    var loading = true
    var progressText = "読み込み中…"
    var probe = MediaProbe()
    var clip: AudioClip?
    var deck: Deck?
    var audioViaPlayer = false
    var error: String?
    /// 再生に使うファイル（この Mac で再生できない形式を変換したもの）。nil なら元のファイル
    var playURL: URL?

    var hasVideo: Bool { probe.hasVideo }
    var isVisual: Bool { probe.hasVideo || deck != nil }
    /// ルーティング表に出す元チャンネル数
    var sourceChannels: Int { deck?.sourceChannels ?? clip?.channelCount ?? max(probe.sourceChannels, 1) }
}

final class ActiveCue {
    let id: Int
    let key: UInt16
    let name: String
    /// 鳴らしたときのページ。ページを切り替えても鳴り続け、キー操作はそのページのものだけに効く
    let page: UUID
    /// 鳴らしたときの割り当て（いまのページで編集されたら更新する）
    var assignment: KeyAssignment
    var voiceID: Int?
    var video: VideoCue?
    var deck: DeckCue?
    /// ライブ入力の映像と音
    var liveVideo: LiveVideoCue?
    var audioCapture: AudioCapture?
    var stopping = false

    init(id: Int, key: UInt16, page: UUID, assignment: KeyAssignment) {
        self.id = id
        self.key = key
        self.page = page
        self.assignment = assignment
        self.name = assignment.displayName
    }
}

/// キーボード 1 枚分の割り当て（プレイリストの 1 項目）
struct KeyPage: Identifiable {
    var id = UUID()
    var name: String
    var keys: [UInt16: KeyAssignment]
}

struct PageDocument: Codable {
    var name: String
    var keys: [String: KeyAssignment]
}

/// スライド素材を順に流している状態
final class DeckCue {
    let deck: Deck
    var itemID: String?
    var index = 0
    var image: ImageCue?
    var video: VideoCue?
    var voiceID: Int?
    var itemStarted = Date()
    /// 切り替えのたびに増やす（色を経由する切り替えの途中で、次の操作が来たら古い方をやめる）
    var generation = 0

    init(deck: Deck) { self.deck = deck }
}

struct CueRow: Identifiable, Equatable {
    let id: Int
    let key: UInt16
    let name: String
    /// スライド素材の何枚目か（0...1）。普通の素材の進み具合は LiveValues で描く
    let progress: Double
    let stopping: Bool
    let hasVideo: Bool
    let slideText: String?
    /// スライド素材でいま流している素材の名前
    var itemName: String? = nil
    /// シークできる（位置は LiveValues）
    var seekable = false
    /// 素材の ch 数（小さなメーター用）
    var channels = 0
    /// ほかのページで鳴らした素材なら、そのページ名
    var pageName: String? = nil
}

/// 再生中に変わるキーの表示（進み具合など）。メーターや再生中リストとは分けて、
/// それぞれ見ている画面だけが描き直されるようにする（M1 などで軽く動かすため）
final class LiveState: ObservableObject {
    /// いまのページで鳴っているキー（進み具合そのものは LiveValues）
    @Published var playing: Set<UInt16> = []
    @Published var slidePosition: [UInt16: String] = [:]
    /// スライド素材のキー → いま出している素材の ID
    @Published var deckCurrent: [UInt16: String] = [:]
}

/// 再生中リスト
final class CueState: ObservableObject {
    @Published var cues: [CueRow] = []
}

/// 素材が鳴っているページ（プレイリストの表示用。めったに変わらない）
final class PageLiveState: ObservableObject {
    @Published var playingPages: Set<UUID> = []
}

enum Routing {
    /// "1,2" "3-4" "5" を 0 始まりの出力チャンネル列へ
    static func parse(_ spec: String) -> [Int] {
        var out: [Int] = []
        for token in spec.split(whereSeparator: { $0 == "," || $0 == " " || $0 == "、" }) {
            let parts = token.split(separator: "-").compactMap { Int($0.trimmingCharacters(in: .whitespaces)) }
            if parts.count == 2, parts[0] <= parts[1] {
                out.append(contentsOf: (parts[0]...parts[1]).map { $0 - 1 })
            } else if parts.count == 1 {
                out.append(parts[0] - 1)
            }
        }
        return out.filter { $0 >= 0 && $0 < Mixer.maxChannels }
    }

    /// メーター・ルーティング表に出す ch 数（デバイスが 8ch 未満でも 8）
    static func visibleChannels(_ device: Int) -> Int { max(device, Mixer.minVisibleChannels) }

    /// (元チャンネル, 出力チャンネル) の組。out はデバイスの ch 数
    static func routes(spec: String, sourceChannels src: Int, outputChannels out: Int) -> [(Int, Int)] {
        let list = parse(spec)
        let outs = max(out, 1)
        let limit = visibleChannels(outs)
        var r: [(Int, Int)] = []
        if list.isEmpty {
            if src == 1 {
                r = outs >= 2 ? [(0, 0), (0, 1)] : [(0, 0)]
            } else {
                r = (0..<src).map { ($0, $0 % outs) }
            }
        } else if src == 1 {
            r = list.map { (0, $0) }
        } else if list.count == 1 {
            r = (0..<src).map { ($0, list[0] + $0) }
        } else {
            r = zip(0..<src, list).map { ($0, $1) }
        }
        // 自動はデバイスの ch に折り返す。明示した出し先は 8ch まで残す（デバイスにない ch はメーターだけ）
        return r.filter { $0.1 < limit }
    }

    static func routes(for a: KeyAssignment, sourceChannels src: Int, outputChannels out: Int) -> [(Int, Int)] {
        guard let m = a.matrix else { return routes(spec: a.outputMap, sourceChannels: src, outputChannels: out) }
        var r: [(Int, Int)] = []
        for (s, dsts) in m.enumerated() where s < src {
            for d in dsts where d < visibleChannels(out) { r.append((s, d)) }
        }
        return r
    }

    /// 表の編集用：いまの割り当てを元チャンネルごとの出力先に直す
    static func matrix(for a: KeyAssignment, sourceChannels src: Int, outputChannels out: Int) -> [[Int]] {
        var m = [[Int]](repeating: [], count: src)
        for (s, d) in routes(for: a, sourceChannels: src, outputChannels: out) { m[s].append(d) }
        return m
    }

    static func describe(_ a: KeyAssignment, sourceChannels src: Int, outputChannels out: Int) -> String {
        let r = routes(for: a, sourceChannels: src, outputChannels: out)
        if r.isEmpty { return "出力先なし" }
        let text = r.map { "\($0.0 + 1)→\($0.1 + 1)" }.joined(separator: " ")
        return r.contains { $0.1 >= out } ? text + "（\(out + 1)ch 以降はデバイスになくメーターのみ）" : text
    }
}

final class AppModel: ObservableObject {
    /// いま表示しているページのキーの割り当て
    @Published var assignments: [UInt16: KeyAssignment] = [:]
    /// プレイリスト（キーボードのページの並び）
    @Published var pages: [KeyPage] = [KeyPage(name: "ページ 1", keys: [:])]
    @Published private(set) var currentPage = 0
    /// はじめの画面（新しく作る・開く）を出しているか
    @Published var showStartScreen = false
    /// 「キーマップを開く…」のパネルを出す（AppDelegate が受け持つ）
    var onOpenPanel: (() -> Void)?

    @Published var showPages = UserDefaults.standard.object(forKey: "showPages") as? Bool ?? true {
        didSet { UserDefaults.standard.set(showPages, forKey: "showPages") }
    }

    var currentPageID: UUID { pages[currentPage].id }
    var currentPageName: String { pages[currentPage].name }
    @Published var selectedKey: UInt16? = UInt16(kVK_ANSI_Q)
    @Published private(set) var media: [String: MediaInfo] = [:]
    @Published private(set) var devices: [AudioOutputDevice] = []
    @Published var outputDeviceUID: String? {
        didSet { if oldValue != outputDeviceUID && !applyingDocument { restartAudio(); markDirty() } }
    }
    @Published var masterVolume = 1.0 {
        didSet { audio.mixer.setMasterVolume(Float(masterVolume)); if !applyingDocument { markDirty() } }
    }
    @Published var stopAllFade = 1.0 { didSet { if !applyingDocument { markDirty() } } }
    @Published var backgroundInput = false { didSet { onBackgroundInputChange?(backgroundInput) } }
    /// 出力チャンネルのミュート（0 始まり・全キー共通）
    @Published var mutedOutputs: Set<Int> = [] {
        didSet { audio.mixer.setMuted(mutedOutputs); if !applyingDocument { markDirty() } }
    }
    @Published var testToneChannel: Int? {
        didSet { audio.mixer.setTestTone(channel: testToneChannel) }
    }
    /// 横に開いているスライド素材のタブ
    @Published var deckTabs: [UInt16] = []
    @Published var activeDeckTab: UInt16?
    @Published private(set) var engineInfo = ""
    @Published var statusMessage = ""
    @Published private(set) var documentURL: URL?
    @Published private(set) var isDirty = false

    let instanceNumber: Int
    let audio = AudioEngine()
    let video = VideoOutput()
    let live = LiveState()
    /// 再生中に毎回変わる値（SwiftUI を通さず描く）
    let values = LiveValues()
    let cueState = CueState()
    let pageLive = PageLiveState()
    /// 録画・配信・仮想カメラ
    lazy var feed = ProgramFeed(model: self)
    lazy var virtualCamera = VirtualCamera(feed: feed)

    var onTitleChange: (() -> Void)?
    var onBackgroundInputChange: ((Bool) -> Void)?

    private var cues: [ActiveCue] = []
    private var nextCueID = 1
    private var visualSequence: CGFloat = 0
    private var timer: Timer?
    private var applyingDocument = false
    private var displayedMeters: [Float] = []
    private var displayedSourceMeters: [UInt16: [Float]] = [:]
    private var videoTargetSink: AnyCancellable?

    init(instance: Int) {
        instanceNumber = instance
        video.instanceNumber = instance
    }

    func start() {
        devices = AudioDeviceList.outputDevices()
        AudioDeviceList.observeChanges { [weak self] in self?.devices = AudioDeviceList.outputDevices() }
        audio.onConfigurationChange = { [weak self] in
            self?.devices = AudioDeviceList.outputDevices()
            self?.restartAudio()
        }
        restartAudio()
        videoTargetSink = video.$outputs.dropFirst().sink { [weak self] _ in
            guard let self, !self.applyingDocument else { return }
            self.markDirty()
        }
        let t = Timer(timeInterval: 1.0 / 20, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func restartAudio() {
        do {
            try audio.start(deviceUID: outputDeviceUID)
        } catch {
            statusMessage = "オーディオ出力を開始できません: \(error.localizedDescription)"
            try? audio.start(deviceUID: nil)
        }
        audio.mixer.setMasterVolume(Float(masterVolume))
        audio.mixer.setMuted(mutedOutputs)
        audio.mixer.setTestTone(channel: testToneChannel)
        engineInfo = "\(audio.deviceName) · \(audio.channelCount)ch · \(Int(audio.sampleRate))Hz"
        video.audioDeviceUID = audio.deviceUID
        video.audioClock = audio.makeDeviceClock()
        video.resetPrepared()
    }

    /// 出力デバイスの実際の ch 数
    var outputChannels: Int { audio.channelCount }
    /// メーター・ルーティング表に出す ch 数
    var visibleOutputChannels: Int { Routing.visibleChannels(audio.channelCount) }

    var outputDeviceName: String { audio.deviceName }

    // MARK: - 割り当て

    func info(for code: UInt16) -> MediaInfo? {
        guard let a = assignments[code] else { return nil }
        return media[a.mediaKey]
    }

    func mediaInfo(_ a: KeyAssignment) -> MediaInfo? { media[a.mediaKey] }

    func assign(_ url: URL, to code: UInt16) {
        let existingColor = assignments[code]?.colorIndex
        let color = existingColor ?? (assignments.count % KeyPalette.colors.count)
        if let old = assignments[code] { stopKey(code, fade: 0.05); releaseMediaIfUnused(old.mediaKey, excluding: code) }
        let a = AppModel.newAssignment(path: url.path, colorIndex: color)
        assignments[code] = a
        loadMedia(for: a)
        markDirty()
    }

    // MARK: - プレイリスト（キーボードのページを並べて、クリックや ⌘→ で丸ごと切り替える）

    /// 全ページの割り当て（いまのページは編集中の assignments を使う）
    var allAssignments: [KeyAssignment] {
        var out = Array(assignments.values)
        for (i, p) in pages.enumerated() where i != currentPage { out += p.keys.values }
        return out
    }

    /// ページを切り替える。鳴っている素材は止めずにそのまま流す
    func switchPage(to index: Int) {
        guard pages.indices.contains(index), index != currentPage else { return }
        pages[currentPage].keys = assignments
        currentPage = index
        assignments = pages[index].keys
        deckTabs = []
        layerPreview = [:]
        activeDeckTab = nil
        reloadAll(force: false)
        statusMessage = "ページ \(index + 1): \(pages[index].name)"
        onTitleChange?()
    }

    func stepPage(by delta: Int) {
        let next = currentPage + delta
        if pages.indices.contains(next) { switchPage(to: next) }
    }

    func newPage(duplicate: Bool = false) {
        pages[currentPage].keys = assignments
        var page = KeyPage(name: "ページ \(pages.count + 1)", keys: [:])
        if duplicate {
            page.name = pages[currentPage].name + " のコピー"
            page.keys = assignments
        }
        pages.insert(page, at: currentPage + 1)
        markDirty()
        switchPage(to: currentPage + 1)
    }

    func renamePage(_ index: Int) {
        guard pages.indices.contains(index),
              let name = askName("ページの名前を変更", current: pages[index].name, original: "ページ \(index + 1)") else { return }
        pages[index].name = name.isEmpty ? "ページ \(index + 1)" : name
        markDirty()
    }

    func deletePage(_ index: Int) {
        guard pages.count > 1, pages.indices.contains(index) else { return }
        let alert = NSAlert()
        alert.messageText = "「\(pages[index].name)」を削除しますか？"
        alert.informativeText = "このページのキーの割り当てが消えます（素材のファイルは消えません）。"
        alert.addButton(withTitle: "削除")
        alert.addButton(withTitle: "キャンセル")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        pages[currentPage].keys = assignments
        let wasCurrent = index == currentPage
        pages.remove(at: index)
        if wasCurrent {
            currentPage = min(index, pages.count - 1)
            assignments = pages[currentPage].keys
            deckTabs = []
            activeDeckTab = nil
            reloadAll(force: false)
        } else if index < currentPage {
            currentPage -= 1
        }
        markDirty()
    }

    func movePages(from: IndexSet, to: Int) {
        pages[currentPage].keys = assignments
        let currentID = pages[currentPage].id
        pages.move(fromOffsets: from, toOffset: to)
        currentPage = pages.firstIndex { $0.id == currentID } ?? 0
        markDirty()
    }

    /// キーの素材をほかのページの同じキーへコピーする
    func copyKey(_ code: UInt16, toPage index: Int) {
        guard let a = assignments[code], pages.indices.contains(index), index != currentPage else { return }
        if pages[index].keys[code] != nil {
            let alert = NSAlert()
            alert.messageText = "「\(pages[index].name)」の \(KeyboardLayout.label(for: code)) キーにはすでに素材があります"
            alert.informativeText = "上書きしますか？"
            alert.addButton(withTitle: "上書き")
            alert.addButton(withTitle: "キャンセル")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        pages[index].keys[code] = a
        markDirty()
        statusMessage = "\(KeyboardLayout.label(for: code)) キーを「\(pages[index].name)」にコピーしました"
    }

    // MARK: - ライブ入力（カメラ・キャプチャ機器・オーディオ入力）

    static func liveInfo(_ live: LiveInput) -> MediaInfo {
        var info = MediaInfo()
        info.loading = false
        var missing: [String] = []
        if let id = live.videoID {
            if let d = CaptureDevices.device(id) {
                info.probe.hasVideo = true
                let dim = CMVideoFormatDescriptionGetDimensions(d.activeFormat.formatDescription)
                info.probe.videoSize = CGSize(width: Int(dim.width), height: Int(dim.height))
            } else {
                missing.append(live.videoName ?? "映像入力")
            }
        }
        if let id = live.audioID {
            if let d = CaptureDevices.device(id) {
                info.probe.hasAudio = true
                info.probe.sourceChannels = CaptureDevices.channels(d)
                info.probe.audioTracks = 1
            } else {
                missing.append(live.audioName ?? "音声入力")
            }
        }
        if !missing.isEmpty { info.error = "つながっていません: \(missing.joined(separator: "、"))" }
        return info
    }

    /// キーにライブ入力を入れる。すでに素材があれば確認する
    func assignLive(_ live: LiveInput, to code: UInt16) {
        if let a = assignments[code], a.liveInput != live {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "\(KeyboardLayout.label(for: code)) キーにはすでに素材があります"
            alert.informativeText = "いま「\(a.displayName)」が入っています。ライブ入力に差し替えますか？"
            alert.addButton(withTitle: "差し替える")
            alert.addButton(withTitle: "キャンセル")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        let color = assignments[code]?.colorIndex ?? (assignments.count % KeyPalette.colors.count)
        if let old = assignments[code] { stopKey(code, fade: 0.05); releaseMediaIfUnused(old.mediaKey, excluding: code) }
        var a = AppModel.newAssignment(path: "", colorIndex: color)
        a.liveInput = live
        assignments[code] = a
        loadMedia(for: a)
        selectedKey = code
        markDirty()
    }

    /// 「ライブ入力を割り当て…」：映像と音声の入力を選ぶ
    func chooseLiveInput(for code: UInt16) {
        let videos = CaptureDevices.video()
        let audios = CaptureDevices.audio()
        let current = assignments[code]?.liveInput

        let alert = NSAlert()
        alert.messageText = "\(KeyboardLayout.label(for: code)) キーにライブ入力を割り当て"
        alert.informativeText = "キーを押すと映像を出力に出し、音をルーティングどおりに流します。もう一度押すとフェードして止まります。"
        let videoPopup = NSPopUpButton(frame: NSRect(x: 60, y: 34, width: 340, height: 26))
        videoPopup.addItem(withTitle: "なし")
        for d in videos { videoPopup.addItem(withTitle: d.localizedName) }
        if let id = current?.videoID, let i = videos.firstIndex(where: { $0.uniqueID == id }) { videoPopup.selectItem(at: i + 1) }
        else if current == nil, !videos.isEmpty { videoPopup.selectItem(at: 1) }
        let audioPopup = NSPopUpButton(frame: NSRect(x: 60, y: 2, width: 340, height: 26))
        audioPopup.addItem(withTitle: "なし")
        for d in audios { audioPopup.addItem(withTitle: "\(d.localizedName)（\(CaptureDevices.channels(d))ch）") }
        if let id = current?.audioID, let i = audios.firstIndex(where: { $0.uniqueID == id }) { audioPopup.selectItem(at: i + 1) }
        let box = NSView(frame: NSRect(x: 0, y: 0, width: 400, height: 62))
        let v = NSTextField(labelWithString: "映像")
        v.frame = NSRect(x: 0, y: 38, width: 56, height: 18)
        let s = NSTextField(labelWithString: "音声")
        s.frame = NSRect(x: 0, y: 6, width: 56, height: 18)
        [v, s, videoPopup, audioPopup].forEach(box.addSubview)
        alert.accessoryView = box
        alert.addButton(withTitle: "割り当て")
        alert.addButton(withTitle: "キャンセル")
        if videos.isEmpty && audios.isEmpty {
            alert.informativeText = "カメラ・キャプチャ機器・音声入力が見つかりません。機器をつないでから、もう一度開いてください。"
        }
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        var live = LiveInput()
        if videoPopup.indexOfSelectedItem > 0 {
            let d = videos[videoPopup.indexOfSelectedItem - 1]
            live.videoID = d.uniqueID
            live.videoName = d.localizedName
        }
        if audioPopup.indexOfSelectedItem > 0 {
            let d = audios[audioPopup.indexOfSelectedItem - 1]
            live.audioID = d.uniqueID
            live.audioName = d.localizedName
        }
        guard live.videoID != nil || live.audioID != nil else { return }
        // 初めて使うときは macOS がカメラ・マイクの使用を確認する
        let assign = { [weak self] in self?.assignLive(live, to: code) }
        if live.videoID != nil {
            CaptureDevices.requestAccess(.video) { ok in
                if !ok { self.statusMessage = "カメラの使用が許可されていません（システム設定 › プライバシーとセキュリティ › カメラ）" }
                if live.audioID != nil {
                    CaptureDevices.requestAccess(.audio) { ok2 in
                        if !ok2 { self.statusMessage = "マイクの使用が許可されていません（システム設定 › プライバシーとセキュリティ › マイク）" }
                        assign()
                    }
                } else {
                    assign()
                }
            }
        } else {
            CaptureDevices.requestAccess(.audio) { ok in
                if !ok { self.statusMessage = "マイクの使用が許可されていません（システム設定 › プライバシーとセキュリティ › マイク）" }
                assign()
            }
        }
    }

    /// 新しく入れるキー。設定の「新しく入れるキーの初期値」を使う
    static func newAssignment(path: String, colorIndex: Int) -> KeyAssignment {
        var a = KeyAssignment(path: path, colorIndex: colorIndex)
        a.mode = Prefs.defaultMode
        a.fadeIn = Prefs.defaultFadeIn
        a.fadeOut = Prefs.defaultFadeOut
        a.onNext = Prefs.defaultOnNext
        return a
    }

    /// 複数ファイルを 1 つのキーに順番に入れる（押すたび次の素材へ）
    func assignList(_ urls: [URL], to code: UInt16) {
        guard let first = urls.first else { return }
        let color = assignments[code]?.colorIndex ?? (assignments.count % KeyPalette.colors.count)
        if let old = assignments[code] { stopKey(code, fade: 0.05); releaseMediaIfUnused(old.mediaKey, excluding: code) }
        var a = AppModel.newAssignment(path: first.path, colorIndex: color)
        a.sources = urls.map(\.path)
        assignments[code] = a
        loadMedia(for: a)
        selectedKey = code
        markDirty()
    }

    /// すでに素材があるキーの後ろに素材を足す。設定はそのまま残す
    func appendToKey(_ urls: [URL], code: UInt16) {
        guard var a = assignments[code], !urls.isEmpty else { return }
        let oldKey = a.mediaKey
        if a.sources == nil {
            // 1 ファイルのキーを素材リストにする。pptx などで並べ替えていた設定は引き継ぐ
            if SlideConverter.isDeck(a.url), let items = a.deckItems {
                a.deckItems = items.map { item in
                    var n = item
                    n.id = "d1:\(a.path)#\(item.id)"
                    return n
                }
            }
            a.sources = [a.path]
        }
        a.sources! += urls.map(\.path)
        if a.path.isEmpty { a.path = a.sources![0] }
        assignments[code] = a
        releaseMediaIfUnused(oldKey, excluding: code)
        loadMedia(for: a)
        markDirty()
        statusMessage = "\(KeyboardLayout.label(for: code)) キーに \(urls.count) 個追加しました（押すたび順に切り替わります）"
    }

    /// 素材リストから 1 つ外す（pptx などを展開した素材なら、その資料ごと外す）
    func removeFromList(_ code: UInt16, itemID: String) {
        guard var a = assignments[code], var sources = a.sources,
              let colon = itemID.firstIndex(of: ":"), let n = Int(itemID.dropFirst().prefix(upTo: colon)) else { return }
        var path = String(itemID[itemID.index(after: colon)...])
        if itemID.hasPrefix("d"), let hash = path.lastIndex(of: "#") { path = String(path[..<hash]) }
        var seen = 0
        guard let index = sources.firstIndex(where: { s in
            guard s == path else { return false }
            seen += 1
            return seen == n
        }) else { return }
        stopKey(code, fade: 0.05)
        sources.remove(at: index)
        if sources.isEmpty {
            clear(code)
            return
        }
        let oldKey = a.mediaKey
        a.sources = sources
        a.path = sources.first ?? ""
        // 同じファイルの 2 つ目以降は番号が 1 つ詰まるので、設定はいったん外した素材の分だけ消す
        a.deckItems = a.deckItems?.filter { !$0.id.hasPrefix(itemID.hasPrefix("d") ? "d\(n):\(path)#" : itemID) }
        assignments[code] = a
        releaseMediaIfUnused(oldKey, excluding: code)
        loadMedia(for: a)
        markDirty()
    }

    /// ドロップやファイル選択でキーに入れるとき。すでに素材があれば確認する
    func place(_ urls: [URL], on code: UInt16) {
        let files = urls.filter(AppModel.isSupported)
        guard !files.isEmpty else {
            statusMessage = "対応していないファイルです"
            return
        }
        let label = KeyboardLayout.label(for: code)
        if let a = assignments[code] {
            // 設定で「いつも追加／差し替え」にしていなければ、すでに素材があることを知らせて確認する
            var choice = Prefs.occupiedDrop
            if choice == "ask" {
                let alert = NSAlert()
                alert.alertStyle = .warning
                alert.messageText = "\(label) キーにはすでに素材があります"
                alert.informativeText = "いま「\(a.displayName)」が入っています。\n「後ろに追加」にすると、\(label) キーを押すたびに順番に切り替わります。"
                alert.addButton(withTitle: "後ろに追加")
                alert.addButton(withTitle: "差し替える")
                alert.addButton(withTitle: "キャンセル")
                switch alert.runModal() {
                case .alertFirstButtonReturn: choice = "append"
                case .alertSecondButtonReturn: choice = "replace"
                default: return
                }
            }
            if choice == "append" {
                appendToKey(files, code: code)
            } else if files.count == 1 {
                assign(files[0], to: code)
            } else {
                assignList(files, to: code)
            }
        } else if files.count > 1 {
            var choice = Prefs.multiDrop
            if choice == "ask" {
                let alert = NSAlert()
                alert.messageText = "\(files.count) 個のファイルをどう入れますか？"
                alert.informativeText = "「\(label) キーにまとめる」にすると、\(label) キーを押すたびに順番に切り替わります（Keynote・PowerPoint のように）。"
                alert.addButton(withTitle: "\(label) キーにまとめる")
                alert.addButton(withTitle: "後ろの空きキーに 1 つずつ")
                alert.addButton(withTitle: "キャンセル")
                switch alert.runModal() {
                case .alertFirstButtonReturn: choice = "list"
                case .alertSecondButtonReturn: choice = "spread"
                default: return
                }
            }
            if choice == "list" { assignList(files, to: code) } else { assign(files, startingAt: code) }
        } else {
            assign(files[0], to: code)
        }
        selectedKey = code
    }

    /// 複数ファイル：最初をそのキーに、残りは後ろの空きキーへ
    func assign(_ urls: [URL], startingAt code: UInt16) {
        let files = urls.filter(AppModel.isSupported)
        guard let first = files.first else {
            statusMessage = "対応していないファイルです"
            return
        }
        assign(first, to: code)
        var rest = files.dropFirst()
        if !rest.isEmpty, let start = KeyboardLayout.fillOrder.firstIndex(of: code) {
            for next in KeyboardLayout.fillOrder[(start + 1)...] where assignments[next] == nil {
                guard let url = rest.popFirst() else { break }
                assign(url, to: next)
            }
        }
        if !rest.isEmpty { statusMessage = "空きキーが足りず \(rest.count) 個のファイルを割り当てられませんでした" }
        selectedKey = code
    }

    func clear(_ code: UInt16) {
        guard let old = assignments[code] else { return }
        stopKey(code, fade: 0.05)
        assignments[code] = nil
        closeDeckTab(code)
        releaseMediaIfUnused(old.mediaKey, excluding: code)
        markDirty()
    }

    func update(_ code: UInt16, _ a: KeyAssignment) {
        guard let old = assignments[code], old != a else { return }
        assignments[code] = a
        for cue in cues where cue.key == code && cue.page == currentPageID {
            cue.assignment = a
            guard !cue.stopping else { continue }
            if let id = cue.voiceID {
                audio.mixer.setVolume(id: id, volume: Float(a.volume))
                audio.mixer.setLoop(id: id, loop: a.loop)
            }
            if let v = cue.video { video.setVolume(v, volume: Float(a.volume)) }
        }
        markDirty()
    }

    // MARK: - 名前の変更（右クリックから。入力欄を画面に置かないため）

    /// 名前を入力するダイアログ。キャンセルなら nil、「元に戻す」なら ""
    private func askName(_ title: String, current: String, original: String) -> String? {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = "元の名前: \(original)"
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        field.stringValue = current
        field.placeholderString = original
        alert.accessoryView = field
        alert.addButton(withTitle: "変更")
        alert.addButton(withTitle: "キャンセル")
        alert.addButton(withTitle: "元の名前に戻す")
        alert.window.initialFirstResponder = field
        switch alert.runModal() {
        case .alertFirstButtonReturn: return field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        case .alertThirdButtonReturn: return ""
        default: return nil
        }
    }

    func renameKey(_ code: UInt16) {
        guard var a = assignments[code] else { return }
        let original = a.url.deletingPathExtension().lastPathComponent
        guard let name = askName("\(KeyboardLayout.label(for: code)) キーの名前を変更", current: a.displayName, original: original) else { return }
        a.title = name == original ? "" : name
        update(code, a)
    }

    func renameDeckItem(_ code: UInt16, itemID: String) {
        guard var item = deckItems(code).first(where: { $0.id == itemID }),
              let fresh = info(for: code)?.deck?.items.first(where: { $0.id == itemID }) else { return }
        guard let name = askName("素材の名前を変更", current: item.title, original: fresh.title) else { return }
        item.title = name.isEmpty ? fresh.title : name
        updateDeckItem(code, item)
    }

    /// このキーのルーティングを他の割り当て済みキーにも使う
    func applyRoutingToAll(from code: UInt16) {
        guard let src = assignments[code] else { return }
        for (k, var a) in assignments where k != code && !a.isDeck {
            a.matrix = src.matrix
            a.outputMap = src.outputMap
            assignments[k] = a
        }
        markDirty()
        statusMessage = "\(KeyboardLayout.label(for: code)) のルーティングを全キーに適用しました"
    }

    private static var openPanelTypes: [UTType] {
        [.audio, .movie, .audiovisualContent, .image, .pdf, .presentation]
            + ["pptx", "ppt", "key"].compactMap { UTType(filenameExtension: $0) }
    }

    /// 「ファイルを割り当て…」。すでに素材があれば、追加か差し替えかを確認する
    func chooseFile(for code: UInt16) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = AppModel.openPanelTypes
        panel.allowsMultipleSelection = true
        panel.message = "\(KeyboardLayout.label(for: code)) キーに入れるファイル（複数選べます）"
        if panel.runModal() == .OK { place(panel.urls, on: code) }
    }

    /// 「差し替え…」。確認なしでこのキーの中身を選んだファイルにする
    func replaceFile(for code: UInt16) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = AppModel.openPanelTypes
        panel.allowsMultipleSelection = true
        panel.message = "\(KeyboardLayout.label(for: code)) キーの素材を差し替え（複数選ぶと順番に切り替わる 1 つのキーになります）"
        guard panel.runModal() == .OK else { return }
        let files = panel.urls.filter(AppModel.isSupported)
        if files.count == 1 { assign(files[0], to: code) } else if !files.isEmpty { assignList(files, to: code) }
    }

    /// 一覧タブの「素材を追加…」。素材リストの最後に足す
    func addFiles(to code: UInt16) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = AppModel.openPanelTypes
        panel.allowsMultipleSelection = true
        panel.message = "\(KeyboardLayout.label(for: code)) キーの最後に追加するファイル"
        guard panel.runModal() == .OK else { return }
        let files = panel.urls.filter(AppModel.isSupported)
        if !files.isEmpty { appendToKey(files, code: code) }
    }

    static func isSupported(_ url: URL) -> Bool {
        if SlideConverter.isDeck(url) { return true }
        guard let type = UTType(filenameExtension: url.pathExtension) else { return false }
        return type.conforms(to: .audio) || type.conforms(to: .movie) || type.conforms(to: .audiovisualContent)
            || type.conforms(to: .image)
    }

    private func releaseMediaIfUnused(_ key: String, excluding code: UInt16) {
        // ほかのキー・ほかのページ・鳴っている素材で使っていなければ手放す
        let used = assignments.contains { $0.key != code && $0.value.mediaKey == key }
            || pages.enumerated().contains { i, p in i != currentPage && p.keys.values.contains { $0.mediaKey == key } }
            || cues.contains { !$0.stopping && $0.assignment.mediaKey == key }
        if !used {
            media[key] = nil
            if !key.hasPrefix("list:") { video.forget(URL(fileURLWithPath: key)) }
        }
    }

    func loadMedia(for a: KeyAssignment, force: Bool = false) {
        if let live = a.liveInput {
            media[a.mediaKey] = AppModel.liveInfo(live)
            return
        }
        loadMedia(key: a.mediaKey, path: a.path, sources: a.listSources, force: force)
    }

    /// セキュリティソフトや macOS にファイルの読み書きを止められたか
    static func isPermissionError(_ error: Error) -> Bool {
        let ns = error as NSError
        if ns.domain == NSCocoaErrorDomain && [CocoaError.fileWriteNoPermission.rawValue, CocoaError.fileReadNoPermission.rawValue].contains(ns.code) { return true }
        if ns.domain == NSPOSIXErrorDomain && (ns.code == Int(EPERM) || ns.code == Int(EACCES)) { return true }
        if let underlying = ns.userInfo[NSUnderlyingErrorKey] as? Error { return isPermissionError(underlying) }
        return false
    }

    func reload(_ code: UInt16) {
        if let a = assignments[code] { loadMedia(for: a, force: true) }
    }

    private func loadMedia(key: String, path: String, sources: [String]?, force: Bool) {
        if !force, let m = media[key], m.error == nil { return }
        let url = URL(fileURLWithPath: path)
        let isDeck = sources != nil || SlideConverter.isDeck(url)
        var placeholder = MediaInfo()
        if sources != nil { placeholder.progressText = "素材を読み込み中…" }
        else if isDeck { placeholder.progressText = "スライドを展開中…（初回のみ）" }
        media[key] = placeholder
        let rate = audio.sampleRate
        Task.detached(priority: .userInitiated) { [weak self] in
            var info = MediaInfo()
            info.loading = false
            do {
                if sources == nil {
                    guard FileManager.default.fileExists(atPath: path) else { throw AudioLoader.LoadError.failed("ファイルが見つかりません") }
                }
                if isDeck {
                    let progress: (String) -> Void = { message in
                        DispatchQueue.main.async { self?.media[key]?.progressText = message }
                    }
                    let deck: Deck
                    if let sources {
                        deck = try await DeckBuilder.buildList(sources, force: force, progress: progress)
                    } else {
                        deck = try await DeckBuilder.build(url, force: force, progress: progress)
                    }
                    // 動画・音声素材の音はキーのルーティングで鳴らすので先に展開しておく
                    for item in deck.items where item.kind != .image {
                        let itemURL = deck.url(item)
                        guard let probe = try? await AudioLoader.probe(url: itemURL) else { continue }
                        deck.probes[item.id] = probe
                        if probe.hasAudio {
                            deck.clips[item.id] = try? await AudioLoader.loadClip(url: itemURL, targetRate: rate, probe: probe)
                        }
                    }
                    info.deck = deck
                    let attrs = try? FileManager.default.attributesOfItem(atPath: path)
                    info.probe.fileSize = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
                } else {
                    // アニメーション形式など、そのままでは再生できない動画は変換してから使う（初回のみ）
                    let playURL = try await VideoConverter.playable(url) { p in
                        DispatchQueue.main.async { self?.media[key]?.progressText = "再生できる形式に変換中…（初回のみ）\(Int(p * 100))%" }
                    }
                    if playURL != url { info.playURL = playURL }
                    info.probe = try await AudioLoader.probe(url: playURL)
                    if !info.probe.hasAudio && !info.probe.hasVideo { throw AudioLoader.LoadError.failed("音声も映像も含まれていません") }
                    if info.probe.hasAudio {
                        do {
                            info.clip = try await AudioLoader.loadClip(url: playURL, targetRate: rate, probe: info.probe)
                        } catch AudioLoader.LoadError.tooLarge where info.probe.hasVideo {
                            info.audioViaPlayer = true
                        }
                    }
                }
            } catch {
                info.error = AppModel.isPermissionError(error)
                    ? "書き込みが止められました。セキュリティソフト（F-Secure の DeepGuard など）で KeyCue を許可してください（\(error.localizedDescription)）"
                    : error.localizedDescription
            }
            let result = info
            await MainActor.run { [weak self] in
                guard let self, self.allAssignments.contains(where: { $0.mediaKey == key }) else { return }
                self.media[key] = result
                if result.hasVideo { self.video.prepare(result.playURL ?? url) }
                if let missing = result.deck?.missing, !missing.isEmpty {
                    self.statusMessage = "見つからないファイルがあります: \(missing.joined(separator: ", "))"
                }
            }
        }
    }

    /// 全ページの素材を読み込む（ページを切り替えたらすぐ鳴らせるように、ほかのページの分も先に）
    func reloadAll(force: Bool = true) {
        var done = Set<String>()
        for a in assignments.values where done.insert(a.mediaKey).inserted { loadMedia(for: a, force: force) }
        for a in allAssignments where done.insert(a.mediaKey).inserted { loadMedia(for: a, force: force) }
    }

    // MARK: - キー操作

    func handleKey(code: UInt16, down: Bool, shift: Bool, isRepeat: Bool) {
        if code == UInt16(kVK_Escape) {
            if down && !isRepeat { stopAll(immediate: shift) }
            return
        }
        if isRepeat { return }
        guard let a = assignments[code] else {
            if down, KeyboardLayout.labels[code] != nil { selectedKey = code }
            return
        }
        if !down {
            if a.mode == .hold && !a.isDeck { stopKey(code, fade: a.fadeOut) }
            return
        }
        selectedKey = code
        if shift {
            stopKey(code, fade: 0)
            return
        }
        if a.isDeck {
            // スライド素材：押すたび次の素材へ
            if let cue = runningCue(code), cue.deck != nil { deckStep(cue, by: 1) } else { startKey(code) }
            return
        }
        switch a.mode {
        case .toggle:
            if isRunning(code) { stopKey(code, fade: a.fadeOut) } else { startKey(code) }
        case .retrigger:
            stopKey(code, fade: min(a.fadeOut, 0.03))
            startKey(code)
        case .hold:
            if !isRunning(code) { startKey(code) }
        }
    }

    /// 映像を出す素材か（動画・画像・スライド・カメラ）。レイヤー表示の段分けと入れ替えに使う
    func isVisual(_ a: KeyAssignment) -> Bool {
        if a.isLive { return a.liveInput?.videoID != nil && a.showVideo }
        guard let info = media[a.mediaKey] else { return a.isDeck }
        if let deck = info.deck { return deck.items.contains { $0.kind != .audio } }
        return info.hasVideo && a.showVideo
    }

    /// レイヤー表示でクリック：設定がオンなら、同じレイヤーで流れている映像を新しい素材のフェード IN で消して入れ替え、
    /// それからキーを押したのと同じ動きをする
    func layerTake(_ code: UInt16, shift: Bool) {
        guard let a = assignments[code] else { return }
        if Prefs.layerReplace, !shift, !isRunning(code), isVisual(a) {
            for cue in cues where !cue.stopping && cue.key != code && cue.assignment.layer == a.layer && isVisual(cue.assignment) {
                stop(cue, fade: max(a.fadeIn, 0.03))
            }
        }
        handleKey(code: code, down: true, shift: shift, isRepeat: false)
    }

    // MARK: - レイヤー表示のプレビューとプログラム（Wirecast のように、プレビューで組んでから「ライブへ」で出す）

    /// レイヤーごとに次に出すもの：キー、または nil（そのレイヤーを消す）。入っていないレイヤーはいまのまま
    @Published private(set) var layerPreview: [Int: UInt16?] = [:]

    /// いまそのレイヤーで流れている（ライブの）映像のキー
    func programKeys(layer: Int) -> [UInt16] {
        cues.filter { !$0.stopping && $0.page == currentPageID && $0.assignment.layer == layer && isVisual($0.assignment) }.map(\.key)
    }

    /// プレビューに組まれているもの（変えていないレイヤーはライブと同じ）
    func previewKey(layer: Int) -> UInt16? {
        if let entry = layerPreview[layer] { return entry }
        return programKeys(layer: layer).last
    }

    /// カードをクリック：そのキーのレイヤーのプレビューにする（もう一度クリックすると外す）
    func previewPick(_ code: UInt16) {
        guard let a = assignments[code] else { return }
        selectedKey = code
        if layerPreview[a.layer] == .some(code) {
            layerPreview[a.layer] = nil
        } else {
            layerPreview[a.layer] = .some(code)
        }
    }

    /// 「レイヤーを消去」：プレビューでそのレイヤーを空にする
    func previewClear(layer: Int) {
        layerPreview[layer] = .some(nil)
    }

    var hasPendingPreview: Bool { !layerPreview.isEmpty }

    /// 「ライブへ」：プレビューに組んだものを出す。smooth ならフェード（秒数は設定）、cut ならすぐ
    func take(smooth: Bool) {
        let fade = smooth ? max(Prefs.takeDuration, 0) : 0
        for (layer, target) in layerPreview.sorted(by: { $0.key < $1.key }) {
            for cue in cues where !cue.stopping && cue.page == currentPageID && cue.assignment.layer == layer
                && isVisual(cue.assignment) && cue.key != target {
                stop(cue, fade: max(fade, 0.03))
            }
            if let code = target, !isRunning(code) { startKey(code, fadeOverride: fade) }
        }
        layerPreview = [:]
    }

    /// ページを変えたらプレビューは組み直し
    func resetLayerPreview() { layerPreview = [:] }

    /// キーの映像レイヤーを変える（次に流したときから）
    func setLayer(_ code: UInt16, _ layer: Int) {
        guard var a = assignments[code], a.layer != layer else { return }
        a.layer = min(max(layer, 1), 9)
        update(code, a)
    }

    /// 画面のボタンから：鳴っていれば止め、止まっていれば鳴らす
    func togglePlayback(_ code: UInt16) {
        guard let a = assignments[code] else { return }
        if isRunning(code) { stopKey(code, fade: a.fadeOut) } else { startKey(code) }
    }

    func isRunning(_ code: UInt16) -> Bool { runningCue(code) != nil }

    /// いまのページで、そのキーから鳴っている素材
    private func runningCue(_ code: UInt16) -> ActiveCue? {
        cues.last { $0.key == code && !$0.stopping && $0.page == currentPageID }
    }

    /// 鳴っている素材の素材リスト（鳴らしたページの割り当てで）
    private func deckItems(for cue: ActiveCue) -> [DeckItem] {
        let a = cue.assignment
        guard let deck = media[a.mediaKey]?.deck else { return a.deckItems ?? [] }
        return DeckItem.merge(saved: a.deckItems, fresh: deck.items)
    }

    private func enabledDeckItems(for cue: ActiveCue) -> [DeckItem] { deckItems(for: cue).filter(\.enabled) }

    /// 新しい素材が流れるとき、「次の素材が流れたら止める」設定のキーを止める
    /// レイヤー表示では、映像は同じレイヤーの映像が流れたときだけ止める（ほかのレイヤーとは重ねる）
    private func applyNextBehavior(startingKey code: UInt16, group: Int) {
        let starting = assignments[code]
        let layered = UserDefaults.standard.string(forKey: Prefs.mainViewKey) == "layers"
        for cue in cues where !cue.stopping && cue.key != code {
            let a = cue.assignment
            guard a.onNext != .keep else { continue }
            if layered, isVisual(a) {
                guard let s = starting, isVisual(s), s.layer == a.layer else { continue }
            }
            if a.group == 0 || a.group == group {
                stop(cue, fade: a.onNext == .cut ? 0 : (deckFadeOut(cue) ?? a.fadeOut))
            }
        }
    }

    private func nextZ(layer: Int) -> CGFloat {
        visualSequence += 1
        return CGFloat(layer) * 1_000_000 + visualSequence
    }

    /// fadeOverride：レイヤー表示の「ライブへ」など、キーのフェード IN の代わりに使う秒数
    func startKey(_ code: UInt16, deckIndex: Int = 0, fadeOverride: Double? = nil) {
        guard let a = assignments[code] else { return }
        let fadeIn = fadeOverride ?? a.fadeIn
        guard let info = media[a.mediaKey], !info.loading else {
            statusMessage = "\(a.displayName) は\(media[a.mediaKey]?.progressText ?? "読み込み中です")"
            return
        }
        if let error = info.error {
            statusMessage = "\(a.displayName): \(error)"
            NSSound.beep()
            return
        }
        applyNextBehavior(startingKey: code, group: a.group)

        let cue = ActiveCue(id: nextCueID, key: code, page: currentPageID, assignment: a)
        nextCueID += 1

        // ライブ入力：カメラ・キャプチャ機器の映像と、入力の音
        if let live = a.liveInput {
            if a.showVideo, let device = CaptureDevices.device(live.videoID) {
                cue.liveVideo = video.showLive(device: device, fadeIn: fadeIn, z: nextZ(layer: a.layer), outputs: a.screenSet)
            }
            if let device = CaptureDevices.device(live.audioID), let capture = AudioCapture(device: device, sampleRate: audio.sampleRate) {
                capture.start()
                cue.audioCapture = capture
                let routes = Routing.routes(for: a, sourceChannels: capture.source.channels, outputChannels: audio.channelCount)
                cue.voiceID = audio.mixer.addLiveVoice(source: capture.source, routes: routes, volume: Float(a.volume), fadeIn: fadeIn)
            }
            guard cue.voiceID != nil || cue.liveVideo != nil else {
                statusMessage = "\(a.displayName): ライブ入力を開けません（機器の接続と、カメラ・マイクの許可を確認してください）"
                return
            }
            cues.append(cue)
            return
        }
        if let deck = info.deck {
            guard !enabledDeckItems(code).isEmpty else {
                statusMessage = "\(a.displayName): 流す素材がありません（一覧ですべて無効になっています）"
                return
            }
            cue.deck = DeckCue(deck: deck)
            cues.append(cue)
            // 最初に出す素材に「この素材に入るとき」が決めてあればそれで、なければキーのフェード IN で出す
            let startItems = enabledDeckItems(code)
            var fade = fadeIn, style = TransitionStyle.dissolve
            if fadeOverride == nil, deckIndex < startItems.count, startItems[deckIndex].transition != nil || startItems[deckIndex].transitionStyle != nil {
                fade = startItems[deckIndex].transition ?? a.fadeIn
                style = startItems[deckIndex].transitionStyle ?? .dissolve
            }
            deckGo(cue, to: deckIndex, fade: fade, style: style)
            return
        }

        let wantsVideo = info.hasVideo && a.showVideo
        if let clip = info.clip {
            let routes = Routing.routes(for: a, sourceChannels: clip.channelCount, outputChannels: audio.channelCount)
            if routes.isEmpty { statusMessage = "\(a.displayName): 出力先のチャンネルがありません（ルーティングを確認）" }
            cue.voiceID = audio.mixer.addVoice(clip: clip, routes: routes, volume: Float(a.volume),
                                               fadeIn: fadeIn, loop: a.loop, pending: wantsVideo)
        }
        if wantsVideo {
            let mixer = audio.mixer
            let voiceID = cue.voiceID
            cue.video = video.play(url: info.playURL ?? a.url, fadeIn: fadeIn, loop: a.loop,
                                   playerAudio: info.audioViaPlayer, volume: Float(a.volume), z: nextZ(layer: a.layer),
                                   outputs: a.screenSet,
                                   onStart: { host in if let voiceID { mixer.setStart(id: voiceID, hostTime: host) } },
                                   onFail: { [weak self] in
                                       if let voiceID { mixer.setStart(id: voiceID, hostTime: 0) }
                                       self?.statusMessage = "\(a.displayName): 映像を再生できません"
                                   })
        }
        guard cue.voiceID != nil || cue.video != nil else { return }
        cues.append(cue)
    }

    // MARK: - スライド素材

    /// 保存した並び・設定を反映した素材リスト
    func deckItems(_ code: UInt16) -> [DeckItem] {
        guard let a = assignments[code] else { return [] }
        guard let deck = media[a.mediaKey]?.deck else { return a.deckItems ?? [] }
        return DeckItem.merge(saved: a.deckItems, fresh: deck.items)
    }

    func enabledDeckItems(_ code: UInt16) -> [DeckItem] { deckItems(code).filter(\.enabled) }

    func setDeckItems(_ code: UInt16, _ items: [DeckItem]) {
        guard var a = assignments[code] else { return }
        a.deckItems = items
        update(code, a)
    }

    func moveDeckItems(_ code: UInt16, from: IndexSet, to: Int) {
        var items = deckItems(code)
        items.move(fromOffsets: from, toOffset: to)
        setDeckItems(code, items)
    }

    func updateDeckItem(_ code: UInt16, _ item: DeckItem) {
        var items = deckItems(code)
        guard let i = items.firstIndex(where: { $0.id == item.id }) else { return }
        items[i] = item
        setDeckItems(code, items)
    }

    func resetDeckItems(_ code: UInt16) {
        guard var a = assignments[code] else { return }
        a.deckItems = nil
        update(code, a)
    }

    func openDeckTab(_ code: UInt16) {
        guard assignments[code]?.isDeck == true else { return }
        if !deckTabs.contains(code) { deckTabs.append(code) }
        activeDeckTab = code
    }

    func closeDeckTab(_ code: UInt16) {
        deckTabs.removeAll { $0 == code }
        if activeDeckTab == code { activeDeckTab = deckTabs.last }
    }

    /// 一覧から：その素材へ飛ぶ（止まっていればそこから流す）
    func deckJump(_ code: UInt16, itemID: String) {
        guard let a = assignments[code] else { return }
        let items = enabledDeckItems(code)
        guard let i = items.firstIndex(where: { $0.id == itemID }) else {
            statusMessage = "無効にした素材は流せません"
            return
        }
        if let cue = runningCue(code), cue.deck != nil {
            let t = transition(into: items[i], a)
            deckGo(cue, to: i, fade: t.seconds, style: t.style)
        } else {
            startKey(code, deckIndex: i)
        }
    }

    func deckStep(code: UInt16, by delta: Int) {
        guard let cue = runningCue(code), cue.deck != nil else { return }
        deckStep(cue, by: delta)
    }

    func stepDeck(cueID: Int, by delta: Int) {
        guard let cue = cues.first(where: { $0.id == cueID && !$0.stopping }), cue.deck != nil else { return }
        deckStep(cue, by: delta)
    }

    private func deckStep(_ cue: ActiveCue, by delta: Int) {
        guard let d = cue.deck else { return }
        let a = cue.assignment
        let items = enabledDeckItems(for: cue)
        let current = d.itemID.flatMap { id in items.firstIndex { $0.id == id } } ?? d.index
        let next = max(0, current + delta)
        if next < items.count {
            let t = transition(into: items[next], a)
            deckGo(cue, to: next, fade: t.seconds, style: t.style)
        } else {
            deckGo(cue, to: next, fade: 0)
        }
    }

    /// index の素材に切り替える（最後の次は、ループなら先頭、そうでなければ終了）
    private func deckGo(_ cue: ActiveCue, to index: Int, fade: Double, style: TransitionStyle = .dissolve) {
        guard let d = cue.deck, !cue.stopping else { return }
        let a = cue.assignment
        let items = enabledDeckItems(for: cue)
        var i = max(0, index)
        var fade = fade, style = style
        if i >= items.count {
            guard a.slideLoop && !items.isEmpty else { stop(cue, fade: deckFadeOut(cue) ?? a.fadeOut); return }
            i = 0
            (fade, style) = transition(into: items[0], a)
        }
        let item = items[i]
        d.generation += 1
        if style == .cut { fade = 0 }

        // 色を経由：前半で今の映像を色で覆い、覆いきったら次の素材に替えて、後半で色を外す
        if let color = style.color, fade > 0, item.kind != .audio {
            let half = fade / 2
            let z = nextZ(layer: a.layer)
            let curtain = video.showColor(color, fadeIn: half, z: nextZ(layer: a.layer), outputs: a.screenSet)
            if let v = d.voiceID { audio.mixer.stop(id: v, fade: half) }
            d.voiceID = nil
            // 押した時点で次の素材に進んだことにする（途中でもう一度押せば、その次へ）
            d.itemID = item.id
            d.index = i
            d.itemStarted = Date()
            let generation = d.generation
            DispatchQueue.main.asyncAfter(deadline: .now() + half) { [weak self, weak cue] in
                guard let self else { return }
                if let cue, let d = cue.deck, d.generation == generation, !cue.stopping {
                    self.showDeckItem(cue, item, index: i, fade: 0, audioFade: half, z: z)
                }
                self.video.stop(curtain, fade: half)
            }
            return
        }
        showDeckItem(cue, item, index: i, fade: fade, audioFade: fade, z: nextZ(layer: a.layer))
    }

    /// 素材に入るときの切り替え：素材ごとの設定 → pptx・Keynote の設定 → キーの設定
    func transition(into item: DeckItem, _ a: KeyAssignment) -> (seconds: Double, style: TransitionStyle) {
        if item.transition != nil || item.transitionStyle != nil {
            return (item.transition ?? item.fileTransition ?? a.slideTransition,
                    item.transitionStyle ?? item.fileTransitionStyle ?? a.slideTransitionStyle)
        }
        if let s = item.fileTransitionStyle { return (item.fileTransition ?? a.slideTransition, s) }
        return (a.slideTransition, a.slideTransitionStyle)
    }

    /// 今出ている素材に、止めるときのフェードアウトが決めてあればその秒数
    private func deckFadeOut(_ cue: ActiveCue) -> Double? {
        guard let d = cue.deck, let id = d.itemID else { return nil }
        return deckItems(for: cue).first { $0.id == id }?.fadeOut
    }

    private func showDeckItem(_ cue: ActiveCue, _ item: DeckItem, index i: Int, fade: Double, audioFade: Double, z: CGFloat) {
        guard let d = cue.deck else { return }
        let a = cue.assignment
        let deck = d.deck
        let items = enabledDeckItems(for: cue)

        if let v = d.voiceID { audio.mixer.stop(id: v, fade: audioFade) }
        d.voiceID = nil
        if item.kind != .audio {
            if let old = d.image { video.stop(old, fade: fade) }
            if let old = d.video { video.stop(old, fade: fade) }
            d.image = nil
            d.video = nil
        }
        let volume = Float(a.volume * item.volume)
        let loop = item.endAction == .loop
        switch item.kind {
        case .image:
            d.image = video.showImage(load: { done in deck.image(for: item, completion: done) },
                                      fadeIn: fade, z: z, outputs: a.screenSet)
        case .video:
            let clip = deck.clips[item.id]
            if let clip {
                let routes = Routing.routes(for: a, sourceChannels: clip.channelCount, outputChannels: audio.channelCount)
                d.voiceID = audio.mixer.addVoice(clip: clip, routes: routes, volume: volume, fadeIn: audioFade, loop: loop, pending: true)
            }
            let mixer = audio.mixer
            let voiceID = d.voiceID
            let itemID = item.id
            d.video = video.play(url: deck.url(item), fadeIn: fade, loop: loop, playerAudio: clip == nil && deck.probes[item.id]?.hasAudio == true,
                                 volume: volume, z: z, outputs: a.screenSet, holdAtEnd: true,
                                 onEnd: { [weak self, weak cue] in
                                     guard let self, let cue, let d = cue.deck, d.itemID == itemID, item.endAction == .next else { return }
                                     self.deckStep(cue, by: 1)
                                 },
                                 onStart: { host in if let voiceID { mixer.setStart(id: voiceID, hostTime: host) } },
                                 onFail: { [weak self] in
                                     if let voiceID { mixer.setStart(id: voiceID, hostTime: 0) }
                                     self?.statusMessage = "\(item.title): 映像を再生できません"
                                 })
        case .audio:
            if let clip = deck.clips[item.id] {
                let routes = Routing.routes(for: a, sourceChannels: clip.channelCount, outputChannels: audio.channelCount)
                d.voiceID = audio.mixer.addVoice(clip: clip, routes: routes, volume: volume, fadeIn: audioFade, loop: loop, pending: false)
            } else {
                statusMessage = "\(item.title): 音声を読み込めませんでした"
            }
        }
        d.itemID = item.id
        d.index = i
        d.itemStarted = Date()

        if i + 1 < items.count {
            let next = items[i + 1]
            if next.kind == .image { deck.preload(next) } else if next.kind == .video { video.prepare(deck.url(next)) }
        }
    }

    func stopKey(_ code: UInt16, fade: Double) {
        for cue in cues where cue.key == code && !cue.stopping && cue.page == currentPageID {
            // 素材ごとにフェードアウトを決めてあれば、それで止める
            let f = fade == cue.assignment.fadeOut ? (deckFadeOut(cue) ?? fade) : fade
            stop(cue, fade: f)
        }
    }

    func stopAll(immediate: Bool) {
        for cue in cues where !cue.stopping || immediate { stop(cue, fade: immediate ? 0 : stopAllFade) }
    }

    /// シークできる音・映像（スライド素材ならいま流している動画・音声素材）
    private func seekTarget(_ cue: ActiveCue) -> (voiceID: Int?, video: VideoCue?, duration: Double)? {
        if cue.assignment.isLive { return nil }
        if let d = cue.deck {
            guard let id = d.itemID, let item = deckItems(for: cue).first(where: { $0.id == id }), item.kind != .image else { return nil }
            let duration = d.deck.clips[id]?.duration ?? d.video?.duration ?? d.deck.probes[id]?.duration ?? 0
            return (d.voiceID, d.video, duration)
        }
        guard cue.voiceID != nil || cue.video != nil else { return nil }
        let clip = media[cue.assignment.mediaKey]?.clip
        return (cue.voiceID, cue.video, clip?.duration ?? cue.video?.duration ?? 0)
    }

    /// そのキーで流れている映像の再生位置（0...1）
    func videoPosition(for code: UInt16) -> Double? {
        guard let cue = cues.last(where: { $0.key == code && !$0.stopping }) else { return nil }
        return (cue.video ?? cue.deck?.video)?.progress
    }

    /// 再生中リストのシークバーから：fraction（0...1）の位置へ移す。映像があれば音声もそこに合わせる
    func seek(cueID: Int, to fraction: Double) {
        guard let cue = cues.first(where: { $0.id == cueID && !$0.stopping }), let target = seekTarget(cue),
              target.duration > 0 else { return }
        let seconds = min(max(fraction, 0), 0.999) * target.duration
        if let videoCue = target.video {
            let host = HostTime.now(plus: 0.1)
            // 映像が終わっている／まだ始まっていないときは動かさない
            guard video.seek(videoCue, to: seconds, atHostTime: host) else { return }
            if let v = target.voiceID { audio.mixer.seek(id: v, seconds: seconds, startHost: host) }
        } else if let v = target.voiceID {
            audio.mixer.seek(id: v, seconds: seconds)
        }
    }

    func stopCue(id: Int) {
        guard let cue = cues.first(where: { $0.id == id }) else { return }
        stop(cue, fade: deckFadeOut(cue) ?? cue.assignment.fadeOut)
    }

    private func stop(_ cue: ActiveCue, fade: Double) {
        cue.stopping = true
        if let id = cue.voiceID { audio.mixer.stop(id: id, fade: fade) }
        if let v = cue.video { video.stop(v, fade: fade) }
        if let lv = cue.liveVideo { video.stop(lv, fade: fade) }
        if let d = cue.deck {
            if let v = d.voiceID { audio.mixer.stop(id: v, fade: fade) }
            if let i = d.image { video.stop(i, fade: fade) }
            if let v = d.video { video.stop(v, fade: fade) }
        }
    }

    private func tick() {
        let statuses = audio.mixer.snapshot()
        var progress: [UInt16: Double] = [:]
        var slidePosition: [UInt16: String] = [:]
        var deckCurrent: [UInt16: String] = [:]
        var sourceLevels: [UInt16: [Float]] = [:]
        var cueValues: [Int: LiveValues.Cue] = [:]
        var rows: [CueRow] = []
        let now = Date()

        // スライド素材の自動送り（画像の秒数、音声素材の終わり）
        for cue in cues where !cue.stopping {
            let a = cue.assignment
            guard let d = cue.deck, let item = deckItems(for: cue).first(where: { $0.id == d.itemID }) else { continue }
            switch item.kind {
            case .image:
                let after = item.advanceAfter ?? (a.slideAuto ? a.slideInterval : 0)
                if after > 0 && now.timeIntervalSince(d.itemStarted) >= max(after, 0.3) { deckStep(cue, by: 1) }
            case .audio:
                if item.endAction == .next, let v = d.voiceID, statuses[v]?.done == true { deckStep(cue, by: 1) }
            case .video:
                break
            }
        }

        let pageID = currentPageID
        var playingPages = Set<UUID>()
        cues.removeAll { cue in
            var slideText: String?
            let p: Double
            // キーボードの表示（進み具合・素材メーター）はいまのページで鳴らしたものだけ
            let here = cue.page == pageID
            if let d = cue.deck {
                let voiceDone = d.voiceID.flatMap { statuses[$0]?.done } ?? true
                if cue.stopping && voiceDone && (d.image?.finished ?? true) && (d.video?.finished ?? true) { return true }
                let count = max(enabledDeckItems(for: cue).count, 1)
                p = Double(d.index + 1) / Double(count)
                slideText = "\(d.index + 1)/\(count)"
                if here { slidePosition[cue.key] = slideText }
                if here, !cue.stopping, let id = d.itemID { deckCurrent[cue.key] = id }
            } else {
                let status = cue.voiceID.flatMap { statuses[$0] }
                let audioDone = status?.done ?? true
                let videoDone = (cue.video?.finished ?? true) && (cue.liveVideo?.finished ?? true)
                if audioDone && videoDone {
                    cue.audioCapture?.stop()
                    return true
                }
                // ライブ入力は「流れている間ずっと」なので帯はいっぱい
                p = cue.assignment.isLive ? 1 : ((!audioDone ? status?.progress : nil) ?? cue.video?.progress ?? 0)
            }
            if !cue.stopping { playingPages.insert(cue.page) }
            if here { progress[cue.key] = max(progress[cue.key] ?? 0, p) }
            if here, let vid = cue.deck?.voiceID ?? cue.voiceID, let levels = statuses[vid]?.levels {
                var merged = sourceLevels[cue.key] ?? []
                if merged.count < levels.count { merged += [Float](repeating: 0, count: levels.count - merged.count) }
                for i in levels.indices { merged[i] = max(merged[i], levels[i]) }
                sourceLevels[cue.key] = merged
            }
            // 一覧の行（SwiftUI）には、行の見た目が変わる情報だけを入れる。進み具合は cueValues へ
            var row = CueRow(id: cue.id, key: cue.key, name: cue.name, progress: cue.deck != nil ? p : 0,
                             stopping: cue.stopping, hasVideo: cue.video != nil || cue.deck?.video != nil || cue.liveVideo != nil, slideText: slideText)
            var live = LiveValues.Cue(position: nil, progress: p, duration: 0, stopping: cue.stopping)
            if let media = seekTarget(cue) {
                let status = media.voiceID.flatMap { statuses[$0] }
                if let status, !status.done {
                    live.position = status.progress
                } else if let v = media.video, !v.finished {
                    live.position = v.progress
                }
                live.duration = media.duration
            }
            cueValues[cue.id] = live
            row.seekable = live.position != nil
            row.channels = (cue.deck.map { d in d.itemID.flatMap { d.deck.clips[$0]?.channelCount } } ?? nil)
                ?? media[cue.assignment.mediaKey]?.clip?.channelCount ?? 0
            if let d = cue.deck { row.itemName = deckItems(for: cue).first { $0.id == d.itemID }?.title }
            if !here { row.pageName = pages.first { $0.id == cue.page }?.name ?? "削除したページ" }
            rows.append(row)
            return false
        }
        if live.deckCurrent != deckCurrent { live.deckCurrent = deckCurrent }
        if pageLive.playingPages != playingPages { pageLive.playingPages = playingPages }

        // 素材メーター：キーごと・素材のチャンネルごと（ゆっくり下がる）
        var sourceMeters: [UInt16: [Float]] = [:]
        for (key, levels) in sourceLevels {
            let old = displayedSourceMeters[key] ?? []
            sourceMeters[key] = levels.indices.map { i in max(levels[i], (i < old.count ? old[i] : 0) * 0.8) }
        }
        displayedSourceMeters = sourceMeters
        audio.mixer.removeDone()
        // SwiftUI へは「鳴り始めた・止まった」などの変化だけを流す
        let playing = Set(progress.keys)
        if live.playing != playing { live.playing = playing }
        if live.slidePosition != slidePosition { live.slidePosition = slidePosition }
        if cueState.cues != rows { cueState.cues = rows }

        let peaks = audio.mixer.takeMeters(count: visibleOutputChannels)
        if displayedMeters.count != peaks.count { displayedMeters = peaks }
        for i in peaks.indices { displayedMeters[i] = max(peaks[i], displayedMeters[i] * 0.8) }

        // 毎回変わる値は軽い描画ビューへ直接
        values.progress = progress
        values.meters = displayedMeters
        values.sourceMeters = sourceMeters
        values.cues = cueValues
        values.refresh()
    }

    // MARK: - キーマップの保存と読み込み

    private func markDirty() {
        guard !applyingDocument, !isDirty else { return }
        isDirty = true
        onTitleChange?()
    }

    /// キーマップに保存してある表示（開いた・保存したときのもの）
    private var documentView = AppModel.currentView
    private static var currentView: String { "\(UserDefaults.standard.string(forKey: Prefs.mainViewKey) ?? "keyboard")|\(Prefs.layerMonitors)" }

    /// 表示を切り替えたとき：表示もキーマップに入るので、未保存にする（まだ何も入っていない新しいキーマップは除く）
    func viewChanged() {
        guard AppModel.currentView != documentView else { return }
        if documentURL != nil || pages.contains(where: { !$0.keys.isEmpty }) || !assignments.isEmpty { markDirty() }
    }

    var documentName: String { documentURL?.lastPathComponent ?? "名称未設定.keycue" }

    func newDocument() {
        stopAll(immediate: true)
        applyingDocument = true
        assignments = [:]
        pages = [KeyPage(name: "ページ 1", keys: [:])]
        currentPage = 0
        deckTabs = []
        activeDeckTab = nil
        media = [:]
        mutedOutputs = []
        video.resetPrepared()
        applyingDocument = false
        documentURL = nil
        documentView = AppModel.currentView
        isDirty = false
        UserDefaults.standard.removeObject(forKey: lastDocumentKey)
        onTitleChange?()
    }

    func open(_ url: URL) throws {
        let data = try Data(contentsOf: url)
        let doc = try JSONDecoder().decode(KeymapDocument.self, from: data)
        stopAll(immediate: true)
        applyingDocument = true
        defer { applyingDocument = false }

        let base = url.deletingLastPathComponent()
        let pageDocs = doc.pages ?? [PageDocument(name: "ページ 1", keys: doc.keys)]
        var loadedPages: [KeyPage] = []
        for pd in pageDocs {
            loadedPages.append(KeyPage(name: pd.name, keys: AppModel.resolve(pd.keys, base: base)))
        }
        if loadedPages.isEmpty { loadedPages = [KeyPage(name: "ページ 1", keys: [:])] }
        let index = min(max(doc.currentPage ?? 0, 0), loadedPages.count - 1)
        media = [:]
        deckTabs = []
        activeDeckTab = nil
        pages = loadedPages
        currentPage = index
        assignments = loadedPages[index].keys
        masterVolume = doc.masterVolume
        stopAllFade = doc.stopAllFade
        mutedOutputs = Set(doc.mutedOutputs ?? [])
        // キーマップを作ったときの表示に
        if let v = doc.mainView, ["keyboard", "thumbnails", "layers"].contains(v) { UserDefaults.standard.set(v, forKey: Prefs.mainViewKey) }
        if let m = doc.layerMonitors { UserDefaults.standard.set(m, forKey: Prefs.layerMonitorsKey) }
        // 古いキーマップは出し先 1 つだけ
        if let outs = doc.videoOutputs {
            video.outputs = outs
        } else {
            let old = VideoTarget(stored: doc.videoTarget)
            if case .display(let name) = old { video.outputs = [name] } else { video.outputs = [VideoOutput.windowID] }
        }
        if outputDeviceUID != doc.outputDeviceUID {
            outputDeviceUID = doc.outputDeviceUID
            restartAudio()
        }
        finishOpening(url, doc: doc)
    }

    /// 保存したキーの割り当てを読み、見つからないファイルはキーマップからの相対パスで探す
    private static func resolve(_ stored: [String: KeyAssignment], base: URL) -> [UInt16: KeyAssignment] {
        var keys: [UInt16: KeyAssignment] = [:]
        for (k, var a) in stored {
            guard let code = UInt16(k) else { continue }
            let fm = FileManager.default
            if !fm.fileExists(atPath: a.path), let rel = a.relativePath {
                let candidate = base.appendingPathComponent(rel).standardizedFileURL.path
                if fm.fileExists(atPath: candidate) { a.path = candidate }
            }
            // 素材リスト：見つからないファイルはキーマップからの相対パスで探す（素材ごとの設定も付け替える）
            if var sources = a.sources {
                for i in sources.indices where !fm.fileExists(atPath: sources[i]) {
                    guard let rels = a.relativeSources, i < rels.count else { continue }
                    let candidate = base.appendingPathComponent(rels[i]).standardizedFileURL.path
                    guard fm.fileExists(atPath: candidate) else { continue }
                    let old = sources[i]
                    a.deckItems = a.deckItems?.map { item in
                        var n = item
                        n.id = n.id.replacingOccurrences(of: ":" + old, with: ":" + candidate)
                        return n
                    }
                    sources[i] = candidate
                }
                a.sources = sources
                a.path = sources.first ?? a.path
            }
            keys[code] = a
        }
        return keys
    }

    private func finishOpening(_ url: URL, doc: KeymapDocument) {
        if let uid = doc.outputDeviceUID, !devices.contains(where: { $0.uid == uid }) {
            statusMessage = "保存時の出力デバイスが見つからないため、システム既定に出力しています"
        }
        documentURL = url
        documentView = AppModel.currentView
        isDirty = false
        reloadAll(force: true)
        UserDefaults.standard.set(url.path, forKey: lastDocumentKey)
        Prefs.addRecentKeymap(url.path)
        onTitleChange?()
    }

    func save(to url: URL) throws {
        let base = url.deletingLastPathComponent().standardizedFileURL.path
        var doc = KeymapDocument()
        pages[currentPage].keys = assignments
        func stored(_ keys: [UInt16: KeyAssignment]) -> [String: KeyAssignment] {
            var out: [String: KeyAssignment] = [:]
            for (code, var a) in keys {
                a.relativePath = AppModel.relativePath(a.path, from: base)
                a.relativeSources = a.sources?.map { AppModel.relativePath($0, from: base) }
                out[String(code)] = a
            }
            return out
        }
        doc.pages = pages.map { PageDocument(name: $0.name, keys: stored($0.keys)) }
        doc.currentPage = currentPage
        // 古い版でも開けるように、いまのページを keys にも入れておく
        doc.keys = stored(assignments)
        doc.outputDeviceUID = outputDeviceUID
        doc.masterVolume = masterVolume
        doc.videoOutputs = video.outputs
        doc.videoTarget = video.outputs.first.map { $0 == VideoOutput.windowID ? "window" : "display:" + $0 } ?? "window"
        doc.stopAllFade = stopAllFade
        doc.mutedOutputs = mutedOutputs.sorted()
        doc.mainView = UserDefaults.standard.string(forKey: Prefs.mainViewKey)
        doc.layerMonitors = Prefs.layerMonitors
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(doc).write(to: url, options: .atomic)
        documentURL = url
        documentView = AppModel.currentView
        isDirty = false
        UserDefaults.standard.set(url.path, forKey: lastDocumentKey)
        Prefs.addRecentKeymap(url.path)
        onTitleChange?()
    }

    private var lastDocumentKey: String { "lastKeymap.instance\(instanceNumber)" }

    func restoreLastDocument() {
        guard let path = UserDefaults.standard.string(forKey: lastDocumentKey),
              FileManager.default.fileExists(atPath: path) else { return }
        do {
            try open(URL(fileURLWithPath: path))
        } catch {
            statusMessage = "前回のキーマップを開けません: \(error.localizedDescription)"
        }
    }

    private static func relativePath(_ path: String, from base: String) -> String {
        let p = URL(fileURLWithPath: path).standardizedFileURL.pathComponents
        let b = URL(fileURLWithPath: base).pathComponents
        var i = 0
        while i < p.count && i < b.count && p[i] == b[i] { i += 1 }
        let ups = Array(repeating: "..", count: b.count - i)
        return (ups + p[i...]).joined(separator: "/")
    }
}
