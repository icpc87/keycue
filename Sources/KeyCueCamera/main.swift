import CoreMediaIO
import CoreVideo
import Foundation
import IOKit.audio
import os.log

// KeyCue の仮想カメラ（Camera Extension）。
// KeyCue 本体が「入力（sink）」のストリームにコマを入れ、それをそのまま「出力（source）」としてほかのアプリ（Zoom・Teams・OBS など）に出す。
// KeyCue から何も来ていないあいだは黒いコマを出し続ける（受け取る側の映像が止まらないように）。

private let log = Logger(subsystem: "jp.ic2.keycue.camera", category: "camera")

enum Camera {
    static let width: Int32 = 1920
    static let height: Int32 = 1080
    static let fps: Int32 = 30
    // 本体と同じ値（VirtualCamera.swift）
    static let deviceID = UUID(uuidString: "6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D41")!
    static let sourceStreamID = UUID(uuidString: "6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D42")!
    static let sinkStreamID = UUID(uuidString: "6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D43")!
}

final class DeviceSource: NSObject, CMIOExtensionDeviceSource {
    private(set) var device: CMIOExtensionDevice!
    private var sourceStream: StreamSource!
    private var sinkStream: SinkSource!
    let format: CMFormatDescription
    private let queue = DispatchQueue(label: "jp.ic2.keycue.camera.frames", qos: .userInteractive)
    private var timer: DispatchSourceTimer?
    private var pool: CVPixelBufferPool?
    private var sourceRunning = 0
    private var sinkRunning = false
    private var lastSinkFrame: UInt64 = 0
    private var sinkClient: CMIOExtensionClient?
    private var ticks = 0
    private var forwarded = 0

