import AppKit
import AudioToolbox
import AVFoundation
import Carbon.HIToolbox
import SwiftUI

/// 開発用の自動チェック。KEYCUE_SELFTEST=<テスト素材フォルダ> で起動すると実行して終了する。
enum SelfTest {
    static var folder: URL? {
        ProcessInfo.processInfo.environment["KEYCUE_SELFTEST"].map { URL(fileURLWithPath: $0) }
    }

    static func log(_ s: String) {
        FileHandle.standardError.write((s + "\n").data(using: .utf8)!)
    }

    /// ミキサーに 8ch のバッファを直接渡して、鳴っている出力チャンネルを返す
    static func activeOutputs(clip: AudioClip, routes: [(Int, Int)], muted: Set<Int> = []) -> [Int] {
        let mixer = Mixer()
        mixer.configure(sampleRate: clip.sampleRate, latency: 0)
        mixer.setMuted(muted)
        let frames = 512, out = 8
        _ = mixer.addVoice(clip: clip, routes: routes, volume: 1, fadeIn: 0, loop: false, pending: false)
        let abl = AudioBufferList.allocate(maximumBuffers: out)
        let storage = (0..<out).map { _ in UnsafeMutablePointer<Float>.allocate(capacity: frames) }
        for i in 0..<out {
            abl[i] = AudioBuffer(mNumberChannels: 1, mDataByteSize: UInt32(frames * 4), mData: storage[i])
        }
        var ts = AudioTimeStamp()
        mixer.render(frameCount: frames, timestamp: &ts, bufferList: abl.unsafeMutablePointer)
        let active = (0..<out).filter { c in (0..<frames).contains { abs(storage[c][$0]) > 0.001 } }
        storage.forEach { $0.deallocate() }
        free(abl.unsafeMutablePointer)
        return active.map { $0 + 1 }
    }

    static func offlineRouting(clip: AudioClip) -> Bool {
        var a = KeyAssignment(path: "", colorIndex: 0)
        var ok = true
        func expect(_ label: String, _ got: [Int], _ want: [Int]) {
            let pass = got == want
            ok = ok && pass
            log("routing \(label) → \(got) \(pass ? "OK" : "NG (expected \(want))")")
        }
        expect("auto", activeOutputs(clip: clip, routes: Routing.routes(for: a, sourceChannels: 8, outputChannels: 8)), [1, 2, 3, 4, 5, 6, 7, 8])
        a.outputMap = "3"
        expect("'3'", activeOutputs(clip: clip, routes: Routing.routes(for: a, sourceChannels: 8, outputChannels: 8)), [3, 4, 5, 6, 7, 8])
        a.matrix = [[5], [0, 7]]
        expect("matrix ch1→6, ch2→1+8", activeOutputs(clip: clip, routes: Routing.routes(for: a, sourceChannels: 8, outputChannels: 8)), [1, 6, 8])
        expect("matrix + mute 8", activeOutputs(clip: clip, routes: Routing.routes(for: a, sourceChannels: 8, outputChannels: 8), muted: [7]), [1, 6])
        return ok
    }

    static func shoot(_ name: String) {
        guard let dir = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_SHOTS"] else { return }
        RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        for w in NSApp.windows where w.isVisible {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            p.arguments = ["-x", "-o", "-l", String(w.windowNumber), "\(dir)/\(name)_\(w.windowNumber).png"]
            try? p.run()
            p.waitUntilExit()
        }
    }

