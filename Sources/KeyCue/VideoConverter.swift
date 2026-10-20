import AVFoundation
import CoreVideo
import CryptoKit

/// いまの macOS では再生できない動画（QuickTime の「アニメーション」形式など）を、
/// 透明部分（アルファ）を残したまま ProRes 4444 に変換して使う。結果は覚えておき、2 回目からはすぐ開く
enum VideoConverter {
    enum ConvertError: LocalizedError {
        case unsupported(String)
        case failed(String)
        var errorDescription: String? {
            switch self {
            case .unsupported(let codec): return "この動画の形式（\(codec)）は読めません。ProRes 4444 や H.264 などで書き出してください"
            case .failed(let message): return "動画を変換できませんでした（\(message)）"
            }
        }
    }

    private static let folder: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("KeyCue/Converted", isDirectory: true)
    }()

    /// 映像があるのにこの Mac で再生できない（変換が要る）なら、その形式の 4 文字コード
    static func undecodableCodec(_ url: URL) async -> String? {
        let asset = AVURLAsset(url: url)
        guard let track = try? await asset.loadTracks(withMediaType: .video).first else { return nil }
        if (try? await track.load(.isDecodable)) == true { return nil }
        guard let format = try? await track.load(.formatDescriptions).first else { return "?" }
        return fourCC(CMFormatDescriptionGetMediaSubType(format))
    }

    static func fourCC(_ c: FourCharCode) -> String {
        String(bytes: [UInt8(c >> 24 & 255), UInt8(c >> 16 & 255), UInt8(c >> 8 & 255), UInt8(c & 255)], encoding: .ascii) ?? "?"
    }

    /// 元ファイル（場所・大きさ・更新日時）ごとの変換先
    static func convertedURL(for src: URL) -> URL {
        let attrs = try? FileManager.default.attributesOfItem(atPath: src.path)
        let size = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
        let date = (attrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        let digest = SHA256.hash(data: Data("\(src.path)|\(size)|\(date)".utf8)).prefix(10).map { String(format: "%02x", $0) }.joined()
        return folder.appendingPathComponent("\(src.deletingPathExtension().lastPathComponent)-\(digest).mov")
    }

    /// 再生に使うファイル。そのまま再生できればそれ自身、できなければ変換したもの
    static func playable(_ src: URL, progress: @escaping (Double) -> Void) async throws -> URL {
        guard let codec = await undecodableCodec(src) else { return src }
        let dst = convertedURL(for: src)
        if FileManager.default.fileExists(atPath: dst.path) { return dst }
        guard codec == "rle " else { throw ConvertError.unsupported(codec.trimmingCharacters(in: .whitespaces)) }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let tmp = dst.deletingLastPathComponent().appendingPathComponent(".\(UUID().uuidString).mov")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try await convertAnimation(src, to: tmp, progress: progress)
        try FileManager.default.moveItem(at: tmp, to: dst)
        return dst
    }

    // MARK: - アニメーション形式 → ProRes 4444

    private static func convertAnimation(_ src: URL, to dst: URL, progress: @escaping (Double) -> Void) async throws {
        let asset = AVURLAsset(url: src)
        guard let vTrack = try await asset.loadTracks(withMediaType: .video).first,
              let format = try await vTrack.load(.formatDescriptions).first else { throw ConvertError.failed("映像がありません") }
        let dims = CMVideoFormatDescriptionGetDimensions(format)
        let width = Int(dims.width), height = Int(dims.height)
        let depth = (CMFormatDescriptionGetExtension(format, extensionKey: kCMFormatDescriptionExtension_Depth) as? NSNumber)?.intValue ?? 32
        guard depth == 32 || depth == 24 else { throw ConvertError.unsupported("アニメーション \(depth) ビット") }
        let transform = try await vTrack.load(.preferredTransform)
        let duration = try await asset.load(.duration).seconds
        let aTrack = try await asset.loadTracks(withMediaType: .audio).first
        let aFormat = try await aTrack?.load(.formatDescriptions).first

        // 元のデータはそのまま取り出す（映像は自分で展開、音はそのまま移す）
        let reader = try AVAssetReader(asset: asset)
        let vOut = AVAssetReaderTrackOutput(track: vTrack, outputSettings: nil)
        vOut.alwaysCopiesSampleData = false
        reader.add(vOut)
        var aOut: AVAssetReaderTrackOutput?
        if let aTrack {
            let o = AVAssetReaderTrackOutput(track: aTrack, outputSettings: nil)
            reader.add(o)
            aOut = o
        }

        let writer = try AVAssetWriter(outputURL: dst, fileType: .mov)
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.proRes4444,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
        ])
        vIn.expectsMediaDataInRealTime = false
        vIn.transform = transform
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vIn, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
        ])
        writer.add(vIn)
        var aIn: AVAssetWriterInput?
        if aOut != nil {
            let i = AVAssetWriterInput(mediaType: .audio, outputSettings: nil, sourceFormatHint: aFormat)
            i.expectsMediaDataInRealTime = false
            writer.add(i)
            aIn = i
        }

        guard reader.startReading() else { throw ConvertError.failed(reader.error?.localizedDescription ?? "読み出せません") }
        guard writer.startWriting() else { throw ConvertError.failed(writer.error?.localizedDescription ?? "書き出せません") }
        writer.startSession(atSourceTime: .zero)

        let decoder = AnimationDecoder(width: width, height: height, depth: depth)
        let group = DispatchGroup()
        var failure: Error?

        group.enter()
        let vQueue = DispatchQueue(label: "KeyCue.convert.video")
        vIn.requestMediaDataWhenReady(on: vQueue) {
            while vIn.isReadyForMoreMediaData {
                guard let sb = vOut.copyNextSampleBuffer() else {
                    vIn.markAsFinished()
                    group.leave()
                    return
                }
                let count = CMSampleBufferGetNumSamples(sb)
                guard count > 0, let block = CMSampleBufferGetDataBuffer(sb) else { continue }
                var offset = 0
                for i in 0..<count {
                    let size = CMSampleBufferGetSampleSize(sb, at: i)
                    var timing = CMSampleTimingInfo()
                    CMSampleBufferGetSampleTimingInfo(sb, at: i, timingInfoOut: &timing)
                    var data = Data(count: size)
                    data.withUnsafeMutableBytes { p in _ = CMBlockBufferCopyDataBytes(block, atOffset: offset, dataLength: size, destination: p.baseAddress!) }
                    offset += size
                    decoder.decode(data)
                    guard let pool = adaptor.pixelBufferPool else { continue }
                    var pb: CVPixelBuffer?
                    CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
                    guard let pb else { continue }
                    decoder.copy(to: pb)
                    if !adaptor.append(pb, withPresentationTime: timing.presentationTimeStamp) {
                        failure = writer.error
                        reader.cancelReading()
                        vIn.markAsFinished()
                        group.leave()
                        return
                    }
                    if duration > 0 { progress(min(timing.presentationTimeStamp.seconds / duration, 1)) }
                }
            }
        }

        if let aOut, let aIn {
            group.enter()
            let aQueue = DispatchQueue(label: "KeyCue.convert.audio")
            aIn.requestMediaDataWhenReady(on: aQueue) {
                while aIn.isReadyForMoreMediaData {
                    guard let sb = aOut.copyNextSampleBuffer() else {
                        aIn.markAsFinished()
                        group.leave()
                        return
                    }
                    aIn.append(sb)
                }
            }
        }

        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in group.notify(queue: .global()) { c.resume() } }
        if let failure { writer.cancelWriting(); throw ConvertError.failed(failure.localizedDescription) }
        if reader.status == .failed { writer.cancelWriting(); throw ConvertError.failed(reader.error?.localizedDescription ?? "読み出しに失敗") }
        await writer.finishWriting()
        if writer.status != .completed { throw ConvertError.failed(writer.error?.localizedDescription ?? "書き出しに失敗") }
    }
}

