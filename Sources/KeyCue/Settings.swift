import AppKit
import SwiftUI

/// アプリ全体の設定（全インスタンス共通。出力デバイスなどインスタンスごとのものはキーマップに入る）
enum Prefs {
    private static var d: UserDefaults { .standard }

    static let keyboardLayoutKey = "pref.keyboardLayout"        // auto / us / jis
    static let reopenLastKeymapKey = "pref.reopenLastKeymap"
    /// 起動したとき：start（はじめの画面）/ reopen（前回のキーマップ）/ blank（空のまま）
    static let launchActionKey = "pref.launchAction"
    static let recentKeymapsKey = "pref.recentKeymaps"
    static let multiDropKey = "pref.multiDrop"                  // ask / list / spread
    static let occupiedDropKey = "pref.occupiedDrop"            // ask / append / replace
    static let preferPowerPointKey = "pref.preferPowerPoint"
    static let defaultFadeInKey = "pref.defaultFadeIn"
    static let defaultFadeOutKey = "pref.defaultFadeOut"
    static let defaultModeKey = "pref.defaultMode"
    static let defaultOnNextKey = "pref.defaultOnNext"
    static let videoFillKey = "pref.videoFill"                  // fit / fill / stretch
    static let videoBackgroundKey = "pref.videoBackground"      // black / white
    static let openVideoOnLaunchKey = "pref.openVideoOnLaunch"
    /// メイン画面の表示：keyboard（キーボードの図）／ thumbnails（グリッド）／ layers（レイヤーごとの段）
    static let mainViewKey = "pref.mainView"
    /// レイヤー表示でクリックしたとき、同じレイヤーの映像と入れ替える
    static let layerReplaceKey = "pref.layerReplace"
    static var layerReplace: Bool { d.bool(forKey: layerReplaceKey) }
    static let thumbSizeKey = "pref.thumbSize"
    /// レイヤー表示の上のモニター：both（プレビューとプログラム）／ program ／ preview ／ none
    static let layerMonitorsKey = "pref.layerMonitors"
    static var layerMonitors: String { d.string(forKey: layerMonitorsKey) ?? "both" }
    /// プレビューがあるとき：クリックはプレビューへ、「ライブへ」で出す
    static var layerPreviewMode: Bool { ["both", "preview"].contains(layerMonitors) }
    /// 「ライブへ」の切り替え：cut ／ smooth（smooth はフェード）
    /// 録画・配信・仮想カメラの絵の大きさとコマ数、出力の一覧（JSON）
    static let programWidthKey = "pref.programWidth"
    static let programHeightKey = "pref.programHeight"
    static let programFpsKey = "pref.programFps"
    static let outputsKey = "pref.outputs"
    static let takeStyleKey = "pref.takeStyle"
    static let takeDurationKey = "pref.takeDuration"
    static var takeSmooth: Bool { (d.string(forKey: takeStyleKey) ?? "smooth") == "smooth" }
    static var takeDuration: Double { d.double(forKey: takeDurationKey) }

    /// 出荷時の設定
    static let factoryDefaults: [String: Any] = [
        keyboardLayoutKey: "auto",
        reopenLastKeymapKey: true,
        launchActionKey: "start",
        multiDropKey: "ask",
        occupiedDropKey: "ask",
        preferPowerPointKey: true,
        defaultFadeInKey: 0.0,
        defaultFadeOutKey: 1.0,
        defaultModeKey: KeyMode.toggle.rawValue,
        defaultOnNextKey: NextBehavior.keep.rawValue,
        videoFillKey: "fit",
        videoBackgroundKey: "black",
        openVideoOnLaunchKey: false,
        mainViewKey: "keyboard",
        thumbSizeKey: 200.0,
        layerReplaceKey: true,
        layerMonitorsKey: "both",
        takeStyleKey: "smooth",
        takeDurationKey: 0.5,
        programWidthKey: 1920.0,
        programHeightKey: 1080.0,
        programFpsKey: 30.0,
    ]