    /// 本物のキーイベントをアプリに流して、トグル（押す→再生、もう一度→停止）を確かめる
    static func runKeyEvents(model: AppModel, file: URL) {
        let q = UInt16(kVK_ANSI_Q)
        model.masterVolume = 0.08
        model.assign(file, to: q)
        var popover: NSPopover?
        func post(_ down: Bool) {
            guard let w = NSApp.keyWindow ?? NSApp.mainWindow ?? NSApp.windows.first(where: { $0.title.hasPrefix("KeyCue") }) else { log("no window"); return }
            let e = NSEvent.keyEvent(with: down ? .keyDown : .keyUp, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                     windowNumber: w.windowNumber, context: nil, characters: "q", charactersIgnoringModifiers: "q",
                                     isARepeat: false, keyCode: q)!
            NSApp.postEvent(e, atStart: false)
        }
        func state(_ label: String) {
            let w = NSApp.keyWindow
            log("\(label): running=\(model.isRunning(q)) progress=\(model.values.progress[q].map { String(format: "%.2f", $0) } ?? "-") cues=\(model.cueState.cues.count) keyWindow=\(w?.title ?? "nil") firstResponder=\(String(describing: w?.firstResponder.map { type(of: $0) }))")
        }
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            switch step {
            case 1...60:
                if model.info(for: q)?.loading ?? true { return }
                state("loaded")
                if ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_EXTERNAL"] != nil {
                    // 外からの本物のキー入力を待って、状態を記録し続ける
                    log("READY")
                    step = 1000
                    return
                }
                step = 100
                post(true); post(false)
            case 1001...1040:
                state("watch")
                if step == 1040 { exit(0) }
            case 102:
                state("after 1st press")
                if ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_POPOVER"] != nil,
                   let main = NSApp.windows.first(where: { $0.title.hasPrefix("KeyCue") }), let content = main.contentView {
                    // 出力ルーティングの表を開いた状態にする
                    let p = NSPopover()
                    p.behavior = .transient
                    p.contentViewController = NSHostingController(rootView: RoutingMatrixView(model: model, code: q, sourceChannels: 4))
                    p.show(relativeTo: NSRect(x: 400, y: 300, width: 10, height: 10), of: content, preferredEdge: .maxY)
                    popover = p
                    RunLoop.current.run(until: Date().addingTimeInterval(0.4))
                    state("popover open")
                }
                if ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_STRAYFOCUS"] != nil,
                   let main = NSApp.windows.first(where: { $0.title.hasPrefix("KeyCue") }), let content = main.contentView {
                    // クリックせずに入力欄へカーソルが移った状態を作る
                    func findField(_ v: NSView) -> NSTextField? {
                        if let f = v as? NSTextField, f.isEditable { return f }
                        for s in v.subviews { if let f = findField(s) { return f } }
                        return nil
                    }
                    if let field = findField(content) { main.makeFirstResponder(field) }
                    state("stray focus")
                }
                post(true); post(false)
            case 103:
                state("after 2nd press")
                popover?.close()
            case 107:
                state("2s later")
                log(model.isRunning(q) || !model.cueState.cues.isEmpty ? "RESULT: still playing" : "RESULT: stopped")
                exit(0)
            default:
                break
            }
        }
    }

    /// 全部のパネルを開いた状態で、いろいろな幅にしてもウィンドウが画面からはみ出さないか
    static func runLayout(model: AppModel, folder: URL) {
        let q = UInt16(kVK_ANSI_Q), r = UInt16(kVK_ANSI_R), t = UInt16(kVK_ANSI_T)
        model.masterVolume = 0.08
        model.assign(folder.appendingPathComponent("eight_ch.wav"), to: q)
        model.assign(folder.appendingPathComponent("clip.mp4"), to: r)
        model.assign(folder.appendingPathComponent("deck.pptx"), to: t)
        model.showPages = true
        var step = 0
        var failed = false
        let widths: [CGFloat] = [10_000, 1280, 980]
        Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { timer in
            step += 1
            guard let w = NSApp.windows.first(where: { $0.title.hasPrefix("KeyCue") }),
                  let visible = w.screen?.visibleFrame else { return }
            if step == 1 {
                if [q, r, t].contains(where: { model.info(for: $0)?.loading ?? true }) { step = 0; return }
                model.openDeckTab(t)
                model.selectedKey = r
                return
            }
            let i = (step - 2) / 2
            guard i < widths.count else {
                log(failed ? "FAILED: layout" : "ALL PASS (layout)")
                timer.invalidate()
                exit(failed ? 1 : 0)
            }
            if step % 2 == 0 {
                var f = w.frame
                f.size.width = min(widths[i], visible.width)
                f.origin.x = visible.minX
                w.setFrame(f, display: true)
            } else {
                let want = min(max(widths[i], w.minSize.width), visible.width)
                let ok = w.frame.width <= visible.width + 0.5 && abs(w.frame.width - want) < 1 && w.frame.minX >= visible.minX - 0.5
                failed = failed || !ok
                log("\(ok ? "PASS" : "FAIL")  width \(Int(want)) → window \(Int(w.frame.width)) at x=\(Int(w.frame.minX)) (screen \(Int(visible.width)))")
                shoot("layout\(Int(want))")
            }
        }
    }

    /// サイト用のスクリーンショット：デモ素材を並べて鳴らした画面を撮る
    static func runDemo(model: AppModel, folder: URL) {
        func k(_ code: Int) -> UInt16 { UInt16(code) }
        model.masterVolume = 0.0
        model.pages[0].name = "開演前"
        model.assign(folder.appendingPathComponent("入場BGM.wav"), to: k(kVK_ANSI_Q))
        model.assign(folder.appendingPathComponent("アナウンス.wav"), to: k(kVK_ANSI_W))
        model.newPage()
        model.pages[1].name = "オープニング"
        let files: [(Int, String)] = [
            (kVK_ANSI_Q, "オープニング映像.mp4"), (kVK_ANSI_W, "入場BGM.wav"), (kVK_ANSI_E, "拍手.wav"),
            (kVK_ANSI_R, "アナウンス.wav"), (kVK_ANSI_A, "8ch サラウンド.wav"), (kVK_ANSI_S, "4ch ステージ.mov"),
            (kVK_ANSI_D, "本編スライド.pdf"), (kVK_ANSI_1, "拍手.wav"),
        ]
        for (code, name) in files { model.assign(folder.appendingPathComponent(name), to: k(code)) }
        model.newPage()
        model.pages[2].name = "本編"
        model.switchPage(to: 1)
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            if step == 1 {
                if files.contains(where: { model.info(for: k($0.0))?.loading ?? true }) { step = 0; return }
                model.handleKey(code: k(kVK_ANSI_W), down: true, shift: false, isRepeat: false)
                model.handleKey(code: k(kVK_ANSI_Q), down: true, shift: false, isRepeat: false)
                model.selectedKey = k(kVK_ANSI_Q)
            }
            if step == 8 {
                shoot("demo")
                timer.invalidate()
                exit(0)
            }
        }
    }

    /// 重さの計測：いくつも同時に鳴らした状態で 10 秒間の CPU 使用率（全スレッド合計、1 コア = 100%）を出す
    static func runPerf(model: AppModel, folder: URL) {
        func k(_ code: Int) -> UInt16 { UInt16(code) }
        model.masterVolume = 0
        let files: [(Int, String)] = [
            (kVK_ANSI_Q, "eight_ch.wav"), (kVK_ANSI_W, "stereo_44k.wav"), (kVK_ANSI_E, "mono.wav"),
            (kVK_ANSI_R, "clip.mp4"), (kVK_ANSI_T, "four_tracks.mov"), (kVK_ANSI_Y, "slides.pdf"),
        ]
        for (code, name) in files {
            model.assign(folder.appendingPathComponent(name), to: k(code))
            var a = model.assignments[k(code)]!
            a.loop = true
            model.update(k(code), a)
        }
        model.selectedKey = k(kVK_ANSI_R)
        func cpuSeconds() -> Double {
            var u = rusage()
            getrusage(RUSAGE_SELF, &u)
            return Double(u.ru_utime.tv_sec) + Double(u.ru_utime.tv_usec) / 1e6 + Double(u.ru_stime.tv_sec) + Double(u.ru_stime.tv_usec) / 1e6
        }
        var step = 0
        var startCPU = 0.0
        var startWall = Date()
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            if step == 1 {
                if files.contains(where: { model.info(for: k($0.0))?.loading ?? true }) { step = 0; return }
                for (code, _) in files { model.handleKey(code: k(code), down: true, shift: false, isRepeat: false) }
            }
            if step == 7 {
                startCPU = cpuSeconds()
                startWall = Date()
            }
            if step == 27 {
                let wall = Date().timeIntervalSince(startWall)
                let cpu = (cpuSeconds() - startCPU) / wall * 100
                log(String(format: "PERF  cues=%d  CPU %.1f%% over %.1fs", model.cueState.cues.count, cpu, wall))
                timer.invalidate()
                exit(0)
            }
        }
    }

    static func run(model: AppModel) {
        // 利用者が変えた設定（新しいキーの初期値など）に左右されないよう、出荷時の設定で確かめる（保存はしない）
        // 起動引数（-pref.mainView thumbnails など）で指定したものは残す
        let args = UserDefaults.standard.volatileDomain(forName: UserDefaults.argumentDomain)
        UserDefaults.standard.setVolatileDomain(Prefs.factoryDefaults.merging(args) { _, given in given }, forName: UserDefaults.argumentDomain)
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_ALPHA"], let bg = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_ALPHA_BG"] {
            runAlpha(model: model, file: URL(fileURLWithPath: f), background: URL(fileURLWithPath: bg))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_VCAM"] {
            runVirtualCamera(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_RECORD"] {
            runRecord(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_LAYERS"] {
            runLayers(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_THUMBS"] {
            runThumbs(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_FIELD"] {
            runField(model: model, file: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_FADE"] {
            runFade(model: model, file: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_PERF"] {
            runPerf(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_DEMO"] {
            runDemo(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_LAYOUT"] {
            runLayout(model: model, folder: URL(fileURLWithPath: f))
            return
        }
        if let f = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_KEYS"] {
            runKeyEvents(model: model, file: URL(fileURLWithPath: f))
            return
        }
        guard let folder else { return }
        log("engine: \(model.engineInfo)  keyboard: \(KeyboardLayout.isJIS ? "JIS" : "US")")
        let q = UInt16(kVK_ANSI_Q), w = UInt16(kVK_ANSI_W), e = UInt16(kVK_ANSI_E), r = UInt16(kVK_ANSI_R)
        let t = UInt16(kVK_ANSI_T), y = UInt16(kVK_ANSI_Y), u = UInt16(kVK_ANSI_U), i = UInt16(kVK_ANSI_I)
        let deckFile = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_DECK"] ?? "deck.pptx"
        let deckHasMedia = deckFile.hasSuffix(".pptx") || deckFile.hasSuffix(".key")
        model.masterVolume = 0.08
        model.assign(["eight_ch.wav", "stereo_44k.wav", "mono.wav", "clip.mp4", deckFile, "four_tracks.mov"]
            .map { folder.appendingPathComponent($0) }, startingAt: q)
        let keys = [q, w, e, r, t, y]

        func press(_ k: UInt16) {
            model.handleKey(code: k, down: true, shift: false, isRepeat: false)
            model.handleKey(code: k, down: false, shift: false, isRepeat: false)
        }
        func edit(_ k: UInt16, _ change: (inout KeyAssignment) -> Void) {
            var a = model.assignments[k]!
            change(&a)
            model.update(k, a)
        }
        func current() -> String? { model.live.deckCurrent[t] }

        var step = 0
        var results: [(String, Bool)] = []
        func check(_ name: String, _ pass: Bool) {
            results.append((name, pass))
            log("\(pass ? "PASS" : "FAIL")  \(name)")
        }
        liveAudioUnitTest(check)
        liveAudioMixerTest(model: model, check)
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            switch step {
            case 1...160:
                if keys.contains(where: { model.info(for: $0)?.loading ?? true }) { return }
                for k in keys {
                    let i = model.info(for: k)!
                    log("\(KeyboardLayout.label(for: k)): ch=\(i.clip?.channelCount ?? 0) video=\(i.hasVideo) deck=\(i.deck?.items.count ?? 0) err=\(i.error ?? "-")")
                }
                check("8ch clip decoded", model.info(for: q)?.clip?.channelCount == 8)
                check("44.1k converted to engine rate", model.info(for: w)?.clip?.sampleRate == model.audio.sampleRate)
                check("video detected with audio", model.info(for: r)?.hasVideo == true && model.info(for: r)?.clip != nil)
                if let clip = model.info(for: q)?.clip { check("offline routing / matrix / mute", offlineRouting(clip: clip)) }
                if let clip = model.info(for: y)?.clip {
                    check("4 mono tracks → 4ch clip", clip.channelCount == 4)
                    let routes = Routing.routes(for: model.assignments[y]!, sourceChannels: 4, outputChannels: 8)
                    check("4 tracks auto → outputs 1-4", activeOutputs(clip: clip, routes: routes) == [1, 2, 3, 4])
                } else {
                    check("4 mono tracks → 4ch clip", false)
                }
                let items = model.deckItems(t)
                log("deck items: " + items.map { "\($0.id)(\($0.kind.rawValue)\($0.enabled ? "" : ",off"))" }.joined(separator: " "))
                if deckHasMedia {
                    check("deck: 4 slides + video of slide 2", items.map(\.id) == ["s1", "s2", "s2-m1", "s3", "s4"])
                    check("deck: hidden slide 3 disabled", items.first { $0.id == "s3" }?.enabled == false)
                    check("deck: video audio decoded", model.info(for: t)?.deck?.clips["s2-m1"] != nil)
                } else {
                    check("deck: pdf pages as images", !items.isEmpty && items.allSatisfy { $0.kind == .image })
                }
                check("deck: files written", !items.isEmpty && items.allSatisfy { item in
                    model.info(for: t).map { FileManager.default.fileExists(atPath: $0.deck!.url(item).path) } ?? false
                })
                step = 300
                edit(w) { $0.onNext = .fade; $0.fadeOut = 0.5 }
                press(w)
            case 161...299:
                log("FAIL  media did not finish loading")
                exit(1)
            case 302:
                check("W playing", model.isRunning(w))
                press(e)
                check("next material → W fading (onNext=fade)", !model.isRunning(w) && model.isRunning(e))
                edit(e) { $0.onNext = .fade; $0.group = 1 }
                press(q)
                check("group 1 key ignores group-0 start", model.isRunning(e) && model.isRunning(q))
            case 303:
                check("meters show signal", model.values.meters.contains { $0 > 0.002 })
                check("8 meters even on a \(model.outputChannels)ch device", model.values.meters.count == max(8, model.outputChannels))
                model.stopAll(immediate: true)
                // 4 トラックの素材を 1〜4ch へ。デバイスが 2ch でも 3・4 はメーターに出る
                edit(y) { $0.matrix = [[0], [1], [2], [3]] }
            case 304:
                model.values.meters = []
                press(y)
            case 305:
                let m = model.values.meters
                log("meters: " + m.map { String(format: "%.3f", $0) }.joined(separator: " "))
                check("ch 3 and 4 show on meters", model.isRunning(y) && m.count >= 4 && m[2] > 0.0005 && m[3] > 0.0005)
                let src = model.values.sourceMeters[y] ?? []
                log("source meters: " + src.map { String(format: "%.3f", $0) }.joined(separator: " "))
                check("material meters: 4 channels, all moving", src.count == 4 && src.allSatisfy { $0 > 0.01 })
                model.selectedKey = y
                shoot("source")
                model.stopAll(immediate: true)
                edit(r) { $0.layer = 2 }
                edit(t) { $0.layer = 7; $0.slideTransition = 0.2 }
                press(r)
                model.openDeckTab(t)
            case 306:
                press(t)
            case 308:
                check("deck starts at first item", current() == "s1")
                let top = model.video.outputView.layer?.sublayers?.max { $0.zPosition < $1.zPosition }
                check("deck (layer 7) above video (layer 2)", top != nil && !(top is AVPlayerLayer) && top?.contents != nil)
                check("deck tab opened", model.deckTabs == [t] && model.activeDeckTab == t)
                shoot("deck")
                press(t)
            case 309:
                check("press → next item", current() == "s2")
                if deckHasMedia { press(t) }
            case 310:
                if deckHasMedia {
                    check("press → embedded video", current() == "s2-m1")
                    check("deck video on output", model.video.outputView.layer?.sublayers?.contains { $0 is AVPlayerLayer && $0.zPosition >= 7_000_000 } == true)
                }
            case 323:
                // 動画（5 秒）が終わると次へ。非表示の s3 は飛ばす
                if deckHasMedia { check("video end → skips hidden slide → s4", current() == "s4") }
                let items = model.deckItems(t)
                model.moveDeckItems(t, from: IndexSet(integer: items.count - 1), to: 0)
                check("reorder: last item moved to top", model.deckItems(t).first?.id == items.last?.id)
                var first = model.deckItems(t).first { $0.id == "s1" }!
                first.advanceAfter = 1
                model.updateDeckItem(t, first)
                model.deckJump(t, itemID: "s1")
            case 324:
                check("jump from list → s1", current() == "s1")
            case 327:
                check("item auto-advance after 1s → s2", current() == "s2")
                model.stopAll(immediate: false)
            case 328:
                // 素材ごとのフェード：s2 へは黒を経由して 0.6 秒、s2 で止めるときは 0.2 秒
                var s1 = model.deckItems(t).first { $0.id == "s1" }!
                s1.advanceAfter = 0
                model.updateDeckItem(t, s1)
                var s2 = model.deckItems(t).first { $0.id == "s2" }!
                s2.transitionStyle = .black
                s2.transition = 0.6
                s2.fadeOut = 0.2
                model.updateDeckItem(t, s2)
                model.deckJump(t, itemID: "s1")
            case 329:
                press(t)
                let colorLayers = { model.video.outputView.layer?.sublayers?.filter { $0.backgroundColor != nil && $0.contents == nil && !($0 is AVPlayerLayer) } ?? [] }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
                    check("fade through black: black cover on output", colorLayers().contains { $0.backgroundColor == CGColor(gray: 0, alpha: 1) })
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) {
                    check("fade through black → s2, cover removed", current() == "s2" && colorLayers().isEmpty)
                    model.stopKey(t, fade: model.assignments[t]!.fadeOut)
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.45) {
                    check("per-item fade-out (0.2s) used on stop", !model.cueState.cues.contains { $0.key == t })
                    // 止まっているときに s2 から始めると、s2 の「入るとき」（黒経由）で出る
                    model.deckJump(t, itemID: "s2")
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
                        check("start on s2 uses its own entry fade (black)", colorLayers().contains { $0.backgroundColor == CGColor(gray: 0, alpha: 1) })
                        model.stopKey(t, fade: 0)
                    }
                }
            case 331:
                // シーク：音声だけ（W・6 秒）と映像付き（R・5 秒）
                edit(w) { $0.onNext = .keep }
                press(r)
                press(w)
            case 333:
                let rowW = model.cueState.cues.first { $0.key == w }
                let rowR = model.cueState.cues.first { $0.key == r }
                check("seek bars available", rowW?.seekable == true && rowR?.seekable == true && (rowW.flatMap { model.values.cues[$0.id]?.duration } ?? 0) > 5)
                if let rowW { model.seek(cueID: rowW.id, to: 0.5) }
                if let rowR { model.seek(cueID: rowR.id, to: 0.6) }
            case 335:
                let pw = model.cueState.cues.first { $0.key == w }.flatMap { model.values.cues[$0.id]?.position } ?? -1
                let pr = model.cueState.cues.first { $0.key == r }.flatMap { model.values.cues[$0.id]?.position } ?? -1
                let vr = model.videoPosition(for: r) ?? -1
                log(String(format: "after seek: W audio %.3f  R audio %.3f  R video %.3f", pw, pr, vr))
                check("audio seek → ~50%", pw > 0.5 && pw < 0.75)
                check("video seek → ~60%", vr > 0.6 && vr < 0.85)
                check("video and its audio stay together after seek", abs(pr - vr) < 0.03)
                shoot("seek")
                model.stopAll(immediate: true)
            case 337:
                // 1 つのキーに 3 素材（音声 → 画像 → 動画）
                model.assignList(["mono.wav", "still.png", "clip.mp4"].map { folder.appendingPathComponent($0) }, to: u)
                // すでに素材があるキーへ追加（ステレオ音源 → 画像を足す）
                model.assign(folder.appendingPathComponent("stereo_44k.wav"), to: i)
            case 338:
                model.appendToKey([folder.appendingPathComponent("still.png")], code: i)
            case 339...360:
                if [u, i].contains(where: { model.info(for: $0)?.loading ?? true }) { return }
                let ids = model.deckItems(u).map(\.id)
                log("list U: " + model.deckItems(u).map { "\($0.title)(\($0.kind.rawValue))" }.joined(separator: " → "))
                check("list key holds 3 materials in order", model.deckItems(u).map(\.kind) == [.audio, .image, .video] && ids.count == 3)
                check("append turns a key into a 2-item list", model.assignments[i]?.sources?.count == 2 && model.deckItems(i).count == 2)
                step = 400
                press(u)
            case 361...399:
                log("FAIL  list did not load")
                exit(1)
            case 401:
                check("press 1 → 1st material (audio)", model.live.deckCurrent[u] == model.deckItems(u)[0].id)
                press(u)
            case 402:
                check("press 2 → 2nd material (image)", model.live.deckCurrent[u] == model.deckItems(u)[1].id)
                press(u)
            case 403:
                check("press 3 → 3rd material (video)", model.live.deckCurrent[u] == model.deckItems(u)[2].id)
                shoot("list")
                press(u)
            case 404:
                check("press 4 → list stops", !model.isRunning(u))
                // 並べ替えても素材ごとの設定は残る
                var video = model.deckItems(u)[2]
                video.volume = 0.5
                model.updateDeckItem(u, video)
                model.moveDeckItems(u, from: IndexSet(integer: 2), to: 0)
                check("reorder keeps per-item settings", model.deckItems(u).first?.volume == 0.5)
                model.removeFromList(u, itemID: model.deckItems(u)[1].id)
            case 406...420:
                if model.info(for: u)?.loading ?? true { return }
                check("remove from list → 2 left", model.assignments[u]?.sources?.count == 2 && model.deckItems(u).count == 2)
                if let dir = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_SHOTS"] {
                    // 設定画面の 3 つのタブ
                    for tab in 0..<3 {
                        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 632, height: 552), styleMask: [.titled], backing: .buffered, defer: false)
                        w.contentView = NSHostingView(rootView: SettingsView(model: model, tab: tab))
                        w.center()
                        w.orderFront(nil)
                        RunLoop.current.run(until: Date().addingTimeInterval(0.4))
                        let p = Process()
                        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
                        p.arguments = ["-x", "-o", "-l", String(w.windowNumber), "\(dir)/settings_\(tab).png"]
                        try? p.run()
                        p.waitUntilExit()
                        w.orderOut(nil)
                    }
                }
                step = 499
            case 500:
                // ここまでのスライド・画像はすべて片付いているはず
                let others = model.video.outputView.layer?.sublayers?.filter { !($0 is AVPlayerLayer) } ?? []
                check("no leftover image layers on the output", others.isEmpty)
                // プレイリスト（ページ）：ページ 2 を作って Q に動画を入れる
                model.newPage()
                model.assign(folder.appendingPathComponent("clip.mp4"), to: q)
            case 501...530:
                if model.info(for: q)?.loading ?? true { return }
                check("new page → 2 pages, now on page 2", model.pages.count == 2 && model.currentPage == 1)
                check("page 2 Q has its own material", model.assignments[q]?.url.lastPathComponent == "clip.mp4")
                check("page 1 Q kept", model.pages[0].keys[q]?.url.lastPathComponent == "eight_ch.wav")
                model.switchPage(to: 0)
                check("switch back → keyboard shows page 1", model.assignments[q]?.url.lastPathComponent == "eight_ch.wav")
                press(w)
                step = 600
            case 531...599:
                log("FAIL  page 2 material did not load")
                exit(1)
            case 601:
                model.switchPage(to: 1)
                press(q)
            case 602:
                let rowW = model.cueState.cues.first { $0.key == w }
                check("page switch keeps playing material (W from page 1)", rowW != nil && rowW?.pageName == "ページ 1")
                check("page 2 Q plays on page 2", model.isRunning(q))
                check("page 1 sound still playing", model.cueState.cues.contains { $0.key == w })
                shoot("pages")
                press(q)
            case 603:
                check("page 2 Q press again → stops only Q", !model.isRunning(q) && model.cueState.cues.contains { $0.key == w && !$0.stopping })
                model.stepPage(by: -1)
                check("⌘← → page 1", model.currentPage == 0)
                model.stopAll(immediate: true)
                // 保存して開き直してもページが残る
                let url = folder.appendingPathComponent("pages-test.keycue")
                try? model.save(to: url)
                model.newDocument()
                try? model.open(url)
            case 604...630:
                if model.info(for: q)?.loading ?? true { return }
                check("pages saved in keymap and reopened", model.pages.count == 2 && model.pages[1].keys[q]?.url.lastPathComponent == "clip.mp4")
                // 複数モニター：ウィンドウ＋ディスプレイ 1（全画面は 1 秒ほどだけ）
                model.video.outputs = [VideoOutput.windowID]
                model.switchPage(to: 1)
                press(q)
                step = 640
            case 641:
                model.video.setOutput(model.video.screenNames[0], on: true)
                let surfaces = model.video.windows.map { ($0.contentView?.layer?.sublayers ?? []).contains { $0 is AVPlayerLayer } }
                check("two outputs open (window + display)", model.video.windows.count == 2)
                check("playing video shown on both outputs", surfaces == [true, true])
                // キーごとの出す画面：ウィンドウだけ
                model.stopAll(immediate: true)
                var a = model.assignments[q]!
                a.screens = [VideoOutput.windowID]
                model.update(q, a)
                press(q)
            case 642:
                // ウィンドウ出力（タイトルあり）にだけ映像があり、ディスプレイ全画面にはない
                let flags = model.video.windows.map { w in
                    (w.styleMask.contains(.titled), (w.contentView?.layer?.sublayers ?? []).contains { $0 is AVPlayerLayer })
                }
                check("per-key screens: only the window shows it", flags.allSatisfy { $0.0 == $0.1 })
                model.stopAll(immediate: true)
                model.video.setOutput(model.video.screenNames[0], on: false)
                check("display output closed again", model.video.windows.count == 1)
                model.newDocument()
                step = 650
            case 651:
                // 動画のフェード IN：2 回目（プレーヤーが用意済み）でもフェードする
                model.assign(SelfTest.folder!.appendingPathComponent("clip.mp4"), to: q)
                edit(q) { $0.fadeIn = 1 }
            case 652...670:
                if model.info(for: q)?.loading ?? true { return }
                press(q)
                step = 671
            case 673:
                model.stopAll(immediate: true)
            case 675:
                press(q)
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                    let l = model.video.outputView.layer?.sublayers?.compactMap { $0 as? AVPlayerLayer }.last
                    let o = l.map { $0.presentation()?.opacity ?? $0.opacity } ?? 1
                    log(String(format: "second press opacity after 0.3s: %.2f", o))
                    check("video fade-in also on 2nd press (prepared player)", l != nil && o < 0.6)
                }
            case 678:
                model.stopAll(immediate: true)
                step = 699
            case 700:
                check("all stopped", model.cueState.cues.isEmpty)
                (NSApp.delegate as? AppDelegate)?.showSettings(nil)
                let settings = NSApp.windows.first { $0.title.hasPrefix("設定") }
                check("settings window has content (⌘,)", settings?.contentView is NSHostingView<SettingsView>)
                settings?.close()
                transitionParseTest(check)
                animationDecoderTest(check)
                let failed = results.filter { !$0.1 }.map(\.0)
                log(failed.isEmpty ? "ALL PASS (\(results.count))" : "FAILED: \(failed)")
                timer.invalidate()
                exit(failed.isEmpty ? 0 : 1)
            default:
                break
            }
        }
    }
}

