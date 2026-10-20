import AppKit
import QuartzCore
import SwiftUI

/// 再生中に毎回変わる値（進み具合・メーター・シーク位置）。
/// SwiftUI には流さず、下の軽い描画ビューが直接読んで描き直す（画面全体の配置計算を起こさないため）。
final class LiveValues {
    struct Cue {
        var position: Double?
        var progress: Double
        var duration: Double
        var stopping: Bool
    }

    var progress: [UInt16: Double] = [:]
    var meters: [Float] = []
    var sourceMeters: [UInt16: [Float]] = [:]
    var cues: [Int: Cue] = [:]

    private let views = NSHashTable<AnyObject>.weakObjects()

    func register(_ view: LiveRefreshing) { views.add(view) }

    /// 登録されている描画ビューを描き直す（tick から呼ぶ）
    func refresh() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for case let v as LiveRefreshing in views.allObjects { v.refresh(self) }
        CATransaction.commit()
    }
}

protocol LiveRefreshing: AnyObject {
    func refresh(_ values: LiveValues)
}

/// 層で描く NSView の共通部分
class LiveLayerView: NSView, LiveRefreshing {
    weak var values: LiveValues?

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.masksToBounds = true
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    func attach(_ values: LiveValues) {
        guard self.values !== values else { return }
        self.values = values
        values.register(self)
    }

    override func layout() {
        super.layout()
        if let values { CATransaction.begin(); CATransaction.setDisableActions(true); refresh(values); CATransaction.commit() }
    }

    func refresh(_ values: LiveValues) {}

    static func plainLayer(_ color: CGColor) -> CALayer {
        let l = CALayer()
        l.backgroundColor = color
        l.actions = ["bounds": NSNull(), "position": NSNull(), "frame": NSNull(), "backgroundColor": NSNull(), "hidden": NSNull()]
        return l
    }
}

// MARK: - キーの進み具合

final class KeyProgressView: LiveLayerView {
    var code: UInt16 = 0
    private let fill = LiveLayerView.plainLayer(NSColor.white.withAlphaComponent(0.28).cgColor)

    override init(frame: NSRect) {
        super.init(frame: frame)
        layer?.addSublayer(fill)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func refresh(_ values: LiveValues) {
        guard let p = values.progress[code] else { fill.isHidden = true; return }
        fill.isHidden = false
        fill.frame = CGRect(x: 0, y: 0, width: bounds.width * p, height: bounds.height)
    }
}

struct KeyProgressLayer: NSViewRepresentable {
    let values: LiveValues
    let code: UInt16
    let cornerRadius: CGFloat

    func makeNSView(context: Context) -> KeyProgressView {
        let v = KeyProgressView()
        v.code = code
        v.layer?.cornerRadius = cornerRadius
        v.attach(values)
        return v
    }

    func updateNSView(_ v: KeyProgressView, context: Context) {
        v.code = code
        v.layer?.cornerRadius = cornerRadius
        v.refresh(values)
    }
}

// MARK: - 波形の上の再生位置

final class PlayheadView: LiveLayerView {
    var code: UInt16 = 0
    var color: CGColor = NSColor.systemBlue.cgColor { didSet { shade.backgroundColor = color.copy(alpha: 0.35) } }
    private let shade = LiveLayerView.plainLayer(NSColor.systemBlue.withAlphaComponent(0.35).cgColor)
    private let line = LiveLayerView.plainLayer(NSColor.white.cgColor)

    override init(frame: NSRect) {
        super.init(frame: frame)
        layer?.addSublayer(shade)
        layer?.addSublayer(line)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func refresh(_ values: LiveValues) {
        guard let p = values.progress[code] else { shade.isHidden = true; line.isHidden = true; return }
        shade.isHidden = false
        line.isHidden = false
        let x = bounds.width * p
        shade.frame = CGRect(x: 0, y: 0, width: x, height: bounds.height)
        line.frame = CGRect(x: max(x - 1, 0), y: 0, width: 2, height: bounds.height)
    }
}

struct PlayheadLayer: NSViewRepresentable {
    let values: LiveValues
    let code: UInt16
    let color: Color

