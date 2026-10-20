import Accelerate
import AudioToolbox
import Darwin

enum HostTime {
    static let ticksToSeconds: Double = {
        var tb = mach_timebase_info_data_t()
        mach_timebase_info(&tb)
        return Double(tb.numer) / Double(tb.denom) / 1e9
    }()

    static func now(plus seconds: Double = 0) -> UInt64 {
        mach_absolute_time() + UInt64(max(seconds, 0) / ticksToSeconds)
    }
}

struct VoiceStatus {
    let progress: Double
    let done: Bool
    /// 素材のチャンネルごとのピーク（前回の snapshot 以降。音量・フェード後、マスター前）
    let levels: [Float]
}

/// 再生中の 1 音
final class Voice {
    static let pending = UInt64.max

    let id: Int
    let clip: AudioClip
    /// ルーティング：src[i] チャンネルを dst[i] 出力チャンネルへ
    var src: [Int]
    var dst: [Int]
    var position: Double = 0
    var env: Float
    var envStep: Float
    var volume: Float
    var loop: Bool
    /// 0 = すぐ再生、pending = 開始時刻待ち、それ以外 = この host time に鳴り始める
    var startHost: UInt64
    var started = false
    var done = false
    /// 素材のチャンネルごとのピーク（オーディオスレッドで書き、snapshot で読んで戻す）
    let peaks: UnsafeMutablePointer<Float>

    init(id: Int, clip: AudioClip, routes: [(Int, Int)], volume: Float, env: Float, envStep: Float, loop: Bool, startHost: UInt64) {
        self.id = id
        self.clip = clip
        peaks = .allocate(capacity: max(clip.channelCount, 1))
        peaks.initialize(repeating: 0, count: max(clip.channelCount, 1))
        self.src = routes.map { $0.0 }
        self.dst = routes.map { $0.1 }
        self.volume = volume
        self.env = env
        self.envStep = envStep
        self.loop = loop
        self.startHost = startHost
    }

    deinit { peaks.deallocate() }
}

/// 出力チャンネル数ぶんのバッファへ各ボイスを直接足し込むミキサー。
/// render はオーディオスレッド、それ以外はメインスレッドから呼ぶ。
final class Mixer {
    static let maxChannels = 64
    /// デバイスの ch 数が少なくても、メーターとルーティングはこの ch 数まで扱う
    static let minVisibleChannels = 8
    private static let virtualFrames = 8192

    private let lock: UnsafeMutablePointer<os_unfair_lock>
    private var voices: [Voice] = []
    /// ライブ入力（カメラ・キャプチャ機器・オーディオ入力の音）
    private var liveVoices: [LiveVoice] = []
    private var nextID = 1
    private var sampleRate: Double = 48000
    private var latency: Double = 0
    private var masterVolume: Float = 1
    private let meterPeaks: UnsafeMutablePointer<Float>
    private let muted: UnsafeMutablePointer<Bool>
    /// 録画・配信に送る音（出力の 1 ch 目から tapChannels ch、interleave）。オーディオのスレッドから呼ぶ
    private var tap: (([Float], Int) -> Void)?
    private var tapChannels = 2
    private var toneChannel = -1
    private var tonePhase: Double = 0
    private let outs: UnsafeMutablePointer<UnsafeMutablePointer<Float>?>
    /// デバイスにない ch 用の作業バッファ（音は出さず、メーターだけに使う）
    private let virtualBuffers: [UnsafeMutablePointer<Float>]

    init() {
        lock = .allocate(capacity: 1)
        lock.initialize(to: os_unfair_lock())
        meterPeaks = .allocate(capacity: Mixer.maxChannels)
        meterPeaks.initialize(repeating: 0, count: Mixer.maxChannels)
        muted = .allocate(capacity: Mixer.maxChannels)
        muted.initialize(repeating: false, count: Mixer.maxChannels)
        virtualBuffers = (0..<Mixer.minVisibleChannels).map { _ in
            let p = UnsafeMutablePointer<Float>.allocate(capacity: Mixer.virtualFrames)
            p.initialize(repeating: 0, count: Mixer.virtualFrames)
            return p
        }
        outs = .allocate(capacity: Mixer.maxChannels)
        outs.initialize(repeating: nil, count: Mixer.maxChannels)
        voices.reserveCapacity(512)
    }