extension SelfTest {
    /// ライブ入力のリングバッファ：ためてから出す・順番どおり・遅れがたまったら捨てる
    static func liveAudioUnitTest(_ check: (String, Bool) -> Void) {
        let rate = 48000.0
        let src = LiveAudioSource(channels: 2, sampleRate: rate)
        func feed(_ start: Int, _ count: Int) {
            let a = (0..<count).map { Float(start + $0) }, b = a.map { -$0 }
            a.withUnsafeBufferPointer { pa in b.withUnsafeBufferPointer { pb in
                src.write([pa.baseAddress!, pb.baseAddress!], frames: count)
            } }
        }
        feed(0, 100)
        src.read(frames: 64)
        check("live audio: silent until primed", src.scratch[0][0] == 0 && src.scratch[0][63] == 0)
        feed(100, 2000)                       // 2100 frames ≥ 30ms
        src.read(frames: 512)
        check("live audio: in order, per channel", src.scratch[0][0] == 0 && src.scratch[0][511] == 511 && src.scratch[1][10] == -10)
        feed(2100, 48000 / 2)                 // 0.5s 分たまる → 30ms まで捨てて遅れを戻す
        src.read(frames: 512)
        let first = src.scratch[0][0]
        check("live audio: drift trimmed to low latency", first > Float(2100 + 24000 - 48000 * 3 / 100 - 600))
    }

