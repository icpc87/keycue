import AppKit
import SwiftUI

private func elapsed(_ since: Date) -> String {
    let t = Int(Date().timeIntervalSince(since))
    return String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60)
}

/// 上の帯の「録画」「配信」ボタン（押すとオンにした出力をすべて始める・止める）
struct OutputButtons: View {
    @ObservedObject var feed: ProgramFeed
    @ObservedObject var camera: VirtualCamera
    let compact: Bool

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { _ in
            HStack(spacing: 6) {
                button(.record, icon: "record.circle", title: "録画", color: .red)
                button(.stream, icon: "dot.radiowaves.left.and.right", title: "配信", color: Color(red: 0.2, green: 0.55, blue: 1))
                cameraButton
            }
        }
    }

    private func button(_ kind: OutputSpec.Kind, icon: String, title: String, color: Color) -> some View {
        let mine = feed.outputs.filter { $0.spec.kind == kind }
        let running = mine.filter { $0.state == .running }
        let failed = mine.contains { $0.state == .failed }
        let label: String = {
            guard let first = running.map(\.startedAt).min() else { return failed ? "\(title) ⚠" : title }
            return "\(title) \(elapsed(first))" + (running.count > 1 ? "（\(running.count)）" : "")
        }()
        return Button { toggle(kind) } label: {
            if compact {
                // 狭いときは印だけ（色で動いているとわかる。時間は右の欄に出る）
                Image(systemName: icon)
            } else {
                Label(label, systemImage: icon).monospacedDigit()
            }
        }
        .fixedSize()
        .modifier(OnAir(on: !running.isEmpty, color: color))
        .help(kind == .record ? "オンにした録画の出力をすべて始める／止める（設定 › 録画・配信）"
                              : "オンにした配信の出力をすべて始める／止める（設定 › 録画・配信）")
    }

    private var cameraButton: some View {
        let on = camera.state == .running
        return Button {
            camera.isOn ? camera.stop() : camera.start()
        } label: {
            if compact { Image(systemName: "web.camera") } else { Label("仮想カメラ", systemImage: "web.camera") }
        }
        .fixedSize()
        .modifier(OnAir(on: on, color: .green))
        .help("プログラムの絵を、Zoom・Teams・OBS などのカメラ「KeyCue」として出す（もう一度押すと止める）")
    }

    private func toggle(_ kind: OutputSpec.Kind) {
        if feed.isActive(kind) {
            if kind == .stream {
                let a = NSAlert()
                a.messageText = "配信を止めますか？"
                a.informativeText = "オンにしてある配信をすべて止めます。"
                a.addButton(withTitle: "止める")
                a.addButton(withTitle: "キャンセル")
                guard a.runModal() == .alertFirstButtonReturn else { return }
            }
            feed.stopAll(kind)
            return
        }
        feed.clearFailed()
        let errors = feed.startAll(kind)
        if !errors.isEmpty {
            let a = NSAlert()
            a.messageText = kind == .record ? "録画を始められません" : "配信を始められません"
            a.informativeText = errors.joined(separator: "\n")
            a.runModal()
        }
    }
}

/// 動いているときだけ色付きのボタンにする
private struct OnAir: ViewModifier {
    let on: Bool
    let color: Color

    func body(content: Content) -> some View {
        if on {
            content.buttonStyle(.borderedProminent).tint(color)
        } else {
            content.buttonStyle(.bordered)
        }
    }
}

/// 右の欄の、動いている録画・配信の様子
struct OutputStatusList: View {
    @ObservedObject var feed: ProgramFeed
    @ObservedObject var camera: VirtualCamera

