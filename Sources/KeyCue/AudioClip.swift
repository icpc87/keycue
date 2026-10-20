import Accelerate
import AVFoundation

/// メモリ上に展開した音声（Float32・チャンネル別）
final class AudioClip {
    let buffer: AVAudioPCMBuffer
    let channels: UnsafePointer<UnsafeMutablePointer<Float>>
    let channelCount: Int
    let frameCount: Int
    let sampleRate: Double
    /// 波形表示用のピーク値（0...1）
    let peaks: [Float]

    var duration: Double { Double(frameCount) / sampleRate }

    init(buffer: AVAudioPCMBuffer) {
        self.buffer = buffer
        channels = UnsafePointer(buffer.floatChannelData!)
        channelCount = Int(buffer.format.channelCount)
        frameCount = Int(buffer.frameLength)
        sampleRate = buffer.format.sampleRate
        peaks = AudioClip.computePeaks(channels, channelCount: channelCount, frames: frameCount)
    }

    private static func computePeaks(_ data: UnsafePointer<UnsafeMutablePointer<Float>>, channelCount: Int, frames: Int) -> [Float] {
        guard frames > 0 else { return [] }
        let buckets = min(1500, frames)
        var out = [Float](repeating: 0, count: buckets)
        for b in 0..<buckets {
            let start = b * frames / buckets
            let end = max(start + 1, (b + 1) * frames / buckets)
            var peak: Float = 0
            for c in 0..<channelCount {
                var v: Float = 0
                vDSP_maxmgv(data[c] + start, 1, &v, vDSP_Length(end - start))
                peak = max(peak, v)
            }
            out[b] = min(peak, 1)
        }
        return out
    }
}

/// ファイルの中身（音声・映像の有無など）
struct MediaProbe {
    var hasAudio = false
    var hasVideo = false
    var videoSize: CGSize = .zero
    var duration: Double = 0
    /// 全音声トラックのチャンネル数の合計（トラック 1 の ch, トラック 2 の ch… の順に並ぶ）
    var sourceChannels = 0
    var audioTracks = 0
    var sourceSampleRate: Double = 0
    var fileSize: Int64 = 0
}

enum AudioLoader {
    enum LoadError: LocalizedError {
        case tooLarge
        case failed(String)

        var errorDescription: String? {
            switch self {
            case .tooLarge: return "音声が長すぎてメモリに展開できません"
            case .failed(let s): return s
            }
        }
    }

    /// メモリに展開する上限（これを超える動画は AVPlayer 側で音を出す）
    static let maxBytes = 1_500_000_000