    /// ライブの音がミキサーを通って出力メーターに出る（機器なしで、合成した音を流し込む）
    static func liveAudioMixerTest(model: AppModel, _ check: @escaping (String, Bool) -> Void) {
        let rate = model.audio.sampleRate
        let src = LiveAudioSource(channels: 2, sampleRate: rate)
        let frames = Int(rate * 0.14)
        let tone = (0..<frames).map { Float(sin(Double($0) * 2 * .pi * 440 / rate)) * 0.5 }
        tone.withUnsafeBufferPointer { p in src.write([p.baseAddress!, p.baseAddress!], frames: frames) }
        let id = model.audio.mixer.addLiveVoice(source: src, routes: [(0, 0), (1, 1)], volume: 1, fadeIn: 0)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            let m = model.values.meters
            check("live audio: plays through mixer", m.count >= 2 && m[0] > 0.0005 && m[1] > 0.0005)
            model.audio.mixer.stop(id: id, fade: 0)
        }
    }
}

extension SelfTest {
    /// pptx・Keynote の切り替え設定の読み取り
    static func transitionParseTest(_ check: (String, Bool) -> Void) {
        func parse(_ xml: String) -> (TransitionStyle, Double)? {
            let ns = #"xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:p14="http://schemas.microsoft.com/office/powerpoint/2010/main""#
            guard let doc = try? XMLDocument(xmlString: xml.replacingOccurrences(of: "NS", with: ns)), let e = doc.rootElement() else { return nil }
            return DeckBuilder.pptxTransition(e)
        }
        let black = parse(#"<p:transition NS spd="slow" p14:dur="2000"><p:fade thruBlk="1"/></p:transition>"#)
        let smooth = parse(#"<p:transition NS spd="fast"><p:fade/></p:transition>"#)
        let push = parse(#"<p:transition NS spd="med"><p:push dir="u"/></p:transition>"#)
        let cut = parse(#"<p:transition NS><p:cut/></p:transition>"#)
        check("pptx: fade through black 2s", black?.0 == .black && black?.1 == 2)
        check("pptx: smooth fade, fast = 0.5s", smooth?.0 == .dissolve && smooth?.1 == 0.5)
        check("pptx: other effects → dissolve", push?.0 == .dissolve && push?.1 == 0.75)
        check("pptx: cut", cut?.0 == .cut)
        let k = DeckBuilder.keynoteTransition(SlideConverter.SlideMeta(skipped: false, movies: [], audio: [], effect: "fade through color", duration: 1.5))
        let none = DeckBuilder.keynoteTransition(SlideConverter.SlideMeta(skipped: false, movies: [], audio: [], effect: "no transition effect", duration: 1))
        check("keynote: fade through color → black", k?.0 == .black && k?.1 == 1.5 && none == nil)
    }
}

extension SelfTest {
    /// フェード IN の測定：KEYCUE_SELFTEST_FADE=<動画> を Q に入れ、フェード 2 秒で鳴らして、映像の不透明度と音のレベルを 0.1 秒ごとに記録する
    static func runFade(model: AppModel, file: URL) {
        let q = UInt16(kVK_ANSI_Q)
        model.masterVolume = 0.05
        model.assign(file, to: q)
        var a = model.assignments[q]!
        a.fadeIn = 2
        model.update(q, a)
        var pressed: Date?
        var round = 0
        Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { timer in
            guard pressed != nil else {
                if model.info(for: q)?.loading ?? true { return }
                log("loaded: video=\(model.info(for: q)?.hasVideo == true) clip=\(model.info(for: q)?.clip != nil) audioViaPlayer=\(model.info(for: q)?.audioViaPlayer == true)")
                pressed = Date()
                model.handleKey(code: q, down: true, shift: false, isRepeat: false)
                return
            }
            let t = Date().timeIntervalSince(pressed!)
            if t < 0 { return }
            let layers = model.video.outputView.layer?.sublayers?.compactMap { $0 as? AVPlayerLayer } ?? []
            let opacity = layers.map { $0.presentation()?.opacity ?? $0.opacity }
            let ready = layers.map { $0.isReadyForDisplay }
            let vt = layers.last?.player.map { String(format: "%.2f", $0.currentTime().seconds) } ?? "-"
            let st = model.cueState.cues.first { $0.key == q }.flatMap { model.values.cues[$0.id]?.position }.map { String(format: "%.2f", $0) } ?? "-"
            let src = model.values.sourceMeters[q] ?? []
            log(String(format: "t=%.2f  video=%@ audio=%@ opacity=%@ ready=%@  level=%@", t, vt, st, opacity.map { String(format: "%.2f", $0) }.joined(separator: ","),
                       ready.map { $0 ? "y" : "n" }.joined(separator: ","), src.map { String(format: "%.3f", $0) }.joined(separator: ",")))
            if t > 1.2 && round == 0 {
                // 2 回目：動画が用意済みのとき
                round = 1
                model.stopAll(immediate: true)
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    log("--- second press (prepared player)")
                    pressed = Date()
                    model.handleKey(code: q, down: true, shift: false, isRepeat: false)
                }
                pressed = Date.distantFuture
            }
            else if t > 1.2 && round == 1 { timer.invalidate(); exit(0) }
        }
    }
}

extension SelfTest {
    /// 数値欄の確定の確認：KEYCUE_SELFTEST_FIELD=<素材>。フェード IN 欄に「2」を打ち、Enter を押さずにキーを押したときに値が入っているか
    static func runField(model: AppModel, file: URL) {
        let q = UInt16(kVK_ANSI_Q)
        model.assign(file, to: q)
        model.selectedKey = q
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            if model.info(for: q)?.loading ?? true { return }
            timer.invalidate()
            guard let window = NSApp.mainWindow ?? NSApp.windows.first(where: { $0.isVisible && $0.title.hasPrefix("KeyCue") }) else { log("no window"); exit(1) }
            var fields: [NSTextField] = []
            func walk(_ v: NSView) {
                if let f = v as? NSTextField, f.isEditable { fields.append(f) }
                v.subviews.forEach(walk)
            }
            walk(window.contentView!)
            for f in fields { log("field '\(f.stringValue)' at \(f.convert(f.bounds, to: nil))") }
            // 詳細パネルの最初の数値欄（フェード IN）
            guard let field = fields.filter({ $0.stringValue == "0" || $0.stringValue == "0.5" || $0.stringValue == "1" }).sorted(by: {
                let a = $0.convert($0.bounds, to: nil), b = $1.convert($1.bounds, to: nil)
                return a.minY == b.minY ? a.minX < b.minX : a.minY > b.minY
            }).first else { log("no field"); exit(1) }
            log("using field '\(field.stringValue)' fadeIn=\(model.assignments[q]!.fadeIn)")
            window.makeFirstResponder(field)
            field.currentEditor()?.string = "2"
            (field.currentEditor() as? NSTextView)?.didChangeText()
            // Enter を押さず、キーボードの図をクリックしたのと同じ：入力欄から外す
            window.makeFirstResponder(nil)
            log("right after leaving the field: fadeIn=\(model.assignments[q]!.fadeIn)")
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                let ok = model.assignments[q]!.fadeIn == 2
                log("0.3s later: fadeIn=\(model.assignments[q]!.fadeIn)")
                log(ok ? "PASS  fade IN typed without Enter is kept" : "FAIL  fade IN typed without Enter is lost")
                exit(ok ? 0 : 1)
            }
        }
    }
}

extension SelfTest {
    /// サムネイル表示の確認：KEYCUE_SELFTEST_THUMBS=<素材フォルダ>（-pref.mainView thumbnails を付けて起動）
    static func runThumbs(model: AppModel, folder: URL) {
        func k(_ code: Int) -> UInt16 { UInt16(code) }
        model.masterVolume = 0
        let files: [(Int, String)] = [
            (kVK_ANSI_Q, "clip.mp4"), (kVK_ANSI_W, "stereo_44k.wav"), (kVK_ANSI_E, "slides.pdf"),
            (kVK_ANSI_R, "still.png"), (kVK_ANSI_T, "four_tracks.mov"), (kVK_ANSI_Y, "eight_ch.wav"),
        ]
        for (code, name) in files { model.assign(folder.appendingPathComponent(name), to: k(code)) }
        var a = model.assignments[k(kVK_ANSI_W)]!
        a.colorIndex = 3
        model.update(k(kVK_ANSI_W), a)
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            if step == 1 {
                if files.contains(where: { model.info(for: k($0.0))?.loading ?? true }) { step = 0; return }
                model.handleKey(code: k(kVK_ANSI_Q), down: true, shift: false, isRepeat: false)
                model.handleKey(code: k(kVK_ANSI_E), down: true, shift: false, isRepeat: false)
                model.handleKey(code: k(kVK_ANSI_E), down: true, shift: false, isRepeat: false)
                model.selectedKey = k(kVK_ANSI_W)
            }
            if step == 3 {
                // Q のカードをクリック（鳴っているので止まるはず）
                let before = model.isRunning(k(kVK_ANSI_Q))
                _ = clickCard(k(kVK_ANSI_Q))
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                    let ok = before && !model.isRunning(k(kVK_ANSI_Q)) && model.selectedKey == k(kVK_ANSI_Q)
                    log(ok ? "PASS  click on a playing card stops it and selects it" : "FAIL  click on Q card (running \(before) → \(model.isRunning(k(kVK_ANSI_Q))))")
                }
            }
            if step == 5 {
                shoot("thumbs")
                timer.invalidate()
                exit(0)
            }
        }
    }
}