    var body: some View {
        if !feed.outputs.isEmpty || camera.isOn {
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                VStack(alignment: .leading, spacing: 6) {
                    Text("録画・配信").font(.caption).foregroundStyle(.secondary)
                    ForEach(feed.outputs, id: \.spec.id) { o in row(o) }
                    if camera.isOn { cameraRow }
                }
            }
        }
    }

    private var cameraRow: some View {
        let (text, color): (String, Color) = switch camera.state {
        case .running: ("カメラ「KeyCue」に出しています", .green)
        case .installing(let m): (m, .secondary)
        case .failed(let m): (m, .orange)
        case .off: ("", .secondary)
        }
        return VStack(alignment: .leading, spacing: 1) {
            Label("仮想カメラ", systemImage: "web.camera").font(.caption.weight(.semibold)).foregroundStyle(color)
            Text(text).font(.caption2).foregroundStyle(camera.state == .running ? .secondary : color).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func row(_ o: FFmpegOutput) -> some View {
        let color: Color = switch o.state {
        case .running: o.spec.kind == .record ? .red : Color(red: 0.35, green: 0.65, blue: 1)
        case .failed: .orange
        default: .secondary
        }
        let state: String = switch o.state {
        case .running: "\(elapsed(o.startedAt)) · \(Int(o.bitrateKbps)) kbps · \(Int(o.encodeFps.rounded())) fps" + (o.droppedFrames > 0 ? " · 落ち \(o.droppedFrames)" : "")
        case .stopping: "書き終えています…"
        case .failed: "止まりました"
        case .stopped: "止めました"
        }
        return VStack(alignment: .leading, spacing: 1) {
            Label(o.spec.name, systemImage: o.spec.kind == .record ? "record.circle" : "dot.radiowaves.left.and.right")
                .font(.caption.weight(.semibold)).foregroundStyle(color)
            Text(state).font(.caption2).foregroundStyle(.secondary).monospacedDigit()
            if o.spec.kind == .record {
                Text((o.target as NSString).lastPathComponent).font(.caption2).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle)
                    .onTapGesture { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: o.target)]) }
                    .help("Finder で表示")
            }
            if let e = o.error {
                Text(e).font(.caption2).foregroundStyle(.orange).lineLimit(4).textSelection(.enabled)
            }
        }
    }
}

/// 設定の「録画・配信」タブ：出力の大きさ・コマ数と、出力の一覧（いくつでも）
struct OutputSettings: View {
    @ObservedObject var feed: ProgramFeed
    @AppStorage(Prefs.programWidthKey) private var width = 1920.0
    @AppStorage(Prefs.programHeightKey) private var height = 1080.0
    @AppStorage(Prefs.programFpsKey) private var fps = 30.0
    @State private var specs = ProgramFeed.specs

    private var size: Binding<String> {
        Binding(get: { "\(Int(width))x\(Int(height))" }, set: { v in
            let p = v.split(separator: "x").compactMap { Double($0) }
            if p.count == 2 { width = p[0]; height = p[1] }
        })
    }

