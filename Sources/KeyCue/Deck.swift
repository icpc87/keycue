import AppKit
import AVFoundation
import CryptoKit
import ImageIO
import PDFKit
import UniformTypeIdentifiers

/// スライド素材（pptx / key / pdf）を展開した 1 つの素材
/// 素材を切り替えるときの効果（PowerPoint・Keynote のフェードに相当）
enum TransitionStyle: String, Codable, CaseIterable, Identifiable {
    /// なし（すぐ切り替え）
    case cut
    /// ディゾルブ：前の素材と重ねながら入れ替える（PowerPoint の「フェード（スムーズ）」）
    case dissolve
    /// いったん黒にしてから次を出す（PowerPoint の「フェード（黒）」・Keynote の「カラーでフェード」）
    case black
    /// いったん白にしてから次を出す
    case white

    var id: String { rawValue }
    var label: String {
        switch self {
        case .cut: return "なし（カット）"
        case .dissolve: return "ディゾルブ（重ねて切り替え）"
        case .black: return "黒を経由してフェード"
        case .white: return "白を経由してフェード"
        }
    }
    var shortLabel: String {
        switch self {
        case .cut: return "カット"
        case .dissolve: return "ディゾルブ"
        case .black: return "黒経由"
        case .white: return "白経由"
        }
    }
    /// 経由する色
    var color: CGColor? {
        switch self {
        case .black: return CGColor(gray: 0, alpha: 1)
        case .white: return CGColor(gray: 1, alpha: 1)
        default: return nil
        }
    }
}

struct DeckItem: Codable, Identifiable, Equatable {
    enum Kind: String, Codable { case image, video, audio }

    enum EndAction: String, Codable, CaseIterable, Identifiable {
        case next, hold, loop
        var id: String { rawValue }
        var label: String {
            switch self {
            case .next: return "次の素材へ進む"
            case .hold: return "最後で止まって待つ"
            case .loop: return "ループ"
            }
        }
    }

    var id: String
    var kind: Kind
    var file: String
    var slide: Int
    var title: String
    var enabled = true
    /// 画像を自動で送る秒数。nil = キーの設定に従う、0 = キーを押すまで待つ
    var advanceAfter: Double?
    /// 前の素材からの切り替え秒数。nil = キーの設定に従う
    var transition: Double?
    /// 前の素材からの切り替え効果。nil = キーの設定に従う
    var transitionStyle: TransitionStyle?
    /// この素材のときにキーを止めた（最後まで行った）ときのフェードアウト秒数。nil = キーの設定に従う
    var fadeOut: Double?
    /// pptx・Keynote で付けてあった切り替え（変換のたびに読み直す）
    var fileTransition: Double?
    var fileTransitionStyle: TransitionStyle?
    var volume = 1.0
    var endAction = EndAction.next

    init(id: String, kind: Kind, file: String, slide: Int, title: String, enabled: Bool) {
        self.id = id
        self.kind = kind
        self.file = file
        self.slide = slide
        self.title = title
        self.enabled = enabled
    }

    private enum CodingKeys: String, CodingKey {
        case id, kind, file, slide, title, enabled, advanceAfter, transition, transitionStyle, fadeOut, fileTransition, fileTransitionStyle, volume, endAction
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        kind = try c.decode(Kind.self, forKey: .kind)
        file = try c.decode(String.self, forKey: .file)
        slide = try c.decodeIfPresent(Int.self, forKey: .slide) ?? 0
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? id
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        advanceAfter = try c.decodeIfPresent(Double.self, forKey: .advanceAfter)
        transition = try c.decodeIfPresent(Double.self, forKey: .transition)
        transitionStyle = try c.decodeIfPresent(TransitionStyle.self, forKey: .transitionStyle)
        fadeOut = try c.decodeIfPresent(Double.self, forKey: .fadeOut)
        fileTransition = try c.decodeIfPresent(Double.self, forKey: .fileTransition)
        fileTransitionStyle = try c.decodeIfPresent(TransitionStyle.self, forKey: .fileTransitionStyle)
        volume = try c.decodeIfPresent(Double.self, forKey: .volume) ?? 1
        endAction = try c.decodeIfPresent(EndAction.self, forKey: .endAction) ?? .next
    }