extension SelfTest {
    /// グリッド・レイヤー表示のカードを、本物のマウス操作と同じ形でクリックする
    static func clickCard(_ code: UInt16) -> Bool {
        guard let w = NSApp.windows.first(where: { $0.title.hasPrefix("KeyCue") }), let root = w.contentView else { return false }
        func find(_ v: NSView) -> NSView? {
            if v.identifier?.rawValue == "card-\(code)" { return v }
            for s in v.subviews { if let f = find(s) { return f } }
            return nil
        }
        guard let card = find(root) else { return false }
        // 段がスクロールの外にあれば、見えるところまで動かす
        var ancestor = card.superview
        while let v = ancestor {
            if let sv = v as? NSScrollView, let doc = sv.documentView { doc.scrollToVisible(card.convert(card.bounds, to: doc)) }
            ancestor = v.superview
        }
        root.layoutSubtreeIfNeeded()
        let p = card.convert(NSPoint(x: card.bounds.midX, y: card.bounds.midY), to: nil)
        // アプリのイベントの流れに乗せる（本物のクリックと同じく、押した層・離した層が決まる）
        func post(_ type: NSEvent.EventType) {
            if let e = NSEvent.mouseEvent(with: type, location: p, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                          windowNumber: w.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1) {
                NSApp.postEvent(e, atStart: false)
            }
        }
        post(.leftMouseDown)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { post(.leftMouseUp) }
        return true
    }