    func makeNSView(context: Context) -> PlayheadView {
        let v = PlayheadView()
        v.code = code
        v.color = NSColor(color).cgColor
        v.attach(values)
        return v
    }

    func updateNSView(_ v: PlayheadView, context: Context) {
        v.code = code
        v.color = NSColor(color).cgColor
        v.refresh(values)
    }
}

// MARK: - メーター（縦の棒をいくつか）

final class MeterBarsView: LiveLayerView {
    enum Source: Equatable { case output, material(UInt16) }

    var source: Source = .output
    var count = 0 { didSet { if count != oldValue { rebuild() } } }
    var spacing: CGFloat = 3
    /// 出力メーターで、いまのデバイスにない ch（色を変える）
    var deviceChannels = Int.max
    var muted: Set<Int> = []
    private var backs: [CALayer] = []
    private var fills: [CALayer] = []

    private func rebuild() {
        (backs + fills).forEach { $0.removeFromSuperlayer() }
        backs = (0..<count).map { _ in LiveLayerView.plainLayer(NSColor(white: 0.22, alpha: 1).cgColor) }
        fills = (0..<count).map { _ in LiveLayerView.plainLayer(NSColor.systemGreen.cgColor) }
        for (b, f) in zip(backs, fills) {
            layer?.addSublayer(b)
            layer?.addSublayer(f)
        }
        needsLayout = true
    }

    override func refresh(_ values: LiveValues) {
        guard count > 0 else { return }
        let levels: [Float]
        switch source {
        case .output: levels = values.meters
        case .material(let code): levels = values.sourceMeters[code] ?? []
        }
        let w = (bounds.width - spacing * CGFloat(count - 1)) / CGFloat(count)
        for i in 0..<count {
            let x = CGFloat(i) * (w + spacing)
            backs[i].frame = CGRect(x: x, y: 0, width: max(w, 1), height: bounds.height)
            backs[i].backgroundColor = NSColor(white: i < deviceChannels ? 0.22 : 0.17, alpha: 1).cgColor
            let v = i < levels.count && !muted.contains(i) ? levels[i] : 0
            let h = bounds.height * MeterStyle.level(v)
            fills[i].frame = CGRect(x: x, y: bounds.height - h, width: max(w, 1), height: h)
            fills[i].backgroundColor = i < deviceChannels
                ? (v >= 0.99 ? NSColor.systemRed : v >= 0.5 ? NSColor.systemYellow : NSColor.systemGreen).cgColor
                : NSColor(red: 0.35, green: 0.55, blue: 0.75, alpha: 1).cgColor
        }
    }
}

struct MeterBars: NSViewRepresentable {
    let values: LiveValues
    let source: MeterBarsView.Source
    let count: Int
    var spacing: CGFloat = 3
    var deviceChannels = Int.max
    var muted: Set<Int> = []

    func makeNSView(context: Context) -> MeterBarsView {
        let v = MeterBarsView()
        apply(v)
        v.attach(values)
        return v
    }

    func updateNSView(_ v: MeterBarsView, context: Context) {
        apply(v)
        v.refresh(values)
    }

    private func apply(_ v: MeterBarsView) {
        v.source = source
        v.spacing = spacing
        v.deviceChannels = deviceChannels
        v.muted = muted
        v.count = count
    }
}

// MARK: - シークバー

final class SeekBarView: LiveLayerView {
    var cueID = 0
    var onSeek: ((Double) -> Void)?
    private let track = LiveLayerView.plainLayer(NSColor(white: 0.3, alpha: 1).cgColor)
    private let fill = LiveLayerView.plainLayer(NSColor.controlAccentColor.cgColor)
    private let knob = LiveLayerView.plainLayer(NSColor.white.cgColor)
    private let elapsed = NSTextField(labelWithString: "")
    private let remaining = NSTextField(labelWithString: "")
    private var dragging: Double?
    private var enabled = false
    private var current = 0.0
    private let barHeight: CGFloat = 14