    var symbol: String {
        switch kind {
        case .image: return "photo"
        case .video: return "film"
        case .audio: return "speaker.wave.2"
        }
    }

    /// 保存済みの並び・設定に、変換し直した素材を合わせる（消えた素材は外し、増えた素材は後ろへ）
    static func merge(saved: [DeckItem]?, fresh: [DeckItem]) -> [DeckItem] {
        guard let saved else { return fresh }
        let freshByID = Dictionary(uniqueKeysWithValues: fresh.map { ($0.id, $0) })
        var out: [DeckItem] = []
        for var item in saved {
            guard let f = freshByID[item.id] else { continue }
            item.kind = f.kind
            item.file = f.file
            item.slide = f.slide
            item.fileTransition = f.fileTransition
            item.fileTransitionStyle = f.fileTransitionStyle
            out.append(item)
        }
        let known = Set(out.map(\.id))
        out.append(contentsOf: fresh.filter { !known.contains($0.id) })
        return out
    }
}

struct DeckManifest: Codable {
    var stamp: String
    var engine: String
    var slideCount: Int
    var items: [DeckItem]
}

/// 展開済みのスライド素材フォルダ
final class Deck {
    let folder: URL
    let manifest: DeckManifest
    /// 動画・音声素材の音（itemID → クリップ）。大きすぎるものは入らない
    var clips: [String: AudioClip] = [:]
    var probes: [String: MediaProbe] = [:]
    /// 素材リストで見つからなかったファイル
    var missing: [String] = []

    private let queue = DispatchQueue(label: "KeyCue.Deck", qos: .userInitiated)
    private var images: [String: CGImage] = [:]
    private var imageOrder: [String] = []
    private var thumbs: [String: NSImage] = [:]

    init(folder: URL, manifest: DeckManifest) {
        self.folder = folder
        self.manifest = manifest
    }

    var items: [DeckItem] { manifest.items }

    /// 素材のファイル。素材リスト（複数ファイルを並べたキー）では元ファイルの絶対パスが入っている
    func url(_ item: DeckItem) -> URL {
        item.file.hasPrefix("/") ? URL(fileURLWithPath: item.file) : folder.appendingPathComponent(item.file)
    }

    /// 複数ファイルを並べた素材リストか（pptx などを 1 つ展開したものではなく）
    var isList: Bool { manifest.engine == "list" }

    /// 一覧の行用。動画は必要になったときに 1 枚目を作る
    @MainActor
    func thumbnailAsync(for item: DeckItem) async -> NSImage? {
        if let t = thumbnail(for: item) { return t }
        guard item.kind == .video else { return nil }
        let gen = AVAssetImageGenerator(asset: AVURLAsset(url: url(item)))
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: 360, height: 360)
        guard let (cg, _) = try? await gen.image(at: CMTime(seconds: 0.5, preferredTimescale: 600)) else { return nil }
        let image = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        thumbs[item.id] = image
        return image
    }

    /// ルーティング表に出す元チャンネル数（動画・音声素材の中で最大）
    var sourceChannels: Int { clips.values.map(\.channelCount).max() ?? 2 }

    /// 出力用の画像（直近のものだけ覚えておく）
    func image(for item: DeckItem, completion: @escaping (CGImage?) -> Void) {
        let url = url(item)
        queue.async { [self] in
            let image = images[item.id] ?? Deck.loadImage(url)
            if let image, images[item.id] == nil {
                images[item.id] = image
                imageOrder.append(item.id)
                while imageOrder.count > 4 { images[imageOrder.removeFirst()] = nil }
            }
            DispatchQueue.main.async { completion(image) }
        }
    }

    func preload(_ item: DeckItem) {
        guard item.kind == .image else { return }
        image(for: item) { _ in }
    }

    func thumbnail(for item: DeckItem) -> NSImage? {
        if let t = thumbs[item.id] { return t }
        let source: URL
        switch item.kind {
        case .image: source = url(item)
        case .video: source = folder.appendingPathComponent(item.id + ".thumb.png")
        case .audio: return nil
        }
        guard let src = CGImageSourceCreateWithURL(source as CFURL, nil),
              let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceThumbnailMaxPixelSize: 360,
              ] as CFDictionary) else { return nil }
        let image = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        thumbs[item.id] = image
        return image
    }

    static func loadImage(_ url: URL) -> CGImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(src, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary)
    }
}

