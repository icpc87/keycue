import AVFoundation
import CoreAudio

/// 選んだ出力デバイスの全チャンネルへミキサーをつなぐ
final class AudioEngine {
    let mixer = Mixer()
    private var engine = AVAudioEngine()
    private var configObserver: NSObjectProtocol?

    private(set) var channelCount = 2
    private(set) var sampleRate: Double = 48000
    private(set) var deviceName = ""
    private(set) var deviceUID: String?
    var onConfigurationChange: (() -> Void)?

    func start(deviceUID requested: String?) throws {
        stop()
        engine = AVAudioEngine()
        let output = engine.outputNode

        let devices = AudioDeviceList.outputDevices()
        let defaultID = AudioDeviceList.defaultOutputDeviceID()
        let device = devices.first { $0.uid == requested } ?? devices.first { $0.id == defaultID }
        if let device, let unit = output.audioUnit {
            var id = device.id
            let status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                              &id, UInt32(MemoryLayout<AudioDeviceID>.size))
            if status != noErr {
                throw NSError(domain: NSOSStatusErrorDomain, code: Int(status),
                              userInfo: [NSLocalizedDescriptionKey: "出力デバイスを設定できません（\(status)）"])
            }
        }

        let hardware = output.outputFormat(forBus: 0)
        sampleRate = hardware.sampleRate > 0 ? hardware.sampleRate : 48000
        channelCount = max(1, min(Mixer.maxChannels, device?.channels ?? Int(hardware.channelCount)))
        deviceName = device?.name ?? "システム既定"
        deviceUID = device?.uid

        guard let format = AudioLoader.makeFormat(sampleRate: sampleRate, channels: channelCount) else {
            throw NSError(domain: "KeyCue", code: 1, userInfo: [NSLocalizedDescriptionKey: "出力フォーマットを作れません"])
        }
        let mixer = self.mixer
        let node = AVAudioSourceNode(format: format) { _, timestamp, frameCount, bufferList in
            mixer.render(frameCount: Int(frameCount), timestamp: timestamp, bufferList: bufferList)
            return noErr
        }
        engine.attach(node)
        engine.connect(node, to: output, format: format)
        engine.prepare()
        try engine.start()
        mixer.configure(sampleRate: sampleRate, latency: output.presentationLatency)

        configObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange,
                                                                object: engine, queue: .main) { [weak self] _ in
            self?.onConfigurationChange?()
        }
    }

    func stop() {
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
        configObserver = nil
        engine.stop()
    }

    /// 映像をこのデバイスの時計に同期させるための CMClock
    func makeDeviceClock() -> CMClock? {
        guard let deviceUID else { return nil }
        var clock: CMClock?
        let status = CMAudioDeviceClockCreate(allocator: kCFAllocatorDefault, deviceUID: deviceUID as CFString, clockOut: &clock)
        return status == noErr ? clock : nil
    }
}