    /// レイヤー表示の確認：KEYCUE_SELFTEST_LAYERS=<素材フォルダ>（-pref.mainView layers を付けて起動）
    static func runLayers(model: AppModel, folder: URL) {
        func k(_ code: Int) -> UInt16 { UInt16(code) }
        let q = k(kVK_ANSI_Q), w = k(kVK_ANSI_W), e = k(kVK_ANSI_E), r = k(kVK_ANSI_R), t = k(kVK_ANSI_T)
        model.masterVolume = 0
        let files: [(UInt16, String, Int)] = [(q, "clip.mp4", 5), (w, "stereo_44k.wav", 5), (e, "slides.pdf", 7), (r, "still.png", 7), (t, "four_tracks.mov", 3)]
        for (code, name, layer) in files {
            model.assign(folder.appendingPathComponent(name), to: code)
            model.setLayer(code, layer)
        }
        var results: [Bool] = []
        func check(_ name: String, _ ok: Bool) { results.append(ok); log("\(ok ? "PASS" : "FAIL")  \(name)") }
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            if step > 1 { log("step \(step): running=\(files.map(\.0).filter { model.isRunning($0) }.map(KeyboardLayout.label(for:))) cues=\(model.cueState.cues.map { "\(KeyboardLayout.label(for: $0.key))\($0.stopping ? "(stopping)" : "")" }) selected=\(model.selectedKey.map(KeyboardLayout.label(for:)) ?? "-")") }
            switch step {
            case 1:
                if files.contains(where: { model.info(for: $0.0)?.loading ?? true }) { step = 0; return }
                check("layers: audio key is not visual, video/image/slides are", !model.isVisual(model.assignments[w]!)
                      && [q, e, r, t].allSatisfy { model.isVisual(model.assignments[$0]!) })
                model.handleKey(code: r, down: true, shift: false, isRepeat: false)
            case 3:
                check("layers: R (layer 7) playing", model.isRunning(r))
                check("layers: click E card (same layer 7)", clickCard(e))
            case 4:
                if Prefs.layerPreviewMode {
                    // プレビューに組んだだけでは出ない。「ライブへ」で入れ替わる
                    check("preview: E is in preview of layer 7, not live yet", model.layerPreview[7] == .some(e) && !model.isRunning(e) && model.isRunning(r))
                    model.take(smooth: false)
                    check("preview: take (cut) → E live, R off", model.isRunning(e) && !model.isRunning(r) && model.layerPreview.isEmpty)
                    check("preview: program monitor shows video", model.video.programView.layer?.sublayers?.contains { !($0 is AVPlayerLayer) && $0.contents != nil } == true)
                } else {
                    check("layers: E replaced R on layer 7", model.isRunning(e) && !model.isRunning(r))
                }
                _ = clickCard(q)
            case 5:
                if Prefs.layerPreviewMode {
                    model.take(smooth: true)
                }
            case 6:
                check("layers: Q on layer 5 plays alongside E", model.isRunning(q) && model.isRunning(e))
                if Prefs.layerPreviewMode {
                    model.previewClear(layer: 7)
                    model.take(smooth: false)
                    check("preview: clear layer 7 → E off, Q stays", !model.isRunning(e) && model.isRunning(q))
                }
                model.setLayer(t, 9)
                check("layers: T moved to layer 9", model.assignments[t]?.layer == 9)
            case 8:
                shoot("layers")
                log(results.allSatisfy { $0 } ? "ALL PASS (layers)" : "FAILED: layers")
                timer.invalidate()
                exit(results.allSatisfy { $0 } ? 0 : 1)
            default:
                break
            }
        }
    }
}