    private func locked<T>(_ body: () -> T) -> T {
        os_unfair_lock_lock(lock)
        defer { os_unfair_lock_unlock(lock) }
        return body()
    }

    func configure(sampleRate: Double, latency: Double) {
        locked {
            self.sampleRate = sampleRate
            self.latency = latency
        }
    }

    func setMasterVolume(_ v: Float) { locked { masterVolume = v } }

    func setTap(channels: Int, _ handler: (([Float], Int) -> Void)?) {
        locked {
            tapChannels = max(1, min(channels, Mixer.maxChannels))
            tap = handler
        }
    }

    fileprivate func addLive(source: LiveAudioSource, routes: [(Int, Int)], volume: Float, fadeIn: Double) -> Int {
        locked {
            let id = nextID
            nextID += 1
            let fadeFrames = Float(fadeIn * sampleRate)
            liveVoices.append(LiveVoice(id: id, source: source, routes: routes.filter { $0.0 < source.channels }, volume: volume,
                                        env: fadeFrames >= 1 ? 0 : 1, envStep: fadeFrames >= 1 ? 1 / fadeFrames : 0))
            return id
        }
    }

    /// ライブ入力をまとめて読み、ルーティングどおりに足す（オーディオスレッド）
    private func renderLive(_ v: LiveVoice, frames: Int, outCount: Int, master: Float) {
        let n = min(frames, LiveAudioSource.maxRead)
        v.source.read(frames: n)
        let data = v.source.scratch
        let src = v.src, dst = v.dst
        let gainBase = v.volume * master
        var env = v.env, envStep = v.envStep
        for f in 0..<n {
            if envStep != 0 {
                env += envStep
                if env >= 1 { env = 1; envStep = 0 } else if env <= 0 { env = 0; v.done = true; break }
            }
            let g = Mixer.fadeGain(env) * gainBase
            for r in 0..<src.count {
                let d = dst[r]
                guard d < outCount, let out = outs[d] else { continue }
                out[f] += data[src[r]][f] * g
            }
        }
        v.env = env
        v.envStep = envStep
        // 素材メーター（入力のレベル、音量とフェードの後）
        let level = Mixer.fadeGain(env) * v.volume
        for c in 0..<v.source.channels {
            var peak: Float = 0
            vDSP_maxmgv(data[c], 1, &peak, vDSP_Length(n))
            peak *= level
            if peak > v.peaks[c] { v.peaks[c] = peak }
        }
    }

    /// 出力チャンネルごとのミュート（全キー共通）
    func setMuted(_ channels: Set<Int>) {
        locked {
            for i in 0..<Mixer.maxChannels { muted[i] = channels.contains(i) }
        }
    }

    /// 指定の出力チャンネルに 1kHz のテストトーンを出す（nil で止める）
    func setTestTone(channel: Int?) {
        locked {
            toneChannel = channel ?? -1
            tonePhase = 0
        }
    }

    /// フェードの音量カーブ（等パワーのサイン）。フェード IN の出だしからすぐ聞こえ、重ねて切り替えても音量が落ち込まない
    @inline(__always) static func fadeGain(_ env: Float) -> Float {
        env >= 1 ? 1 : (env <= 0 ? 0 : sinf(env * (Float.pi / 2)))
    }

    func addVoice(clip: AudioClip, routes: [(Int, Int)], volume: Float, fadeIn: Double, loop: Bool, pending: Bool) -> Int? {
        guard clip.frameCount > 1, !routes.isEmpty else { return nil }
        return locked {
            let id = nextID
            nextID += 1
            let fadeFrames = Float(fadeIn * sampleRate)
            let voice = Voice(id: id, clip: clip, routes: routes, volume: volume,
                              env: fadeFrames >= 1 ? 0 : 1,
                              envStep: fadeFrames >= 1 ? 1 / fadeFrames : 0,
                              loop: loop, startHost: pending ? Voice.pending : 0)
            voices.append(voice)
            return id
        }
    }

    /// fade 秒かけて止める（0 ならクリックしない程度の極短フェード）
    func stop(id: Int, fade: Double) {
        locked {
            if let l = liveVoices.first(where: { $0.id == id }), !l.done {
                let frames = Float(max(fade, 0.005) * sampleRate)
                l.envStep = -max(l.env, 0.0001) / frames
                return
            }
            guard let v = voices.first(where: { $0.id == id }), !v.done else { return }
            if !v.started {
                v.done = true
                return
            }
            let frames = Float(max(fade, 0.005) * sampleRate)
            v.envStep = -max(v.env, 0.0001) / frames
        }
    }