    init(localizedName: String) {
        var fd: CMFormatDescription?
        CMVideoFormatDescriptionCreate(allocator: kCFAllocatorDefault, codecType: kCVPixelFormatType_32BGRA,
                                       width: Camera.width, height: Camera.height, extensions: nil, formatDescriptionOut: &fd)
        format = fd!
        super.init()
        device = CMIOExtensionDevice(localizedName: localizedName, deviceID: Camera.deviceID, legacyDeviceID: Camera.deviceID.uuidString, source: self)
        sourceStream = StreamSource(owner: self, name: "KeyCue", id: Camera.sourceStreamID, direction: .source)
        sinkStream = SinkSource(owner: self, name: "KeyCue.Sink", id: Camera.sinkStreamID)
        do {
            try device.addStream(sourceStream.stream)
            try device.addStream(sinkStream.stream)
        } catch {
            fatalError("ストリームを足せません: \(error.localizedDescription)")
        }
        let attrs: [String: Any] = [kCVPixelBufferWidthKey as String: Camera.width, kCVPixelBufferHeightKey as String: Camera.height,
                                    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                                    kCVPixelBufferIOSurfacePropertiesKey as String: [:]]
        CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, attrs as CFDictionary, &pool)
    }

    var availableProperties: Set<CMIOExtensionProperty> { [.deviceTransportType, .deviceModel] }

    func deviceProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionDeviceProperties {
        let p = CMIOExtensionDeviceProperties(dictionary: [:])
        if properties.contains(.deviceTransportType) { p.transportType = kIOAudioDeviceTransportTypeVirtual }
        if properties.contains(.deviceModel) { p.model = "KeyCue" }
        return p
    }

    func setDeviceProperties(_ deviceProperties: CMIOExtensionDeviceProperties) throws {}

    // MARK: 出力（ほかのアプリが見る側）

    func startSource() {
        log.notice("startSource")
        queue.sync {
            sourceRunning += 1
            guard timer == nil else { return }
            let t = DispatchSource.makeTimerSource(flags: .strict, queue: queue)
            t.schedule(deadline: .now(), repeating: 1.0 / Double(Camera.fps), leeway: .milliseconds(2))
            t.setEventHandler { [weak self] in self?.placeholderTick() }
            t.resume()
            timer = t
        }
    }

    func stopSource() {
        log.notice("stopSource")
        queue.sync {
            sourceRunning = max(0, sourceRunning - 1)
            if sourceRunning == 0 {
                timer?.cancel()
                timer = nil
            }
        }
    }

    /// KeyCue からしばらく来ていなければ黒を出す
    private func placeholderTick() {
        let now = mach_absolute_time()
        ticks += 1
        if ticks % 60 == 1 {
            log.debug("tick sinkRunning=\(self.sinkRunning, privacy: .public) age=\(Self.seconds(now &- self.lastSinkFrame), privacy: .public) forwarded=\(self.forwarded, privacy: .public) sources=\(self.sourceRunning, privacy: .public)")
        }
        if sinkRunning && Self.seconds(now &- lastSinkFrame) < 0.5 { return }
        guard let pool else { return }
        var pb: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &pb)
        guard let pb else { return }
        CVPixelBufferLockBaseAddress(pb, [])
        if let base = CVPixelBufferGetBaseAddress(pb) {
            let rows = CVPixelBufferGetHeight(pb), bpr = CVPixelBufferGetBytesPerRow(pb)
            for y in 0..<rows {
                let row = base.advanced(by: y * bpr).assumingMemoryBound(to: UInt32.self)
                for x in 0..<Int(Camera.width) { row[x] = 0xFF00_0000 }   // BGRA の黒（不透明）
            }
        }
        CVPixelBufferUnlockBaseAddress(pb, [])
        send(pb, hostTime: now)
    }

    private func send(_ pb: CVPixelBuffer, hostTime: UInt64) {
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: Camera.fps),
                                        presentationTimeStamp: CMClockMakeHostTimeFromSystemUnits(hostTime),
                                        decodeTimeStamp: .invalid)
        var sb: CMSampleBuffer?
        CMSampleBufferCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb, dataReady: true, makeDataReadyCallback: nil,
                                           refcon: nil, formatDescription: format, sampleTiming: &timing, sampleBufferOut: &sb)
        guard let sb else { return }
        sourceStream.stream.send(sb, discontinuity: [], hostTimeInNanoseconds: UInt64(Self.seconds(hostTime) * Double(NSEC_PER_SEC)))
    }

    // MARK: 入力（KeyCue 本体が入れる側）

    func startSink(client: CMIOExtensionClient) {
        log.notice("startSink")
        queue.sync {
            sinkClient = client
            sinkRunning = true
        }
        consume()
    }

    func stopSink() {
        log.notice("stopSink")
        queue.sync {
            sinkRunning = false
            sinkClient = nil
        }
    }

    private func consume() {
        guard let client = queue.sync(execute: { sinkRunning ? sinkClient : nil }) else { return }
        sinkStream.stream.consumeSampleBuffer(from: client) { [weak self] sb, sequence, _, hasMore, error in
            guard let self else { return }
            if let sb {
                self.queue.async {
                    self.lastSinkFrame = mach_absolute_time()
                    self.forwarded += 1
                    if self.sourceRunning > 0 {
                        self.sourceStream.stream.send(sb, discontinuity: [], hostTimeInNanoseconds: UInt64(Self.seconds(self.lastSinkFrame) * Double(NSEC_PER_SEC)))
                    }
                }
                let output = CMIOExtensionScheduledOutput(sequenceNumber: sequence, hostTimeInNanoseconds: UInt64(Self.seconds(mach_absolute_time()) * Double(NSEC_PER_SEC)))
                self.sinkStream.stream.notifyScheduledOutputChanged(output)
            }
            if error != nil && sb == nil {
                // まだ何も入っていない：少し待って読み直す
                DispatchQueue.global(qos: .userInteractive).asyncAfter(deadline: .now() + 0.005) { self.consume() }
            } else {
                self.consume()
            }
            _ = hasMore
        }
    }

    private static let timebase: mach_timebase_info_data_t = {
        var t = mach_timebase_info_data_t()
        mach_timebase_info(&t)
        return t
    }()

    static func seconds(_ ticks: UInt64) -> Double {
        Double(ticks) * Double(timebase.numer) / Double(timebase.denom) / 1e9
    }
}

/// ほかのアプリが見る出力
final class StreamSource: NSObject, CMIOExtensionStreamSource {
    private(set) var stream: CMIOExtensionStream!
    private unowned let owner: DeviceSource

    init(owner: DeviceSource, name: String, id: UUID, direction: CMIOExtensionStream.Direction) {
        self.owner = owner
        super.init()
        stream = CMIOExtensionStream(localizedName: name, streamID: id, direction: direction, clockType: .hostTime, source: self)
    }

    var formats: [CMIOExtensionStreamFormat] {
        let d = CMTime(value: 1, timescale: Camera.fps)
        return [CMIOExtensionStreamFormat(formatDescription: owner.format, maxFrameDuration: d, minFrameDuration: d, validFrameDurations: nil)]
    }