extension SelfTest {
    /// 透明部分のある動画の確認：KEYCUE_SELFTEST_ALPHA=<テロップ動画> KEYCUE_SELFTEST_ALPHA_BG=<背景の動画>
    /// 背景をレイヤー 5、テロップをレイヤー 7 で流して、映像出力を撮る
    static func runAlpha(model: AppModel, file: URL, background: URL) {
        let q = UInt16(kVK_ANSI_Q), w = UInt16(kVK_ANSI_W)
        model.masterVolume = 0
        model.assign(background, to: q)
        model.setLayer(q, 5)
        model.assign(file, to: w)
        model.setLayer(w, 7)
        var a = model.assignments[q]!
        a.loop = true
        model.update(q, a)
        model.video.show()
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            if step == 1 {
                if model.info(for: q)?.loading ?? true || model.info(for: w)?.loading ?? true { step = 0; return }
                log("alpha file: playURL=\(model.info(for: w)?.playURL?.lastPathComponent ?? "-") error=\(model.info(for: w)?.error ?? "-")")
                model.handleKey(code: q, down: true, shift: false, isRepeat: false)
                model.handleKey(code: w, down: true, shift: false, isRepeat: false)
            }
            if step == 6 {
                shoot("alpha")
                timer.invalidate()
                exit(0)
            }
        }
    }
}

extension SelfTest {
    /// アニメーション（rle ）形式の展開：そのまま並ぶ画素・同じ色の続き・飛ばし・前のコマとの差分
    static func animationDecoderTest(_ check: (String, Bool) -> Void) {
        let d = AnimationDecoder(width: 4, height: 2, depth: 32)
        func frame(_ body: [UInt8]) -> Data { Data([0, 0, 0, UInt8(6 + body.count)] + [0, 0] + body) }
        // 1 行目：2 画素そのまま（赤・半透明の緑）、2 画素同じ色（青）。2 行目：2 画素飛ばして白 1 画素
        d.decode(frame([1, 2, 255, 255, 0, 0, 128, 0, 255, 0, 0xFE, 255, 0, 0, 255, 0xFF,
                        3, 1, 255, 255, 255, 255, 0xFF]))
        let pb = { () -> [UInt8] in
            var buffer: CVPixelBuffer?
            CVPixelBufferCreate(nil, 4, 2, kCVPixelFormatType_32BGRA, nil, &buffer)
            d.copy(to: buffer!)
            CVPixelBufferLockBaseAddress(buffer!, .readOnly)
            defer { CVPixelBufferUnlockBaseAddress(buffer!, .readOnly) }
            let base = CVPixelBufferGetBaseAddress(buffer!)!.assumingMemoryBound(to: UInt8.self)
            let bpr = CVPixelBufferGetBytesPerRow(buffer!)
            return (0..<2).flatMap { y in (0..<16).map { base[y * bpr + $0] } }
        }
        let first = pb()
        // BGRA
        check("animation: literal + run pixels (BGRA, alpha kept)",
              Array(first[0..<16]) == [0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 255, 255, 0, 0, 255])
        check("animation: skip then 1 pixel", Array(first[16..<32]) == [0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0])
        // 次のコマ：4 バイト未満なら前と同じ。2 行目だけ書き換える（0x0008：開始行 1・1 行）
        d.decode(Data([0, 0, 0, 4]))
        d.decode(Data([0, 0, 0, 21, 0, 8, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, 64, 1, 2, 3, 0xFF]))
        let second = pb()
        check("animation: delta frame keeps line 1, updates line 2",
              Array(second[0..<16]) == Array(first[0..<16]) && Array(second[16..<20]) == [3, 2, 1, 64])
    }
}