    func setStart(id: Int, hostTime: UInt64) {
        locked { voices.first(where: { $0.id == id })?.startHost = hostTime }
    }

    /// 再生位置を seconds へ移す。startHost を渡すと、その host time まで待ってから鳴らす（映像と合わせるため）
    func seek(id: Int, seconds: Double, startHost: UInt64 = 0) {
        locked {
            guard let v = voices.first(where: { $0.id == id }), !v.done else { return }
            let frames = Double(v.clip.frameCount)
            v.position = min(max(seconds * v.clip.sampleRate, 0), frames - 1)
            if startHost != 0 {
                v.started = false
                v.startHost = startHost
            }
        }
    }

    func setVolume(id: Int, volume: Float) {
        locked { liveVoices.first(where: { $0.id == id })?.volume = volume; voices.first(where: { $0.id == id })?.volume = volume }
    }

    func setLoop(id: Int, loop: Bool) {
        locked { voices.first(where: { $0.id == id })?.loop = loop }
    }

    func snapshot() -> [Int: VoiceStatus] {
        locked {
            var out: [Int: VoiceStatus] = [:]
            for v in voices {
                let n = v.clip.channelCount
                let levels = Array(UnsafeBufferPointer(start: v.peaks, count: n))
                v.peaks.update(repeating: 0, count: n)
                out[v.id] = VoiceStatus(progress: v.position / Double(v.clip.frameCount), done: v.done, levels: levels)
            }
            for l in liveVoices {
                let levels = Array(UnsafeBufferPointer(start: l.peaks, count: l.source.channels))
                l.peaks.update(repeating: 0, count: l.source.channels)
                out[l.id] = VoiceStatus(progress: 0, done: l.done, levels: levels)
            }
            return out
        }
    }

    func removeDone() {
        locked {
            voices.removeAll { $0.done }
            liveVoices.removeAll { $0.done }
        }
    }

    /// 前回呼び出し以降の各チャンネルのピーク
    func takeMeters(count: Int) -> [Float] {
        locked {
            let n = min(count, Mixer.maxChannels)
            var out = [Float](repeating: 0, count: n)
            for i in 0..<n {
                out[i] = meterPeaks[i]
                meterPeaks[i] = 0
            }
            return out
        }
    }

    // MARK: - オーディオスレッド

    func render(frameCount: Int, timestamp: UnsafePointer<AudioTimeStamp>, bufferList: UnsafeMutablePointer<AudioBufferList>) {
        let buffers = UnsafeMutableAudioBufferListPointer(bufferList)
        let deviceCount = min(buffers.count, Mixer.maxChannels)
        for i in 0..<buffers.count {
            if let data = buffers[i].mData { memset(data, 0, Int(buffers[i].mDataByteSize)) }
        }
        for i in 0..<deviceCount {
            outs[i] = buffers[i].mData?.assumingMemoryBound(to: Float.self)
        }
        // デバイスが 8ch 未満なら、残りの ch は作業バッファに足し込む（メーター用で音は出ない）
        var outCount = deviceCount
        if deviceCount < Mixer.minVisibleChannels && frameCount <= Mixer.virtualFrames {
            for i in deviceCount..<Mixer.minVisibleChannels {
                memset(virtualBuffers[i], 0, frameCount * MemoryLayout<Float>.size)
                outs[i] = virtualBuffers[i]
            }
            outCount = Mixer.minVisibleChannels
        }

        os_unfair_lock_lock(lock)
        defer { os_unfair_lock_unlock(lock) }

        let ts = timestamp.pointee
        let hostNow: UInt64? = ts.mFlags.contains(.hostTimeValid) ? ts.mHostTime : nil
        let sr = sampleRate
        let master = masterVolume

        for v in voices where !v.done {
            var first = 0
            if !v.started {
                if v.startHost == Voice.pending { continue }
                if v.startHost != 0, let hostNow {
                    let dt = Double(Int64(bitPattern: v.startHost &- hostNow)) * HostTime.ticksToSeconds - latency
                    let offset = Int((dt * sr).rounded())
                    if offset >= frameCount { continue }
                    first = max(0, offset)
                }
                v.started = true
            }
            renderVoice(v, from: first, to: frameCount, outCount: outCount, master: master, outputRate: sr)
        }

        for l in liveVoices where !l.done {
            renderLive(l, frames: frameCount, outCount: outCount, master: master)
        }

        if toneChannel >= 0 && toneChannel < outCount, let out = outs[toneChannel] {
            let inc = 2 * Double.pi * 1000 / sr
            let amp = 0.1 * master
            for f in 0..<frameCount {
                out[f] += Float(sin(tonePhase)) * amp
                tonePhase += inc
            }
            tonePhase = tonePhase.truncatingRemainder(dividingBy: 2 * Double.pi)
        }

        for c in 0..<outCount {
            guard let p = outs[c] else { continue }
            if muted[c] {
                memset(p, 0, frameCount * MemoryLayout<Float>.size)
                continue
            }
            var peak: Float = 0
            vDSP_maxmgv(p, 1, &peak, vDSP_Length(frameCount))
            if peak > meterPeaks[c] { meterPeaks[c] = peak }
        }
        // 録画・配信へ（ミュートとマスター音量のあと）
        if let tap {
            let n = tapChannels
            var block = [Float](repeating: 0, count: frameCount * n)
            for c in 0..<min(n, outCount) {
                guard let p = outs[c] else { continue }
                for f in 0..<frameCount { block[f * n + c] = p[f] }
            }
            tap(block, frameCount)
        }
    }