// MARK: - 変換

/// pptx / key を PDF に書き出すアプリ。pptx は PowerPoint（なければ Keynote）、key は Keynote
enum SlideConverter {
    enum Engine: String {
        case keynote, powerpoint
        var label: String { self == .keynote ? "Keynote" : "PowerPoint" }
        var bundleID: String { self == .keynote ? "com.apple.Keynote" : "com.microsoft.Powerpoint" }
        var isInstalled: Bool { NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil }
    }

    static let deckExtensions: Set<String> = ["pptx", "ppt", "key", "pdf"]

    static func isDeck(_ url: URL) -> Bool { deckExtensions.contains(url.pathExtension.lowercased()) }

    static func engine(for url: URL) -> Engine? {
        switch url.pathExtension.lowercased() {
        case "pptx", "ppt": return Prefs.preferPowerPoint && Engine.powerpoint.isInstalled ? .powerpoint : .keynote
        case "key": return .keynote
        default: return nil
        }
    }

    /// Keynote で書き出したときに分かるスライドごとの情報
    struct SlideMeta {
        var skipped: Bool
        var movies: [String]
        var audio: [String]
        /// このスライドから次へ移るときの効果（Keynote の「トランジション」）
        var effect = ""
        var duration = 0.0
    }

    private static let queue = DispatchQueue(label: "KeyCue.SlideConverter")

    /// AppleScript は 1 本ずつ流す
    static func exportPDF(_ src: URL, to dst: URL, engine: Engine) async throws -> [SlideMeta] {
        try await withCheckedThrowingContinuation { cont in
            queue.async {
                do { cont.resume(returning: try export(src, to: dst, engine: engine)) } catch { cont.resume(throwing: error) }
            }
        }
    }