    static func makeFormat(sampleRate: Double, channels: Int, layout: AVAudioChannelLayout? = nil) -> AVAudioFormat? {
        if let layout, Int(layout.channelCount) == channels {
            return AVAudioFormat(standardFormatWithSampleRate: sampleRate, channelLayout: layout)
        }
        if channels <= 2 {
            return AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: AVAudioChannelCount(max(channels, 1)))
        }
        guard let discrete = AVAudioChannelLayout(layoutTag: kAudioChannelLayoutTag_DiscreteInOrder | UInt32(channels)) else { return nil }
        return AVAudioFormat(standardFormatWithSampleRate: sampleRate, channelLayout: discrete)
    }

    static func probe(url: URL) async throws -> MediaProbe {
        var p = MediaProbe()
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        p.fileSize = (attrs[.size] as? NSNumber)?.int64Value ?? 0

        let asset = AVURLAsset(url: url)
        p.duration = try await asset.load(.duration).seconds
        if let video = try await asset.loadTracks(withMediaType: .video).first {
            p.hasVideo = true
            let (size, transform) = try await video.load(.naturalSize, .preferredTransform)
            let r = CGRect(origin: .zero, size: size).applying(transform)
            p.videoSize = CGSize(width: abs(r.width), height: abs(r.height))
        }
        for audio in try await asset.loadTracks(withMediaType: .audio) {
            p.hasAudio = true
            p.audioTracks += 1
            let descs = try await audio.load(.formatDescriptions)
            if let d = descs.first, let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(d)?.pointee {
                p.sourceChannels += Int(asbd.mChannelsPerFrame)
                if p.sourceSampleRate == 0 { p.sourceSampleRate = asbd.mSampleRate }
            }
        }
        return p
    }

    /// 音声をデコードして targetRate に変換する
    static func loadClip(url: URL, targetRate: Double, probe: MediaProbe) async throws -> AudioClip {
        let estimate = probe.duration * max(probe.sourceSampleRate, targetRate) * Double(max(probe.sourceChannels, 1)) * 4
        if estimate > Double(maxBytes) { throw LoadError.tooLarge }

        let converted: AVAudioPCMBuffer
        if !probe.hasVideo, probe.audioTracks <= 1, let file = try? AVAudioFile(forReading: url) {
            guard let buf = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
                throw LoadError.failed("バッファを確保できません")
            }
            try file.read(into: buf)
            converted = try convert(buf, to: targetRate)
        } else {
            // 動画や、モノラルトラックを複数持つ mov など：全音声トラックを順にチャンネルとして並べる
            let asset = AVURLAsset(url: url)
            let tracks = try await asset.loadTracks(withMediaType: .audio)
            guard !tracks.isEmpty else { throw LoadError.failed("音声トラックがありません") }
            var parts: [AVAudioPCMBuffer] = []
            for track in tracks {
                parts.append(try convert(try readTrack(asset: asset, track: track), to: targetRate))
            }
            converted = try combine(parts, sampleRate: targetRate)
        }
        guard converted.frameLength > 1 else { throw LoadError.failed("音声が空です") }
        return AudioClip(buffer: converted)
    }

    /// 複数トラックのバッファを 1 つの多チャンネルバッファにまとめる（短いトラックは無音で埋める）
    private static func combine(_ parts: [AVAudioPCMBuffer], sampleRate: Double) throws -> AVAudioPCMBuffer {
        if parts.count == 1 { return parts[0] }
        let channels = parts.reduce(0) { $0 + Int($1.format.channelCount) }
        let frames = parts.map { Int($0.frameLength) }.max() ?? 0
        guard let format = makeFormat(sampleRate: sampleRate, channels: channels),
              let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(frames, 1))) else {
            throw LoadError.failed("バッファを確保できません")
        }
        out.frameLength = AVAudioFrameCount(frames)
        let dst = out.floatChannelData!
        var c = 0
        for part in parts {
            let n = Int(part.frameLength)
            for pc in 0..<Int(part.format.channelCount) {
                memset(dst[c], 0, frames * MemoryLayout<Float>.size)
                memcpy(dst[c], part.floatChannelData![pc], n * MemoryLayout<Float>.size)
                c += 1
            }
        }
        return out
    }

    private static func readTrack(asset: AVURLAsset, track: AVAssetTrack) throws -> AVAudioPCMBuffer {
        let reader = try AVAssetReader(asset: asset)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
        output.alwaysCopiesSampleData = false
        reader.add(output)
        guard reader.startReading() else {
            throw LoadError.failed(reader.error?.localizedDescription ?? "読み込みに失敗しました")
        }

        var samples: [Float] = []
        var channels = 0
        var rate: Double = 0
        while let sb = output.copyNextSampleBuffer() {
            if channels == 0, let fd = CMSampleBufferGetFormatDescription(sb),
               let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(fd)?.pointee {
                channels = Int(asbd.mChannelsPerFrame)
                rate = asbd.mSampleRate
            }
            guard let block = CMSampleBufferGetDataBuffer(sb) else { continue }
            let length = CMBlockBufferGetDataLength(block)
            let old = samples.count
            samples.append(contentsOf: repeatElement(0, count: length / 4))
            samples.withUnsafeMutableBytes { raw in
                _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: raw.baseAddress! + old * 4)
            }
        }
        if reader.status == .failed {
            throw LoadError.failed(reader.error?.localizedDescription ?? "読み込みに失敗しました")
        }
        guard channels > 0, rate > 0, let format = makeFormat(sampleRate: rate, channels: channels) else {
            throw LoadError.failed("音声フォーマットを判別できません")
        }
        let frames = samples.count / channels
        guard let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(frames, 1))) else {
            throw LoadError.failed("バッファを確保できません")
        }
        buf.frameLength = AVAudioFrameCount(frames)
        let dst = buf.floatChannelData!
        samples.withUnsafeBufferPointer { src in
            var zero: Float = 0
            for c in 0..<channels {
                vDSP_vsadd(src.baseAddress! + c, vDSP_Stride(channels), &zero, dst[c], 1, vDSP_Length(frames))
            }
        }
        return buf
    }

    static func convert(_ buffer: AVAudioPCMBuffer, to rate: Double) throws -> AVAudioPCMBuffer {
        let inFormat = buffer.format
        if abs(inFormat.sampleRate - rate) < 0.5 && inFormat.commonFormat == .pcmFormatFloat32 && !inFormat.isInterleaved {
            return buffer
        }
        guard let outFormat = makeFormat(sampleRate: rate, channels: Int(inFormat.channelCount), layout: inFormat.channelLayout),
              let converter = AVAudioConverter(from: inFormat, to: outFormat) else {
            throw LoadError.failed("サンプルレートを変換できません")
        }
        converter.sampleRateConverterQuality = AVAudioQuality.max.rawValue
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * rate / inFormat.sampleRate) + 4096
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: capacity) else {
            throw LoadError.failed("バッファを確保できません")
        }
        var given = false
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, inputStatus in
            if given {
                inputStatus.pointee = .endOfStream
                return nil
            }
            given = true
            inputStatus.pointee = .haveData
            return buffer
        }
        if status == .error { throw LoadError.failed(error?.localizedDescription ?? "変換に失敗しました") }
        return out
    }
}