    override init(frame: NSRect) {
        super.init(frame: frame)
        layer?.masksToBounds = false
        for l in [track, fill] { l.cornerRadius = 2; layer?.addSublayer(l) }
        knob.cornerRadius = 4.5
        layer?.addSublayer(knob)
        for t in [elapsed, remaining] {
            t.font = .monospacedDigitSystemFont(ofSize: 9, weight: .regular)
            t.textColor = .secondaryLabelColor
            addSubview(t)
        }
        remaining.alignment = .right
    }

    required init?(coder: NSCoder) { fatalError() }

    override func refresh(_ values: LiveValues) {
        guard let c = values.cues[cueID] else { return }
        enabled = c.position != nil && !c.stopping
        current = c.position ?? c.progress
        let shown = dragging ?? current
        let w = bounds.width
        let mid = barHeight / 2
        track.frame = CGRect(x: 0, y: mid - 2, width: w, height: 4)
        fill.frame = CGRect(x: 0, y: mid - 2, width: max(0, w * shown), height: 4)
        knob.isHidden = !enabled
        let k: CGFloat = dragging == nil ? 9 : 12
        knob.cornerRadius = k / 2
        knob.frame = CGRect(x: min(max(w * shown - k / 2, 0), w - k), y: mid - k / 2, width: k, height: k)
        let labelsVisible = c.duration > 0
        elapsed.isHidden = !labelsVisible
        remaining.isHidden = !labelsVisible
        if labelsVisible {
            let e = SeekBarView.clock(shown * c.duration)
            let r = "-" + SeekBarView.clock(max(c.duration - shown * c.duration, 0))
            if elapsed.stringValue != e { elapsed.stringValue = e }
            if remaining.stringValue != r { remaining.stringValue = r }
            elapsed.frame = CGRect(x: 0, y: barHeight, width: w / 2, height: 12)
            remaining.frame = CGRect(x: w / 2, y: barHeight, width: w / 2, height: 12)
        }
        toolTip = enabled ? "クリック／ドラッグで再生位置を変える" : nil
    }

    private func fraction(_ event: NSEvent) -> Double {
        let p = convert(event.locationInWindow, from: nil)
        return Double(min(max(p.x / max(bounds.width, 1), 0), 1))
    }

    override func mouseDown(with event: NSEvent) {
        guard enabled else { return }
        dragging = fraction(event)
        if let values { refresh(values) }
    }

    override func mouseDragged(with event: NSEvent) {
        guard enabled, dragging != nil else { return }
        dragging = fraction(event)
        if let values { refresh(values) }
    }

    override func mouseUp(with event: NSEvent) {
        guard enabled, dragging != nil else { return }
        let f = fraction(event)
        onSeek?(f)
        // 実際の位置が追いつくまで少しだけ表示を保つ
        dragging = f
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in self?.dragging = nil }
    }

    static func clock(_ s: Double) -> String {
        guard s.isFinite else { return "--:--" }
        let t = Int(s)
        return String(format: "%d:%02d", t / 60, t % 60)
    }
}

struct SeekBarLayer: NSViewRepresentable {
    let values: LiveValues
    let cueID: Int
    let onSeek: (Double) -> Void

    func makeNSView(context: Context) -> SeekBarView {
        let v = SeekBarView()
        v.cueID = cueID
        v.onSeek = onSeek
        v.attach(values)
        return v
    }

    func updateNSView(_ v: SeekBarView, context: Context) {
        v.cueID = cueID
        v.onSeek = onSeek
        v.refresh(values)
    }
}