    private static func export(_ src: URL, to dst: URL, engine: Engine) throws -> [SlideMeta] {
        guard engine.isInstalled else {
            throw AudioLoader.LoadError.failed("\(engine.label) が見つかりません（スライドの変換に必要です）")
        }
        let wasRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: engine.bundleID).isEmpty
        defer {
            if !wasRunning { _ = try? runAppleScript("tell application id \"\(engine.bundleID)\" to quit saving no", args: []) }
        }
        switch engine {
        case .keynote:
            // ファイル参照は tell の外で作る（中で作ると Keynote 側で解決されて開けない）
            let output = try runAppleScript("""
            on run argv
                set srcFile to POSIX file (item 1 of argv)
                set dstFile to POSIX file (item 2 of argv)
                set out to ""
                tell application id "com.apple.Keynote"
                    set openCount to count of documents
                    set d to open srcFile
                    -- 起動直後や pptx の読み込み中は missing value が返るので、開き終わるまで待つ
                    set tries to 0
                    repeat while d is missing value and tries < 120
                        delay 0.5
                        set tries to tries + 1
                        if (count of documents) > openCount then set d to front document
                    end repeat
                    if d is missing value then error "Keynote でファイルを開けませんでした"
                    export d to dstFile as PDF with properties {skipped slides:true}
                    set AppleScript's text item delimiters to "|"
                    repeat with i from 1 to count of slides of d
                        set s to slide i of d
                        set mv to (file name of every movie of s) as text
                        set au to (file name of every audio clip of s) as text
                        set ef to ""
                        set du to 0
                        try
                            set tp to transition properties of s
                            set ef to (transition effect of tp) as text
                            set du to transition duration of tp
                        end try
                        set out to out & ((skipped of s) as text) & tab & mv & tab & au & tab & ef & tab & (du as text) & linefeed
                    end repeat
                    close d saving no
                end tell
                return out
            end run
            """, args: [src.path, dst.path])
            return output.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line in
                let cols = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
                guard cols.count >= 3 else { return nil }
                let names: (String) -> [String] = { $0.split(separator: "|").map(String.init).filter { !$0.isEmpty } }
                var m = SlideMeta(skipped: cols[0] == "true", movies: names(cols[1]), audio: names(cols[2]))
                if cols.count >= 5 {
                    m.effect = cols[3]
                    m.duration = Double(cols[4].replacingOccurrences(of: ",", with: ".")) ?? 0
                }
                return m
            }
        case .powerpoint:
            // PowerPoint はサンドボックス内にしか自由に書けないので、いったんそこへ保存してコピーする。
            // KeyCue からは消せないことがあるため、毎回同じ名前で上書きする
            let target = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Containers/com.microsoft.Powerpoint/Data/keycue-export.pdf")
            let started = Date()
            _ = try runAppleScript("""
            on run argv
                set srcFile to POSIX file (item 1 of argv)
                set dstFile to POSIX file (item 2 of argv)
                tell application id "com.microsoft.Powerpoint"
                    open srcFile
                    set p to active presentation
                    -- 非表示スライドも書き出す（一覧で無効として並べるため。元ファイルは保存しない）
                    repeat with i from 1 to count of slides of p
                        set hidden of slide show transition of slide i of p to false
                    end repeat
                    save p in dstFile as save as PDF
                    close p saving no
                end tell
            end run
            """, args: [src.path, target.path])
            let written = (try? FileManager.default.attributesOfItem(atPath: target.path)[.modificationDate] as? Date) ?? nil
            guard let written, written >= started.addingTimeInterval(-1) else {
                throw AudioLoader.LoadError.failed("PowerPoint で PDF に書き出せませんでした")
            }
            try FileManager.default.copyItem(at: target, to: dst)
            try? FileManager.default.removeItem(at: target)
            return []
        }
    }

    @discardableResult
    static func runAppleScript(_ source: String, args: [String]) throws -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", source] + args
        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        try p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            let msg = String(data: err.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            if msg.contains("-1743") {
                throw AudioLoader.LoadError.failed("スライド変換のため、システム設定 › プライバシーとセキュリティ › オートメーション で KeyCue に Keynote / PowerPoint の操作を許可してください")
            }
            throw AudioLoader.LoadError.failed("スライドを変換できません: \(msg.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
        return String(data: data, encoding: .utf8) ?? ""
    }
}