    static func register() {
        d.register(defaults: factoryDefaults)
    }

    static var keyboardLayout: String { d.string(forKey: keyboardLayoutKey) ?? "auto" }
    static var reopenLastKeymap: Bool { d.bool(forKey: reopenLastKeymapKey) }
    static var launchAction: String { d.string(forKey: launchActionKey) ?? "start" }
    /// 最近開いた・保存したキーマップ（新しい順・まだあるものだけ）
    static var recentKeymaps: [String] {
        (d.stringArray(forKey: recentKeymapsKey) ?? []).filter { FileManager.default.fileExists(atPath: $0) }
    }
    static func addRecentKeymap(_ path: String) {
        var list = (d.stringArray(forKey: recentKeymapsKey) ?? []).filter { $0 != path }
        list.insert(path, at: 0)
        d.set(Array(list.prefix(8)), forKey: recentKeymapsKey)
    }
    static var multiDrop: String { d.string(forKey: multiDropKey) ?? "ask" }
    static var occupiedDrop: String { d.string(forKey: occupiedDropKey) ?? "ask" }
    static var preferPowerPoint: Bool { d.bool(forKey: preferPowerPointKey) }
    static var defaultFadeIn: Double { d.double(forKey: defaultFadeInKey) }
    static var defaultFadeOut: Double { d.double(forKey: defaultFadeOutKey) }
    static var defaultMode: KeyMode { KeyMode(rawValue: d.string(forKey: defaultModeKey) ?? "") ?? .toggle }
    static var defaultOnNext: NextBehavior { NextBehavior(rawValue: d.string(forKey: defaultOnNextKey) ?? "") ?? .keep }
    static var videoFill: String { d.string(forKey: videoFillKey) ?? "fit" }
    static var videoBackground: String { d.string(forKey: videoBackgroundKey) ?? "black" }
    static var openVideoOnLaunch: Bool { d.bool(forKey: openVideoOnLaunchKey) }
}

struct SettingsView: View {
    let model: AppModel
    @State var tab = 0

    var body: some View {
        TabView(selection: $tab) {
            VideoSettings(model: model, video: model.video)
                .tabItem { Label("映像", systemImage: "play.rectangle") }
                .tag(0)
            ScreenSettings(video: model.video)
                .tabItem { Label("スクリーン", systemImage: "display.2") }
                .tag(1)
            AudioSettings(model: model)
                .tabItem { Label("音", systemImage: "speaker.wave.2") }
                .tag(2)
            OutputSettings(feed: model.feed)
                .tabItem { Label("録画・配信", systemImage: "record.circle") }
                .tag(4)
            GeneralSettings(model: model)
                .tabItem { Label("一般", systemImage: "gearshape") }
                .tag(3)
        }
        .padding(16)
        .frame(width: 660, height: 560)
        .preferredColorScheme(.dark)
    }
}

private struct VideoSettings: View {
    let model: AppModel
    @ObservedObject var video: VideoOutput
    @AppStorage(Prefs.videoFillKey) private var fill = "fit"
    @AppStorage(Prefs.videoBackgroundKey) private var background = "black"
    @AppStorage(Prefs.openVideoOnLaunchKey) private var openOnLaunch = false