    var activeFormatIndex: Int = 0

    var availableProperties: Set<CMIOExtensionProperty> { [.streamActiveFormatIndex, .streamFrameDuration] }

    func streamProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionStreamProperties {
        let p = CMIOExtensionStreamProperties(dictionary: [:])
        if properties.contains(.streamActiveFormatIndex) { p.activeFormatIndex = 0 }
        if properties.contains(.streamFrameDuration) {
            let d = CMTime(value: 1, timescale: Camera.fps)
            p.frameDuration = d
        }
        return p
    }

    func setStreamProperties(_ streamProperties: CMIOExtensionStreamProperties) throws {
        if let i = streamProperties.activeFormatIndex { activeFormatIndex = i }
    }

    func authorizedToStartStream(for client: CMIOExtensionClient) -> Bool { true }

    func startStream() throws { owner.startSource() }

    func stopStream() throws { owner.stopSource() }
}

/// KeyCue 本体がコマを入れる入力
final class SinkSource: NSObject, CMIOExtensionStreamSource {
    private(set) var stream: CMIOExtensionStream!
    private unowned let owner: DeviceSource
    private var client: CMIOExtensionClient?

    init(owner: DeviceSource, name: String, id: UUID) {
        self.owner = owner
        super.init()
        stream = CMIOExtensionStream(localizedName: name, streamID: id, direction: .sink, clockType: .hostTime, source: self)
    }

    var formats: [CMIOExtensionStreamFormat] {
        let d = CMTime(value: 1, timescale: Camera.fps)
        return [CMIOExtensionStreamFormat(formatDescription: owner.format, maxFrameDuration: d, minFrameDuration: d, validFrameDurations: nil)]
    }

    var activeFormatIndex: Int = 0

    var availableProperties: Set<CMIOExtensionProperty> {
        [.streamActiveFormatIndex, .streamFrameDuration, .streamSinkBufferQueueSize, .streamSinkBuffersRequiredForStartup,
         .streamSinkBufferUnderrunCount, .streamSinkEndOfData]
    }

    func streamProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionStreamProperties {
        let p = CMIOExtensionStreamProperties(dictionary: [:])
        if properties.contains(.streamActiveFormatIndex) { p.activeFormatIndex = 0 }
        if properties.contains(.streamFrameDuration) { p.frameDuration = CMTime(value: 1, timescale: Camera.fps) }
        if properties.contains(.streamSinkBufferQueueSize) { p.sinkBufferQueueSize = 1 }
        if properties.contains(.streamSinkBuffersRequiredForStartup) { p.sinkBuffersRequiredForStartup = 1 }
        return p
    }

    func setStreamProperties(_ streamProperties: CMIOExtensionStreamProperties) throws {
        if let i = streamProperties.activeFormatIndex { activeFormatIndex = i }
    }

    func authorizedToStartStream(for client: CMIOExtensionClient) -> Bool {
        self.client = client
        return true
    }

    func startStream() throws {
        guard let client else { throw NSError(domain: "KeyCueCamera", code: 1) }
        owner.startSink(client: client)
    }

    func stopStream() throws { owner.stopSink() }
}

final class ProviderSource: NSObject, CMIOExtensionProviderSource {
    private(set) var provider: CMIOExtensionProvider!
    private var deviceSource: DeviceSource!

    init(clientQueue: DispatchQueue?) {
        super.init()
        provider = CMIOExtensionProvider(source: self, clientQueue: clientQueue)
        deviceSource = DeviceSource(localizedName: "KeyCue")
        do {
            try provider.addDevice(deviceSource.device)
        } catch {
            fatalError("カメラを足せません: \(error.localizedDescription)")
        }
    }

    func connect(to client: CMIOExtensionClient) throws {}

    func disconnect(from client: CMIOExtensionClient) {}

    var availableProperties: Set<CMIOExtensionProperty> { [.providerManufacturer] }

    func providerProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionProviderProperties {
        let p = CMIOExtensionProviderProperties(dictionary: [:])
        if properties.contains(.providerManufacturer) { p.manufacturer = "KeyCue" }
        return p
    }

    func setProviderProperties(_ providerProperties: CMIOExtensionProviderProperties) throws {}
}

let providerSource = ProviderSource(clientQueue: nil)
CMIOExtensionProvider.startService(provider: providerSource.provider)
log.info("KeyCue camera started")
CFRunLoopRun()