/// pptx / key / pdf をスライドごとの PNG と、埋め込まれた動画・音声に展開する
enum DeckBuilder {
    static var rootDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("KeyCue/Decks", isDirectory: true)
    }

    static let renderWidth: CGFloat = 2560

    /// 複数ファイルを順に並べた素材リスト。pptx / key / pdf はスライドごとに展開して間に入れる。
    /// 素材の ID は「何番目に出てくる同じファイルか」と元のパスから作るので、並べ替えても設定が残る
    static func buildList(_ sources: [String], force: Bool, progress: @escaping (String) -> Void) async throws -> Deck {
        let fm = FileManager.default
        var items: [DeckItem] = []
        var seen: [String: Int] = [:]
        var missing: [String] = []
        for (i, src) in sources.enumerated() {
            let url = URL(fileURLWithPath: src)
            seen[src, default: 0] += 1
            let n = seen[src]!
            let stem = url.deletingPathExtension().lastPathComponent
            guard fm.fileExists(atPath: src) else {
                missing.append(url.lastPathComponent)
                continue
            }
            if SlideConverter.isDeck(url) {
                let sub = try await build(url, force: force, progress: progress)
                for var item in sub.items {
                    let original = item.id
                    item.file = sub.url(item).path
                    item.id = "d\(n):\(src)#\(original)"
                    item.slide = i + 1
                    item.title = "\(stem) · \(item.title)"
                    items.append(item)
                }
                continue
            }
            let type = UTType(filenameExtension: url.pathExtension)
            let kind: DeckItem.Kind
            if type?.conforms(to: .image) == true { kind = .image }
            else if type?.conforms(to: .movie) == true || type?.conforms(to: .video) == true { kind = .video }
            else { kind = .audio }
            var item = DeckItem(id: "f\(n):\(src)", kind: kind, file: src, slide: i + 1, title: stem, enabled: true)
            if kind == .video {
                // そのままでは再生できない動画（アニメーション形式など）は変換したものを使う
                let converted = try await VideoConverter.playable(url) { p in progress("\(stem) を再生できる形式に変換中…（初回のみ）\(Int(p * 100))%") }
                item.file = converted.path
            }
            // 自分で並べた音・映像は、終わっても次へは進まず、キーを押すのを待つ
            if kind != .image { item.endAction = .hold }
            items.append(item)
        }
        if items.isEmpty && !missing.isEmpty {
            throw AudioLoader.LoadError.failed("ファイルが見つかりません: \(missing.joined(separator: ", "))")
        }
        let folder = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("KeyCue/lists", isDirectory: true)
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let manifest = DeckManifest(stamp: "", engine: "list", slideCount: sources.count, items: items)
        let deck = Deck(folder: folder, manifest: manifest)
        deck.missing = missing
        return deck
    }

    static func build(_ src: URL, force: Bool, progress: @escaping (String) -> Void) async throws -> Deck {
        let engine = SlideConverter.engine(for: src)
        let attrs = try FileManager.default.attributesOfItem(atPath: src.path)
        let stamp = "\(src.path)|\((attrs[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0)|\((attrs[.size] as? NSNumber)?.int64Value ?? 0)|\(engine?.rawValue ?? "pdf")|v2"
        let hash = SHA256.hash(data: Data(stamp.utf8)).map { String(format: "%02x", $0) }.joined()
        let folder = rootDir.appendingPathComponent(src.deletingPathExtension().lastPathComponent + "-" + hash.prefix(12), isDirectory: true)
        let manifestURL = folder.appendingPathComponent("manifest.json")

        if !force, let data = try? Data(contentsOf: manifestURL),
           let manifest = try? JSONDecoder().decode(DeckManifest.self, from: data), manifest.stamp == stamp {
            return Deck(folder: folder, manifest: manifest)
        }

        let fm = FileManager.default
        try? fm.removeItem(at: folder)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let work = folder.appendingPathComponent(".work", isDirectory: true)
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: work) }

        // 1. PDF にする
        var meta: [SlideConverter.SlideMeta] = []
        let pdfURL: URL
        if let engine {
            progress("\(engine.label) で変換中…")
            pdfURL = folder.appendingPathComponent("slides.pdf")
            meta = try await SlideConverter.exportPDF(src, to: pdfURL, engine: engine)
        } else {
            pdfURL = src
        }
        guard let pdf = PDFDocument(url: pdfURL), pdf.pageCount > 0 else {
            throw AudioLoader.LoadError.failed("スライドを読み込めません")
        }

        // 2. スライドごとの非表示設定と埋め込みメディア
        var slides: [(hidden: Bool, media: [URL])] = []
        switch src.pathExtension.lowercased() {
        case "pptx":
            slides = (try? pptxSlides(src, work: work)) ?? []
        case "key":
            let dataDir = try keynoteDataDir(src, work: work)
            let files = (try? fm.contentsOfDirectory(at: dataDir, includingPropertiesForKeys: [.fileSizeKey])) ?? []
            slides = meta.map { m in (m.skipped, (m.movies + m.audio).compactMap { matchKeynoteMedia($0, in: files) }) }
        default:
            break
        }
        if slides.isEmpty && !meta.isEmpty { slides = meta.map { ($0.skipped, []) } }

        // スライドに入るときの切り替え（スライド番号 → 効果）。PowerPoint は入る側、Keynote は出る側のスライドに付いている
        var fileTransitions: [Int: (TransitionStyle, Double)] = [:]
        switch src.pathExtension.lowercased() {
        case "pptx":
            fileTransitions = pptxTransitions(work: work)
        case "key":
            for (i, m) in meta.enumerated() {
                if let t = keynoteTransition(m) { fileTransitions[i + 2] = t }
            }
        default:
            break
        }

        // 3. ページ → スライドの対応（非表示スライドを含めない PDF にも合わせる）
        let pageCount = pdf.pageCount
        var pageForSlide: [Int?] = []
        if slides.isEmpty {
            slides = (0..<pageCount).map { _ in (false, []) }
            pageForSlide = Array(0..<pageCount)
        } else if slides.count == pageCount {
            pageForSlide = Array(0..<pageCount)
        } else if slides.filter({ !$0.hidden }).count == pageCount {
            var page = 0
            pageForSlide = slides.map { s in
                guard !s.hidden else { return nil }
                defer { page += 1 }
                return page
            }
        } else {
            pageForSlide = slides.indices.map { $0 < pageCount ? $0 : nil }
        }

        // 4. PNG と素材を書き出す
        var items: [DeckItem] = []
        for (i, s) in slides.enumerated() {
            let n = i + 1
            let tag = String(format: "s%03d", n)
            if let p = pageForSlide[i], let page = pdf.page(at: p) {
                progress("スライドを書き出し中… \(n)/\(slides.count)")
                let file = tag + ".png"
                try writePNG(page, to: folder.appendingPathComponent(file))
                var item = DeckItem(id: "s\(n)", kind: .image, file: file, slide: n, title: "スライド \(n)", enabled: !s.hidden)
                if let t = fileTransitions[n] {
                    item.fileTransitionStyle = t.0
                    item.fileTransition = t.1
                }
                items.append(item)
            }
            for (k, media) in s.media.enumerated() {
                let ext = media.pathExtension.lowercased()
                guard let type = UTType(filenameExtension: ext) else { continue }
                let kind: DeckItem.Kind
                if type.conforms(to: .movie) || type.conforms(to: .video) { kind = .video }
                else if type.conforms(to: .audio) { kind = .audio }
                else { continue }
                let id = "s\(n)-m\(k + 1)"
                let file = "\(tag)-m\(k + 1).\(ext)"
                try fm.copyItem(at: media, to: folder.appendingPathComponent(file))
                if kind == .video { await writeVideoThumbnail(folder.appendingPathComponent(file), to: folder.appendingPathComponent(id + ".thumb.png")) }
                let name = media.deletingPathExtension().lastPathComponent
                items.append(DeckItem(id: id, kind: kind, file: file, slide: n,
                                      title: "スライド \(n) の\(kind == .video ? "動画" : "音声")（\(name)）", enabled: !s.hidden))
            }
        }

        let manifest = DeckManifest(stamp: stamp, engine: engine?.rawValue ?? "pdf", slideCount: slides.count, items: items)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(manifest).write(to: manifestURL)
        return Deck(folder: folder, manifest: manifest)
    }

    private static func writePNG(_ page: PDFPage, to url: URL) throws {
        let box = page.bounds(for: .cropBox)
        guard box.width > 0, box.height > 0 else { throw AudioLoader.LoadError.failed("空のページがあります") }
        let scale = renderWidth / box.width
        let w = Int(box.width * scale), h = Int(box.height * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue) else {
            throw AudioLoader.LoadError.failed("画像を作れません")
        }
        ctx.setFillColor(.white)
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.interpolationQuality = .high
        ctx.scaleBy(x: scale, y: scale)
        ctx.translateBy(x: -box.minX, y: -box.minY)
        page.draw(with: .cropBox, to: ctx)
        guard let image = ctx.makeImage(), let data = pngData(image) else {
            throw AudioLoader.LoadError.failed("PNG を書き出せません")
        }
        try data.write(to: url)
    }

    /// ファイルへ直接書くと置き換えの段階で失敗することがあるので、メモリ上で作ってから書く
    private static func pngData(_ image: CGImage) -> Data? {
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data as CFMutableData, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(dest, image, nil)
        return CGImageDestinationFinalize(dest) ? data as Data : nil
    }

    private static func writeVideoThumbnail(_ video: URL, to url: URL) async {
        let gen = AVAssetImageGenerator(asset: AVURLAsset(url: video))
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: 480, height: 480)
        guard let (image, _) = try? await gen.image(at: CMTime(seconds: 0.5, preferredTimescale: 600)),
              let data = pngData(image) else { return }
        try? data.write(to: url)
    }

    private static func unzip(_ src: URL, pattern: String, to dir: URL) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        p.arguments = ["-o", "-qq", src.path, pattern, "-d", dir.path]
        p.standardOutput = Pipe()
        p.standardError = Pipe()
        try p.run()
        p.waitUntilExit()
    }

    /// pptx の各スライドの画面切り替え（<p:transition>）。pptxSlides で展開したあとに呼ぶ
    private static func pptxTransitions(work: URL) -> [Int: (TransitionStyle, Double)] {
        let ppt = work.appendingPathComponent("ppt")
        guard let presentation = try? XMLDocument(contentsOf: ppt.appendingPathComponent("presentation.xml")),
              let nodes = try? presentation.nodes(forXPath: "//*[local-name()='sldId']") else { return [:] }
        let presRels = relationships(ppt.appendingPathComponent("_rels/presentation.xml.rels"))
        let ids = nodes.compactMap { ($0 as? XMLElement)?.attributes?.first { $0.name?.hasSuffix(":id") == true }?.stringValue }
        var out: [Int: (TransitionStyle, Double)] = [:]
        for (i, rid) in ids.enumerated() {
            guard let target = presRels[rid]?.target,
                  let doc = try? XMLDocument(contentsOf: ppt.appendingPathComponent(target)),
                  let t = (try? doc.nodes(forXPath: "//*[local-name()='transition']"))?.first as? XMLElement else { continue }
            if let style = pptxTransition(t) { out[i + 1] = style }
        }
        return out
    }

    /// <p:transition spd="slow" p14:dur="2000"><p:fade thruBlk="1"/></p:transition> → 効果と秒数
    static func pptxTransition(_ t: XMLElement) -> (TransitionStyle, Double)? {
        let attr: (XMLElement, String) -> String? = { e, name in e.attributes?.first { ($0.localName ?? $0.name) == name }?.stringValue }
        var seconds: Double
        switch attr(t, "spd") {
        case "fast": seconds = 0.5
        case "med": seconds = 0.75
        default: seconds = 1.0
        }
        if let ms = attr(t, "dur").flatMap(Double.init) { seconds = ms / 1000 }
        guard let effect = t.children?.compactMap({ $0 as? XMLElement }).first(where: { $0.localName != "sndAc" && $0.localName != "extLst" }) else {
            return nil
        }
        let throughBlack = ["1", "true"].contains(attr(effect, "thruBlk") ?? "")
        switch effect.localName {
        case "cut": return throughBlack ? (.black, max(seconds, 0.5)) : (.cut, 0)
        case "fade": return (throughBlack ? .black : .dissolve, seconds)
        default: return (.dissolve, seconds)   // そのほかの効果はディゾルブで近づける
        }
    }

    /// Keynote のトランジション名 → 効果と秒数
    static func keynoteTransition(_ m: SlideConverter.SlideMeta) -> (TransitionStyle, Double)? {
        let e = m.effect.lowercased()
        guard !e.isEmpty, !e.contains("no transition"), e != "tnil" else { return nil }
        let seconds = m.duration > 0 ? m.duration : 1
        if e.contains("fade through color") || e == "tftc" { return (.black, seconds) }
        return (.dissolve, seconds)
    }

    /// pptx のスライド順・非表示・埋め込み動画／音声
    private static func pptxSlides(_ src: URL, work: URL) throws -> [(hidden: Bool, media: [URL])] {
        try unzip(src, pattern: "ppt/*", to: work)
        let ppt = work.appendingPathComponent("ppt")
        let presentation = try XMLDocument(contentsOf: ppt.appendingPathComponent("presentation.xml"))
        let presRels = relationships(ppt.appendingPathComponent("_rels/presentation.xml.rels"))
        let ids = try presentation.nodes(forXPath: "//*[local-name()='sldId']").compactMap { node -> String? in
            (node as? XMLElement)?.attributes?.first { $0.name?.hasSuffix(":id") == true }?.stringValue
        }
        return ids.compactMap { rid -> (Bool, [URL])? in
            guard let target = presRels[rid]?.target else { return nil }
            let slideURL = ppt.appendingPathComponent(target)
            let hidden = (try? XMLDocument(contentsOf: slideURL))?.rootElement()?.attribute(forName: "show")?.stringValue == "0"
            let relsURL = slideURL.deletingLastPathComponent()
                .appendingPathComponent("_rels/" + slideURL.lastPathComponent + ".rels")
            var seen = Set<String>()
            var media: [URL] = []
            for rel in relationships(relsURL).values.sorted(by: { $0.id < $1.id }) {
                guard !rel.external,
                      rel.type.hasSuffix("/video") || rel.type.hasSuffix("/audio") || rel.type.hasSuffix("/media") else { continue }
                let file = slideURL.deletingLastPathComponent().appendingPathComponent(rel.target).standardizedFileURL
                if seen.insert(file.path).inserted, FileManager.default.fileExists(atPath: file.path) { media.append(file) }
            }
            return (hidden, media)
        }
    }

    private struct Relationship { let id: String; let type: String; let target: String; let external: Bool }

    private static func relationships(_ url: URL) -> [String: Relationship] {
        guard let doc = try? XMLDocument(contentsOf: url),
              let nodes = try? doc.nodes(forXPath: "//*[local-name()='Relationship']") else { return [:] }
        var out: [String: Relationship] = [:]
        for case let e as XMLElement in nodes {
            guard let id = e.attribute(forName: "Id")?.stringValue, let target = e.attribute(forName: "Target")?.stringValue else { continue }
            out[id] = Relationship(id: id, type: e.attribute(forName: "Type")?.stringValue ?? "", target: target,
                                   external: e.attribute(forName: "TargetMode")?.stringValue == "External")
        }
        return out
    }

    /// key はフォルダ形式と zip 形式がある。埋め込みメディアは Data/ にある
    private static func keynoteDataDir(_ src: URL, work: URL) throws -> URL {
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: src.path, isDirectory: &isDir), isDir.boolValue {
            return src.appendingPathComponent("Data")
        }
        try unzip(src, pattern: "Data/*", to: work)
        return work.appendingPathComponent("Data")
    }

    /// Keynote の "media1.mp4" は Data/ の "media1-12.mp4" のように番号が付いて入っている
    private static func matchKeynoteMedia(_ name: String, in files: [URL]) -> URL? {
        let stem = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension.lowercased()
        let candidates = files.filter { f in
            let fs = f.deletingPathExtension().lastPathComponent
            guard f.pathExtension.lowercased() == ext, !fs.hasSuffix("-small") else { return false }
            if fs == stem { return true }
            guard fs.hasPrefix(stem + "-") else { return false }
            return Int(fs.dropFirst(stem.count + 1)) != nil
        }
        return candidates.max { a, b in
            ((try? a.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) < ((try? b.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        }
    }
}