/// QuickTime の「アニメーション」（rle ）形式の展開。前のコマとの差分なので、1 枚の絵を持ち続けて書き換える
final class AnimationDecoder {
    let width: Int
    let height: Int
    private let depth: Int
    /// BGRA（アルファはそのまま）
    private var pixels: [UInt8]

    init(width: Int, height: Int, depth: Int) {
        self.width = width
        self.height = height
        self.depth = depth
        pixels = [UInt8](repeating: 0, count: width * height * 4)
        if depth == 24 { for i in stride(from: 3, to: pixels.count, by: 4) { pixels[i] = 255 } }
    }

    func decode(_ data: Data) {
        data.withUnsafeBytes { raw in
            let s = raw.bindMemory(to: UInt8.self)
            let n = s.count
            // 8 バイトに満たないコマは「前と同じ」
            guard n >= 8 else { return }
            var p = 4   // 先頭 4 バイトは大きさ
            func u8() -> Int { guard p < n else { return 0 }; defer { p += 1 }; return Int(s[p]) }
            func u16() -> Int { u8() << 8 | u8() }
            let header = u16()
            var startLine = 0, lines = height
            if header & 0x0008 != 0 {
                guard n >= 14 else { return }
                startLine = u16(); p += 2
                lines = u16(); p += 2
                guard startLine + lines <= height else { return }
            }
            let bpp = depth == 32 ? 4 : 3
            pixels.withUnsafeMutableBufferPointer { out in
                for line in startLine..<(startLine + lines) {
                    guard p < n else { return }
                    let rowStart = line * width
                    var x = u8() - 1          // 行の始めで飛ばす画素数 + 1
                    while p < n {
                        let code = Int(Int8(bitPattern: UInt8(u8())))
                        if code == -1 { break }               // 行の終わり
                        if code == 0 {
                            x += u8() - 1                       // さらに飛ばす
                        } else if code < 0 {
                            // 同じ色が -code 個続く
                            guard p + bpp <= n else { return }
                            let (a, r, g, b) = bpp == 4 ? (s[p], s[p + 1], s[p + 2], s[p + 3]) : (255, s[p], s[p + 1], s[p + 2])
                            p += bpp
                            for _ in 0..<(-code) {
                                if x >= 0 && x < width {
                                    let o = (rowStart + x) * 4
                                    out[o] = b; out[o + 1] = g; out[o + 2] = r; out[o + 3] = a
                                }
                                x += 1
                            }
                        } else {
                            // code 個の画素がそのまま並ぶ
                            guard p + bpp * code <= n else { return }
                            for _ in 0..<code {
                                if x >= 0 && x < width {
                                    let o = (rowStart + x) * 4
                                    if bpp == 4 {
                                        out[o + 3] = s[p]; out[o + 2] = s[p + 1]; out[o + 1] = s[p + 2]; out[o] = s[p + 3]
                                    } else {
                                        out[o + 2] = s[p]; out[o + 1] = s[p + 1]; out[o] = s[p + 2]; out[o + 3] = 255
                                    }
                                }
                                p += bpp
                                x += 1
                            }
                        }
                    }
                }
            }
        }
    }

    func copy(to pb: CVPixelBuffer) {
        CVPixelBufferLockBaseAddress(pb, [])
        defer { CVPixelBufferUnlockBaseAddress(pb, []) }
        guard let base = CVPixelBufferGetBaseAddress(pb) else { return }
        let bpr = CVPixelBufferGetBytesPerRow(pb)
        pixels.withUnsafeBytes { src in
            for y in 0..<height {
                memcpy(base + y * bpr, src.baseAddress! + y * width * 4, width * 4)
            }
        }
    }
}