    var body: some View {
        Form {
            Section("出力の絵（録画・配信・仮想カメラ共通）") {
                Picker("大きさ", selection: size) {
                    Text("1280×720（HD）").tag("1280x720")
                    Text("1920×1080（フル HD）").tag("1920x1080")
                    Text("2560×1440").tag("2560x1440")
                    Text("3840×2160（4K）").tag("3840x2160")
                }
                Picker("コマ数 / 秒", selection: $fps) {
                    ForEach([24, 25, 29.97, 30, 50, 59.94, 60], id: \.self) { Text(String(format: $0 == $0.rounded() ? "%.0f" : "%.2f", $0)).tag($0) }
                }
                Text("録画・配信をしているあいだは変えられません（次に始めたときから）。映り方（縦横比・背景色）は「映像」タブの設定を使います。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .disabled(!feed.outputs.isEmpty)
            Section {
                ForEach($specs) { $spec in
                    OutputSpecEditor(spec: $spec) { specs.removeAll { $0.id == spec.id } }
                }
            } header: {
                HStack {
                    Text("出力（オンにしたものを「録画」「配信」ボタンでまとめて始めます）")
                    Spacer()
                    Button("＋ 録画") { specs.append(OutputSpec(name: "録画", kind: .record, folder: OutputSpec.defaultFolder, videoBitrateKbps: 20000)) }
                    Button("＋ 配信") { specs.append(OutputSpec(name: "配信", kind: .stream, url: OutputSpec.services[0].url)) }
                }
            }
        }
        .formStyle(.grouped)
        .onChange(of: specs) { _, v in ProgramFeed.specs = v }
    }
}

private struct OutputSpecEditor: View {
    @Binding var spec: OutputSpec
    let remove: () -> Void
    @State private var key = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Toggle("", isOn: $spec.enabled).labelsHidden().help("オンにすると「録画」「配信」ボタンで始まります")
                TextField("", text: $spec.name, prompt: Text("名前")).frame(width: 200)
                Text(spec.kind == .record ? "録画" : "配信").foregroundStyle(.secondary)
                Spacer()
                Button("削除", role: .destructive, action: remove)
            }
            if spec.kind == .record {
                HStack {
                    Text("保存先")
                    TextField("", text: $spec.folder)
                    Button("選ぶ…") { chooseFolder() }
                    Button("開く") {
                        let url = URL(fileURLWithPath: (spec.folder as NSString).expandingTildeInPath)
                        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(url)
                    }
                }
                HStack {
                    Text("ファイル名")
                    TextField("", text: $spec.fileName).frame(width: 200)
                    Picker("", selection: $spec.container) {
                        Text(".mp4").tag("mp4")
                        Text(".mov").tag("mov")
                        Text(".mkv（途中で止まっても読める）").tag("mkv")
                    }
                    .labelsHidden().frame(width: 200)
                }
                Text("{date} {time} {name} が使えます").font(.caption).foregroundStyle(.secondary)
            } else {
                HStack {
                    Text("URL")
                    TextField("", text: $spec.url, prompt: Text("rtmp://…"))
                    Menu("配信先") {
                        ForEach(OutputSpec.services, id: \.url) { s in Button(s.name) { spec.url = s.url } }
                    }
                    .fixedSize()
                }
                HStack {
                    Text("配信キー")
                    SecureField("", text: $key)
                        .onAppear { key = spec.streamKey }
                        .onChange(of: key) { _, v in spec.streamKey = v }
                }
                Text("配信キーはキーチェーンに入れます（設定ファイルには書きません）").font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Text("映像")
                Picker("", selection: $spec.videoCodec) {
                    Text("H.264").tag("h264")
                    Text("HEVC（H.265）").tag("hevc")
                    if spec.kind == .record { Text("ProRes 422 HQ（.mov）").tag("prores") }
                }
                .labelsHidden().frame(width: 180)
                if spec.videoCodec != "prores" {
                    NumberField(value: Binding(get: { Double(spec.videoBitrateKbps) }, set: { spec.videoBitrateKbps = max(100, Int($0)) }), width: 70)
                    Text("kbps").foregroundStyle(.secondary)
                }
            }
            HStack {
                Text("音")
                Picker("", selection: $spec.audioCodec) {
                    Text("AAC").tag("aac")
                    if spec.kind == .record { Text("PCM 24bit（.mov / .mkv）").tag("pcm") }
                }
                .labelsHidden().frame(width: 180)
                if spec.audioCodec == "aac" {
                    NumberField(value: Binding(get: { Double(spec.audioBitrateKbps) }, set: { spec.audioBitrateKbps = max(32, Int($0)) }), width: 56)
                    Text("kbps").foregroundStyle(.secondary)
                }
                Picker("", selection: $spec.audioChannels) {
                    Text("2ch（出力 1-2）").tag(2)
                    Text("4ch（出力 1-4）").tag(4)
                    Text("8ch（出力 1-8）").tag(8)
                }
                .labelsHidden().frame(width: 150)
            }
        }
        .labelsHidden()
        .textFieldStyle(.roundedBorder)
        .padding(.vertical, 4)
    }

    private func chooseFolder() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = false
        p.canCreateDirectories = true
        p.message = "録画を保存するフォルダ"
        if p.runModal() == .OK, let url = p.url { spec.folder = url.path }
    }
}