    private func renderVoice(_ v: Voice, from: Int, to: Int, outCount: Int, master: Float, outputRate: Double) {
        let clip = v.clip
        let frames = clip.frameCount
        let data = clip.channels
        let src = v.src
        let dst = v.dst
        let routes = src.count
        let step = clip.sampleRate / outputRate
        let gainBase = v.volume * master
        let loop = v.loop
        let channelCount = clip.channelCount
        let peaks = v.peaks
        var pos = v.position
        var env = v.env
        var envStep = v.envStep
        var done = false

        var f = from
        while f < to {
            if envStep != 0 {
                env += envStep
                if env >= 1 {
                    env = 1
                    envStep = 0
                } else if env <= 0 {
                    env = 0
                    done = true
                    break
                }
            }
            let i0 = Int(pos)
            let frac = Float(pos - Double(i0))
            let i1 = i0 + 1 < frames ? i0 + 1 : (loop ? 0 : i0)
            let g = Mixer.fadeGain(env) * gainBase
            for r in 0..<routes {
                let d = dst[r]
                guard d < outCount, let out = outs[d] else { continue }
                let s = data[src[r]]
                let a = s[i0]
                out[f] += (a + (s[i1] - a) * frac) * g
            }
            // 素材メーター用：ルーティングに関係なく、素材の各チャンネルの音量（マスター前）
            let level = Mixer.fadeGain(env) * v.volume
            for c in 0..<channelCount {
                let p = abs(data[c][i0]) * level
                if p > peaks[c] { peaks[c] = p }
            }
            pos += step
            if pos >= Double(frames) {
                if loop {
                    pos -= Double(frames)
                } else {
                    done = true
                    break
                }
            }
            f += 1
        }
        v.position = pos
        v.env = env
        v.envStep = envStep
        if done { v.done = true }
    }
}

/// ライブ入力の音（リングバッファから読む）
final class LiveVoice {
    let id: Int
    let source: LiveAudioSource
    let src: [Int]
    let dst: [Int]
    var env: Float
    var envStep: Float
    var volume: Float
    var done = false
    let peaks: UnsafeMutablePointer<Float>

    init(id: Int, source: LiveAudioSource, routes: [(Int, Int)], volume: Float, env: Float, envStep: Float) {
        self.id = id
        self.source = source
        src = routes.map { $0.0 }
        dst = routes.map { $0.1 }
        self.volume = volume
        self.env = env
        self.envStep = envStep
        peaks = .allocate(capacity: max(source.channels, 1))
        peaks.initialize(repeating: 0, count: max(source.channels, 1))
    }

    deinit { peaks.deallocate() }
}

extension Mixer {
    func addLiveVoice(source: LiveAudioSource, routes: [(Int, Int)], volume: Float, fadeIn: Double) -> Int {
        addLive(source: source, routes: routes, volume: volume, fadeIn: fadeIn)
    }
}