    var body: some View {
        Form {
            Section("出力（このインスタンス）") {
                HStack {
                    Text(video.isShowing ? "映像出力は開いています" : "映像出力は閉じています").foregroundStyle(.secondary)
                    Spacer()
                    Button(video.isShowing ? "映像出力を閉じる" : "映像出力を開く") {
                        video.isShowing ? video.hide() : video.show()
                    }
                }
                Toggle("起動したら映像出力を開く", isOn: $openOnLaunch)
                Text("出すモニターは「スクリーン」タブで選びます（複数可）。").font(.caption).foregroundStyle(.secondary)
            }
            Section("映り方（全インスタンス共通）") {
                Picker("縦横比が画面と違うとき", selection: $fill) {
                    Text("全体を映す（余白は背景色）").tag("fit")
                    Text("画面いっぱい（はみ出しは切る）").tag("fill")
                    Text("引き伸ばす").tag("stretch")
                }
                Picker("背景色", selection: $background) {
                    Text("黒").tag("black")
                    Text("白").tag("white")
                }
            }
            Section {
                Text("素材ごとの重なり順（映像レイヤー）やフェードは、各キーの設定で変えます。")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .onChange(of: fill) { _, _ in video.applyAppearance() }
        .onChange(of: background) { _, _ in video.applyAppearance() }
    }
}

/// 映像を出すモニターの選択（複数）
private struct ScreenSettings: View {
    @ObservedObject var video: VideoOutput

    var body: some View {
        Form {
            Section("映像を出す先（このインスタンス・複数選べます）") {
                outputRow(VideoOutput.windowID, title: "ウィンドウ", detail: "動かせる普通のウィンドウに出します", warning: nil)
                ForEach(Array(video.screenNames.enumerated()), id: \.element) { i, name in
                    let screen = video.screen(named: name)
                    outputRow(name, title: "ディスプレイ \(i + 1): \(screen?.localizedName ?? name)",
                              detail: screen.map { "\(Int($0.frame.width))×\(Int($0.frame.height))・全画面" + ($0 == NSScreen.screens.first ? "・メニューバーのある画面" : "") } ?? "",
                              warning: video.isControlScreen(name) ? "KeyCue の操作画面があるディスプレイです。全画面にすると操作画面が隠れます（キーでの操作は続けられます）" : nil)
                }
                ForEach(video.missingOutputs, id: \.self) { name in
                    HStack {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
                        VStack(alignment: .leading) {
                            Text(name)
                            Text("いまつながっていません。つなぐと自動で出ます").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("選択から外す") { video.setOutput(name, on: false) }
                    }
                }
            }
            Section {
                HStack {
                    Button("画面を識別（各ディスプレイに番号を表示）") { video.identifyScreens() }
                    Spacer()
                    Button(video.isShowing ? "映像出力を閉じる" : "映像出力を開く") {
                        video.isShowing ? video.hide() : video.show()
                    }
                }
                Text("選んだ出力すべてに同じ映像が出ます。キーごとに出す画面を絞るときは、キーの設定の「出す画面」で選びます。")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .onAppear { video.refreshScreens() }
    }

    private func outputRow(_ id: String, title: String, detail: String, warning: String?) -> some View {
        Toggle(isOn: Binding(get: { video.isOn(id) }, set: { video.setOutput(id, on: $0) })) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(detail).font(.caption).foregroundStyle(.secondary)
                if let warning, video.isOn(id) {
                    Text(warning).font(.caption).foregroundStyle(.yellow)
                }
            }
        }
    }
}

private struct AudioSettings: View {
    @ObservedObject var model: AppModel
    @AppStorage(Prefs.defaultFadeInKey) private var fadeIn = 0.0
    @AppStorage(Prefs.defaultFadeOutKey) private var fadeOut = 1.0
    @AppStorage(Prefs.defaultModeKey) private var mode = KeyMode.toggle.rawValue
    @AppStorage(Prefs.defaultOnNextKey) private var onNext = NextBehavior.keep.rawValue

    var body: some View {
        Form {
            Section("出力（このインスタンス）") {
                Picker("出力デバイス", selection: $model.outputDeviceUID) {
                    Text("システム既定の出力").tag(String?.none)
                    ForEach(model.devices) { Text($0.label).tag(String?.some($0.uid)) }
                }
                LabeledContent("いまの出力", value: model.engineInfo)
                HStack {
                    Text("マスター音量")
                    Slider(value: $model.masterVolume, in: 0...1.5)
                    Text(model.masterVolume <= 0.0001 ? "-∞ dB" : String(format: "%+.1f dB", 20 * log10(model.masterVolume)))
                        .font(.caption.monospacedDigit()).frame(width: 60)
                }
                HStack {
                    Text("全停止（ESC）のフェード")
                    Spacer()
                    numberField($model.stopAllFade)
                    Text("秒").foregroundStyle(.secondary)
                }
            }
            Section("出力チャンネルのミュート・テストトーン（このインスタンス）") {
                ScrollView(.horizontal) {
                    HStack(spacing: 6) {
                        ForEach(0..<model.visibleOutputChannels, id: \.self) { ch in channelColumn(ch) }
                    }
                    .padding(.vertical, 4)
                }
                Text("テストトーンは 1kHz・マスター音量に従います。暗い番号はいまのデバイスにない ch です。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("新しく入れるキーの初期値（全インスタンス共通）") {
                Picker("モード", selection: $mode) {
                    ForEach(KeyMode.allCases) { Text($0.label).tag($0.rawValue) }
                }
                HStack {
                    Text("フェード IN")
                    numberField($fadeIn)
                    Text("秒").foregroundStyle(.secondary)
                    Spacer().frame(width: 24)
                    Text("フェード OUT")
                    numberField($fadeOut)
                    Text("秒").foregroundStyle(.secondary)
                    Spacer()
                }
                Picker("次の素材が流れたら", selection: $onNext) {
                    ForEach(NextBehavior.allCases) { Text($0.label).tag($0.rawValue) }
                }
            }
        }
        .formStyle(.grouped)
        .onDisappear { model.testToneChannel = nil }
    }

    private func channelColumn(_ ch: Int) -> some View {
        let muted = model.mutedOutputs.contains(ch)
        let tone = model.testToneChannel == ch
        return VStack(spacing: 4) {
            Text("\(ch + 1)").font(.caption.monospacedDigit())
                .foregroundStyle(ch < model.outputChannels ? Color.primary : Color.secondary)
            Button(muted ? "M" : "M") {
                if muted { model.mutedOutputs.remove(ch) } else { model.mutedOutputs.insert(ch) }
            }
            .tint(muted ? .red : nil)
            .buttonStyle(.borderedProminent)
            .controlSize(.mini)
            .opacity(muted ? 1 : 0.5)
            Button("T") { model.testToneChannel = tone ? nil : ch }
                .tint(tone ? .orange : nil)
                .buttonStyle(.borderedProminent)
                .controlSize(.mini)
                .opacity(tone ? 1 : 0.5)
        }
        .frame(width: 30)
    }
}

private struct GeneralSettings: View {
    @ObservedObject var model: AppModel
    @AppStorage(Prefs.keyboardLayoutKey) private var layout = "auto"
    @AppStorage(Prefs.mainViewKey) private var mainView = "keyboard"
    @AppStorage(Prefs.layerReplaceKey) private var layerReplace = true
    @AppStorage(Prefs.layerMonitorsKey) private var layerMonitors = "both"

    private var mainViewHelp: String {
        switch mainView {
        case "thumbnails": return "グリッド：素材をサムネイルにして、キーの順に並べます。クリックで再生（キーを押したのと同じ）、⌘ クリックで選択だけ、右クリックでメニュー。"
        case "layers": return "レイヤー：映像の重なり順（レイヤー 9 が前面）ごとに素材を段に並べます。カードを別の段へドラッグするとレイヤーが変わります。クリックで再生、⌘ クリックで選択だけ。"
        default: return "キーボード：キーボードの図に素材を表示します。どの表示でも、キーでの操作は同じです。"
        }
    }
    @AppStorage(Prefs.launchActionKey) private var launchAction = "start"
    @AppStorage(Prefs.multiDropKey) private var multiDrop = "ask"
    @AppStorage(Prefs.occupiedDropKey) private var occupiedDrop = "ask"
    @AppStorage(Prefs.preferPowerPointKey) private var preferPowerPoint = true
    @AppStorage(Updater.autoCheckKey) private var autoUpdate = true

    var body: some View {
        Form {
            Section("画面") {
                Picker("メイン画面の表示", selection: $mainView) {
                    Text("キーボード").tag("keyboard")
                    Text("グリッド").tag("thumbnails")
                    Text("レイヤー").tag("layers")
                }
                .pickerStyle(.segmented)
                Text(mainViewHelp).font(.caption).foregroundStyle(.secondary)
                if mainView == "layers" {
                    Picker("上のモニター", selection: $layerMonitors) {
                        Text("プレビューとプログラム").tag("both")
                        Text("プログラム（ライブ）だけ").tag("program")
                        Text("プレビューだけ").tag("preview")
                        Text("なし").tag("none")
                    }
                    Text("プレビューがあるときは、カードをクリックするとそのレイヤーのプレビューに入り（黄色の枠）、「ライブへ」でまとめて出します（カット／スムーズ）。段の先頭の「レイヤーを消去」でそのレイヤーを空にできます。キーボードで押したときは、すぐライブに出ます。")
                        .font(.caption).foregroundStyle(.secondary)
                    Toggle("クリックしたとき、同じレイヤーで出ている映像と入れ替える", isOn: $layerReplace)
                    Text("オンにすると、レイヤー表示でカードをクリックしたとき、同じレイヤーで流れている映像を新しい素材のフェード IN の秒数で消して入れ替えます（1 つのレイヤーに 1 つずつ）。キーボードで押したときは、キーの設定どおりです。")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Section("キー") {
                Picker("キーボード配列", selection: $layout) {
                    Text("自動（接続中のキーボード）").tag("auto")
                    Text("US").tag("us")
                    Text("JIS（日本語）").tag("jis")
                }
                Text("配列の変更は次に起動したときに反映されます。").font(.caption).foregroundStyle(.secondary)
                Toggle("ほかのアプリが前面でもキーで鳴らす（バックグラウンド入力）", isOn: $model.backgroundInput)
            }
            Section("素材を入れるとき") {
                Picker("すでに素材があるキーに入れたとき", selection: $occupiedDrop) {
                    Text("毎回確認する").tag("ask")
                    Text("いつも後ろに追加（押すたび次へ）").tag("append")
                    Text("いつも差し替える").tag("replace")
                }
                Picker("複数のファイルを空いたキーに入れたとき", selection: $multiDrop) {
                    Text("毎回確認する").tag("ask")
                    Text("そのキーにまとめる（押すたび次へ）").tag("list")
                    Text("後ろの空きキーに 1 つずつ").tag("spread")
                }
                Picker("pptx の変換", selection: $preferPowerPoint) {
                    Text("PowerPoint（なければ Keynote）").tag(true)
                    Text("いつも Keynote").tag(false)
                }
            }
            Section("アップデート") {
                Toggle("起動したときに新しい版があるか確かめる（1 日 1 回）", isOn: $autoUpdate)
                HStack {
                    Text("いまの版: \(Updater.shared.currentVersion)（build \(Updater.shared.currentBuild)）")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("今すぐ確認") { Updater.shared.check(userInitiated: true) }
                    Button("ホームページ") { NSWorkspace.shared.open(URL(string: "https://keycue.ic2.jp/")!) }
                }
            }
            Section("起動・ファイル") {
                Picker("起動したとき", selection: $launchAction) {
                    Text("はじめの画面（新しく作る・開く）").tag("start")
                    Text("前回のキーマップを開く").tag("reopen")
                    Text("空のキーマップ").tag("blank")
                }
                HStack {
                    Text("スライドから書き出した画像・動画")
                    Spacer()
                    Button("Finder で開く") {
                        try? FileManager.default.createDirectory(at: DeckBuilder.rootDir, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(DeckBuilder.rootDir)
                    }
                }
                HStack {
                    Text("このウィンドウ: インスタンス #\(model.instanceNumber)").foregroundStyle(.secondary)
                    Spacer()
                    Button("新しいインスタンスを起動") {
                        let config = NSWorkspace.OpenConfiguration()
                        config.createsNewApplicationInstance = true
                        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: config)
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

private func numberField(_ value: Binding<Double>) -> some View {
    NumberField(value: value, width: 56)
}