extension SelfTest {
    /// 録画の確認：KEYCUE_SELFTEST_RECORD=<素材フォルダ>
    /// 動画と音を流しながら H.264（mp4）と ProRes（mov）を同時に 4 秒録り、できたファイルを開いて確かめる
    static func runRecord(model: AppModel, folder: URL) {
        let q = UInt16(kVK_ANSI_Q), w = UInt16(kVK_ANSI_W)
        model.masterVolume = 0.1
        model.assign(folder.appendingPathComponent("clip.mp4"), to: q)
        model.assign(folder.appendingPathComponent("stereo_44k.wav"), to: w)
        let out = FileManager.default.temporaryDirectory.appendingPathComponent("keycue-record-test-\(UUID().uuidString)")
        let specs = [
            OutputSpec(name: "h264", kind: .record, folder: out.path, fileName: "a_{name}", container: "mp4", videoBitrateKbps: 8000),
            OutputSpec(name: "prores", kind: .record, folder: out.path, fileName: "b_{name}", container: "mov", videoCodec: "prores", audioCodec: "pcm"),
        ]
        var results: [Bool] = []
        func check(_ name: String, _ ok: Bool) { results.append(ok); log("\(ok ? "PASS" : "FAIL")  \(name)") }
        var step = 0
        var files: [URL] = []
        var stopAsked = Date()
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            switch step {
            case 1:
                if [q, w].contains(where: { model.info(for: $0)?.loading ?? true }) { step = 0; return }
                model.handleKey(code: q, down: true, shift: false, isRepeat: false)
                model.handleKey(code: w, down: true, shift: false, isRepeat: false)
            case 2:
                for s in specs {
                    do { try model.feed.start(s) } catch { check("start \(s.name): \(error.localizedDescription)", false) }
                }
                files = model.feed.outputs.map { URL(fileURLWithPath: $0.target) }
                check("record: two outputs running at once", model.feed.outputs.count == 2 && model.feed.isActive(.record))
            case 6:
                shoot("record-main")
                AppDelegate.openSettings(tab: 4)
            case 7:
                shoot("record-settings")
            case 10:
                for o in model.feed.outputs { log("  \(o.spec.name): \(Int(o.bitrateKbps)) kbps \(o.encodeFps) fps dropped=\(o.droppedFrames) \(o.error ?? "")") }
                model.feed.stopAll(.record)
                stopAsked = Date()
            case 11...40:
                guard model.feed.outputs.isEmpty else { return }
                let took = Date().timeIntervalSince(stopAsked)
                log("  stop took \(String(format: "%.1f", took)) s")
                check("record: stopping finishes quickly", took < 6)
                step = 41
                timer.invalidate()
                Task { @MainActor in
                    for f in files { await verify(f, check) }
                    check("record: feed stopped (no tap, no capture)", !model.video.captureFrames)
                    if let keep = ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_KEEP"] {
                        try? FileManager.default.moveItem(at: out, to: URL(fileURLWithPath: keep))
                    } else {
                        try? FileManager.default.removeItem(at: out)
                    }
                    log(results.allSatisfy { $0 } ? "ALL PASS (record)" : "FAILED: record")
                    exit(results.allSatisfy { $0 } ? 0 : 1)
                }
            case 41...:
                break
            default:
                break
            }
            if step == 40 {
                check("record: outputs stopped in time", false)
                exit(1)
            }
        }
    }

    private static func verify(_ url: URL, _ check: (String, Bool) -> Void) async {
        let name = url.lastPathComponent
        let asset = AVURLAsset(url: url)
        guard let duration = try? await asset.load(.duration),
              let video = try? await asset.loadTracks(withMediaType: .video).first,
              let audio = try? await asset.loadTracks(withMediaType: .audio).first else {
            check("\(name): opens with video and audio tracks", false)
            return
        }
        let size = (try? await video.load(.naturalSize)) ?? .zero
        let fps = (try? await video.load(.nominalFrameRate)) ?? 0
        let secs = duration.seconds
        log("  \(name): \(Int(size.width))x\(Int(size.height)) \(fps) fps \(String(format: "%.2f", secs)) s")
        check("\(name): program size \(ProgramFeed.width)x\(ProgramFeed.height)", Int(size.width) == ProgramFeed.width && Int(size.height) == ProgramFeed.height)
        check("\(name): about 4 s long", secs > 3.0 && secs < 5.5)
        check("\(name): frame rate", abs(Double(fps) - ProgramFeed.fps) < 1.5)
        // 映像：動画が映っている（真っ黒でない）
        let gen = AVAssetImageGenerator(asset: asset)
        gen.requestedTimeToleranceBefore = .zero
        gen.requestedTimeToleranceAfter = .zero
        if let image = try? await gen.image(at: CMTime(seconds: 2, preferredTimescale: 600)).image {
            let rep = NSBitmapImageRep(cgImage: image)
            var sum = 0.0
            var n = 0
            for y in stride(from: 0, to: rep.pixelsHigh, by: 17) {
                for x in stride(from: 0, to: rep.pixelsWide, by: 17) {
                    if let c = rep.colorAt(x: x, y: y) { sum += Double(c.redComponent + c.greenComponent + c.blueComponent); n += 1 }
                }
            }
            let mean = sum / Double(max(n, 1)) / 3
            log("  \(name): mean brightness \(String(format: "%.3f", mean))")
            check("\(name): picture shows the clip (not black)", mean > 0.03)
        } else {
            check("\(name): read a frame", false)
        }
        // 音：無音でない
        if let reader = try? AVAssetReader(asset: asset) {
            let o = AVAssetReaderTrackOutput(track: audio, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 32,
                                                                            AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false])
            reader.add(o)
            reader.startReading()
            var peak: Float = 0
            while let sb = o.copyNextSampleBuffer() {
                guard let bb = CMSampleBufferGetDataBuffer(sb) else { continue }
                var len = 0
                var ptr: UnsafeMutablePointer<CChar>?
                CMBlockBufferGetDataPointer(bb, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &len, dataPointerOut: &ptr)
                guard let ptr else { continue }
                ptr.withMemoryRebound(to: Float.self, capacity: len / 4) { p in
                    for i in 0..<(len / 4) { peak = max(peak, abs(p[i])) }
                }
            }
            log("  \(name): audio peak \(String(format: "%.3f", peak))")
            check("\(name): audio is not silent", peak > 0.005)
        }
    }
}

extension SelfTest {
    /// 仮想カメラの確認：KEYCUE_SELFTEST_VCAM=<素材フォルダ>（仮想カメラを入れてある Mac で）
    /// 動画を流して仮想カメラを始め、カメラ「KeyCue」を自分で開いて、届いたコマが動画かどうかを見る
    static func runVirtualCamera(model: AppModel, folder: URL) {
        let q = UInt16(kVK_ANSI_Q)
        model.masterVolume = 0
        model.assign(folder.appendingPathComponent("clip.mp4"), to: q)
        var results: [Bool] = []
        func check(_ name: String, _ ok: Bool) { results.append(ok); log("\(ok ? "PASS" : "FAIL")  \(name)") }
        let sink = CameraProbe()
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { timer in
            step += 1
            switch step {
            case 1:
                if model.info(for: q)?.loading ?? true { step = 0; return }
                model.handleKey(code: q, down: true, shift: false, isRepeat: false)
                model.virtualCamera.start()
            case 2...20:
                // 拡張を入れ替えているあいだは待つ
                guard model.virtualCamera.state == .running || step == 20 else { return }
                log("  camera state: \(model.virtualCamera.state)")
                check("vcam: running", model.virtualCamera.state == .running)
                sink.start()
                step = 21
            case 27:
                log("  frames sent: \(model.virtualCamera.framesSent), received: \(sink.frames) (black \(sink.blackFrames)), brightness: \(String(format: "%.3f", sink.brightness)) \(sink.error ?? "")")
                check("vcam: KeyCue sent frames to the camera", model.virtualCamera.framesSent > 30)
                check("vcam: camera delivered frames", sink.frames > 10)
                check("vcam: picture is the clip (not black)", sink.brightness > 0.05)
                check("vcam: no black frames mixed in", sink.blackFrames < sink.frames / 10)
                sink.stop()
                model.virtualCamera.stop()
                log(results.allSatisfy { $0 } ? "ALL PASS (vcam)" : "FAILED: vcam")
                timer.invalidate()
                exit(results.allSatisfy { $0 } ? 0 : 1)
            default:
                break
            }
        }
    }
}

/// カメラ「KeyCue」を開いてコマを数える（テスト用）
final class CameraProbe: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "KeyCue.CameraProbe")
    private(set) var frames = 0
    private(set) var blackFrames = 0
    private(set) var brightness = 0.0
    private(set) var error: String?

    func start() {
        guard let device = AVCaptureDevice(uniqueID: VirtualCamera.deviceUID) else { error = "no KeyCue camera"; return }
        do {
            let input = try AVCaptureDeviceInput(device: device)
            session.addInput(input)
            let out = AVCaptureVideoDataOutput()
            out.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
            out.setSampleBufferDelegate(self, queue: queue)
            session.addOutput(out)
            session.startRunning()
        } catch {
            self.error = error.localizedDescription
        }
    }

    func stop() { session.stopRunning() }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        frames += 1
        guard let pb = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        CVPixelBufferLockBaseAddress(pb, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pb, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pb) else { return }
        let w = CVPixelBufferGetWidth(pb), h = CVPixelBufferGetHeight(pb), bpr = CVPixelBufferGetBytesPerRow(pb)
        var sum = 0, n = 0
        for y in stride(from: 0, to: h, by: 37) {
            let row = base.advanced(by: y * bpr).assumingMemoryBound(to: UInt8.self)
            for x in stride(from: 0, to: w, by: 37) { sum += Int(row[x * 4]) + Int(row[x * 4 + 1]) + Int(row[x * 4 + 2]); n += 1 }
        }
        let b = Double(sum) / Double(max(n, 1)) / 3 / 255
        if b < 0.02 { blackFrames += 1 } else { brightness = b }
    }
}
