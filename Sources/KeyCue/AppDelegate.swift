import AppKit
import Combine
import ApplicationServices
import SwiftUI
import UniformTypeIdentifiers

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var window: NSWindow!
    private var model: AppModel!
    private var localMonitor: Any?
    private var mouseMonitor: Any?
    /// 利用者がクリックして入った入力欄（それ以外の入力欄にカーソルがあってもキー操作を優先する）
    private weak var clickedField: NSView?
    private var globalMonitor: Any?
    private var instanceLockFD: Int32 = -1
    private var deckTabsSink: AnyCancellable?
    private var pagesSink: AnyCancellable?

    static let keymapType = UTType(exportedAs: "jp.ic2.keycue.keymap", conformingTo: .json)

    func applicationDidFinishLaunching(_ notification: Notification) {
        Prefs.register()
        let instance = acquireInstanceNumber()
        model = AppModel(instance: instance)
        model.onTitleChange = { [weak self] in self?.updateTitle() }
        model.onBackgroundInputChange = { [weak self] on in self?.setBackgroundInput(on) }

        buildMenu()

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1500, height: 920),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        let hosting = NSHostingView(rootView: ContentView(model: model))
        // 中身の幅に合わせてウィンドウが勝手に広がらないようにする（狭いときは中身の方を詰める）
        hosting.sizingOptions = []
        window.contentView = hosting
        window.minSize = AppDelegate.baseMinSize
        window.isReleasedWhenClosed = false
        window.delegate = self
        let autosave = "KeyCueMain-\(instance)"
        if !window.setFrameUsingName(autosave) {
            window.center()
            let offset = CGFloat(instance - 1) * 30
            window.setFrameOrigin(NSPoint(x: window.frame.minX + offset, y: window.frame.minY - offset))
        }
        window.setFrameAutosaveName(autosave)
        fitWindowToScreen()

        // スライド素材のタブを開いたら、その分ウィンドウを広げる（閉じたら戻す）
        deckTabsSink = model.$deckTabs.map(\.isEmpty).removeDuplicates().dropFirst().sink { [weak self] empty in
            self?.resizeForDeckPanel(open: !empty)
        }
        // プレイリストや素材一覧を出したら、中央が潰れないよう最小幅を合わせる
        pagesSink = model.$showPages.removeDuplicates().sink { [weak self] _ in
            DispatchQueue.main.async { self?.updateMinWidth() }
        }
        updateMinWidth()
        model.start()
        if SelfTest.folder != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_KEYS"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_LAYOUT"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_DEMO"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_PERF"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_FADE"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_FIELD"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_THUMBS"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_LAYERS"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_ALPHA"] != nil || ProcessInfo.processInfo.environment["KEYCUE_SELFTEST_RECORD"] != nil {
            SelfTest.run(model: model)
        } else {
            model.restoreLastDocument()
            if Prefs.openVideoOnLaunch { model.video.show() }
            // 多重起動のときは最初のインスタンスだけが確認する
            if instance == 1 { Updater.shared.checkOnLaunch() }
            // 動作確認用：起動してすぐ確認する
            if ProcessInfo.processInfo.environment["KEYCUE_UPDATE_CHECK_NOW"] != nil {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { Updater.shared.check(userInitiated: true) }
            }
        }
        updateTitle()
        installLocalMonitor()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    static let baseMinSize = NSSize(width: 980, height: 680)

    /// ウィンドウを画面の中に収める（前回の位置・大きさが画面より大きいときも）
    private func fitWindowToScreen(animate: Bool = false) {
        guard let window, !window.styleMask.contains(.fullScreen),
              let visible = (window.screen ?? NSScreen.main)?.visibleFrame else { return }
        var frame = window.frame
        frame.size.width = min(frame.width, visible.width)
        frame.size.height = min(frame.height, visible.height)
        frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
        frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
        if frame != window.frame { window.setFrame(frame, display: true, animate: animate) }
    }

    /// 素材一覧のタブを開いたら、画面に収まる範囲でその分ウィンドウを広げる（閉じたら戻す）
    private func resizeForDeckPanel(open: Bool) {
        guard let window, !window.isZoomed, !window.styleMask.contains(.fullScreen) else { return }
        let width = DeckPanel.width + 1
        var frame = window.frame
        if open {
            frame.size.width += width
        } else {
            frame.size.width = max(window.minSize.width, frame.size.width - width)
        }
        window.setFrame(frame, display: true, animate: true)
        DispatchQueue.main.async { self.updateMinWidth() }
    }

    /// 開いているパネルの分だけ最小幅を広げる（画面の幅が上限。足りなければ中身が自動で畳む）
    private func updateMinWidth() {
        guard let window else { return }
        let visible = (window.screen ?? NSScreen.main)?.visibleFrame.width ?? 1800
        var needed = ContentView.mainMinWidth + ContentView.sidebarWidth + 1
        if model.showPages { needed += ContentView.pagesWidth + 1 }
        if !model.deckTabs.isEmpty { needed += DeckPanel.width + 1 }
        window.minSize.width = min(max(needed, AppDelegate.baseMinSize.width), visible)
        if window.frame.width < window.minSize.width, !window.styleMask.contains(.fullScreen) {
            var frame = window.frame
            frame.size.width = window.minSize.width
            window.setFrame(frame, display: true, animate: true)
        }
        fitWindowToScreen()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        // アップデートを選んでいたら、終了後に新しい版と入れ替える
        Updater.shared.applicationWillTerminate()
        // 録画中のファイルを書き終えてから終わる
        model.feed.stopEverything()
    }

    func setStatus(_ text: String) { model.statusMessage = text }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if !model.feed.outputs.isEmpty {
            let a = NSAlert()
            a.messageText = "録画・配信をしています"
            a.informativeText = "止めて終了しますか？（録画中のファイルは書き終えてから終わります）"
            a.addButton(withTitle: "止めて終了")
            a.addButton(withTitle: "キャンセル")
            guard a.runModal() == .alertFirstButtonReturn else { return .terminateCancel }
        }
        return confirmDiscard() ? .terminateNow : .terminateCancel
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        sender !== window || confirmDiscard()
    }

    func windowWillClose(_ notification: Notification) {
        if (notification.object as? NSWindow) === window { model.video.hide() }
    }

    // MARK: - 多重起動

    /// 起動中のインスタンス同士で番号が重ならないよう、ロックファイルで空き番号を取る
    private func acquireInstanceNumber() -> Int {
        // 以前の簡易署名のビルドが KeyCue/ 直下に作ったファイルは開けないことがあるので、別のフォルダを使う
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("KeyCue/Instances", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for n in 1...99 {
            // 読み取り専用で開く（別ビルドが作ったファイルへの書き込みは macOS に拒否されるため）
            let fd = open(dir.appendingPathComponent("instance-\(n).lock").path, O_CREAT | O_RDONLY, 0o644)
            if fd < 0 { continue }
            if flock(fd, LOCK_EX | LOCK_NB) == 0 {
                instanceLockFD = fd
                return n
            }
            close(fd)
        }
        return 1
    }

    private var settingsWindow: NSWindow?

    @objc func showSettings(_ sender: Any?) { showSettings(tab: nil) }

    /// 設定画面を開く（tab: 0 映像 / 1 スクリーン / 2 音 / 3 一般 / 4 録画・配信）
    static func openSettings(tab: Int) {
        (NSApp.delegate as? AppDelegate)?.showSettings(tab: tab)
    }

    func showSettings(tab: Int?) {
        if settingsWindow == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 692, height: 592),
                             styleMask: [.titled, .closable], backing: .buffered, defer: false)
            w.title = "設定 — KeyCue #\(model.instanceNumber)"
            w.isReleasedWhenClosed = false
            w.center()
            settingsWindow = w
        }
        // 新しいウィンドウにも空の contentView があるので、設定画面を入れたかどうかで判断する
        if tab != nil || !(settingsWindow?.contentView is NSHostingView<SettingsView>) {
            settingsWindow?.contentView = NSHostingView(rootView: SettingsView(model: model, tab: tab ?? 0))
        }
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    @objc private func newInstance(_ sender: Any?) {
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: config) { _, error in
            if let error {
                DispatchQueue.main.async { NSAlert(error: error).runModal() }
            }
        }
    }

    // MARK: - キー入力

    private func installLocalMonitor() {
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self else { return event }
            return self.handleLocal(event) ? nil : event
        }
        // クリックで入力欄に入ったかを覚えておく（クリックの処理が終わってから確かめる）
        mouseMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown]) { [weak self] event in
            DispatchQueue.main.async {
                if let text = event.window?.firstResponder as? NSTextView, text.isEditable {
                    self?.clickedField = text.delegate as? NSView
                } else {
                    self?.clickedField = nil
                }
            }
            return event
        }
    }

    private func handleLocal(_ event: NSEvent) -> Bool {
        if !event.modifierFlags.intersection([.command, .control, .option]).isEmpty { return false }
        // 開く／保存パネルや確認ダイアログの間はそちらへ渡す
        if NSApp.modalWindow != nil || event.window is NSSavePanel || event.window?.attachedSheet != nil { return false }
        // 自分でクリックした入力欄に入力中ならそちらへ渡す。
        // 画面が切り替わったはずみで macOS が勝手に入力欄へカーソルを移したときは、外してキー操作を優先する
        if let text = event.window?.firstResponder as? NSTextView, text.isEditable {
            let owner = text.delegate as? NSView
            if let owner, owner === clickedField {
                if event.type == .keyDown, model.assignments[event.keyCode] != nil || event.keyCode == 53 {
                    model.statusMessage = "数値の入力欄が選ばれているため、キーで再生・停止できません（Enter で確定するか、キーボードの図をクリック）"
                }
                return false
            }
            event.window?.makeFirstResponder(nil)
        }
        model.handleKey(code: event.keyCode, down: event.type == .keyDown,
                        shift: event.modifierFlags.contains(.shift),
                        isRepeat: event.type == .keyDown && event.isARepeat)
        return true
    }

    private func setBackgroundInput(_ on: Bool) {
        if let globalMonitor { NSEvent.removeMonitor(globalMonitor) }
        globalMonitor = nil
        guard on else { return }
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(options) {
            model.statusMessage = "システム設定 › プライバシーとセキュリティ › アクセシビリティ で KeyCue を許可してください"
        }
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self, !NSApp.isActive else { return }
            if !event.modifierFlags.intersection([.command, .control, .option]).isEmpty { return }
            self.model.handleKey(code: event.keyCode, down: event.type == .keyDown,
                                 shift: event.modifierFlags.contains(.shift),
                                 isRepeat: event.type == .keyDown && event.isARepeat)
        }
    }

    // MARK: - ファイル

    private func updateTitle() {
        window.title = "KeyCue #\(model.instanceNumber) — \(model.documentName) — \(model.currentPageName)"
        window.representedURL = model.documentURL
        window.isDocumentEdited = model.isDirty
    }

    /// 未保存の変更があれば確認する。続けてよければ true
    private func confirmDiscard() -> Bool {
        guard model.isDirty else { return true }
        let alert = NSAlert()
        alert.messageText = "\(model.documentName) の変更を保存しますか？"
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "キャンセル")
        alert.addButton(withTitle: "保存しない")
        switch alert.runModal() {
        case .alertFirstButtonReturn: return saveDocument()
        case .alertThirdButtonReturn: return true
        default: return false
        }
    }

    @objc private func newDocument(_ sender: Any?) {
        guard confirmDiscard() else { return }
        model.newDocument()
    }

    @objc private func openDocument(_ sender: Any?) {
        guard confirmDiscard() else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [AppDelegate.keymapType, .json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try model.open(url) } catch { NSAlert(error: error).runModal() }
    }

    @objc private func save(_ sender: Any?) { _ = saveDocument() }

    @objc private func saveAs(_ sender: Any?) { _ = saveDocument(forcePanel: true) }

    @discardableResult
    private func saveDocument(forcePanel: Bool = false) -> Bool {
        var url = model.documentURL
        if url == nil || forcePanel {
            let panel = NSSavePanel()
            panel.allowedContentTypes = [AppDelegate.keymapType]
            panel.nameFieldStringValue = model.documentName
            guard panel.runModal() == .OK, let picked = panel.url else { return false }
            url = picked
        }
        do {
            try model.save(to: url!)
            return true
        } catch {
            NSAlert(error: error).runModal()
            return false
        }
    }

    @objc private func assignToSelected(_ sender: Any?) {
        if let code = model.selectedKey { model.chooseFile(for: code) }
    }

    @objc func assignLiveToSelected(_ sender: Any?) {
        if let code = model.selectedKey { model.chooseLiveInput(for: code) }
    }

    @objc private func stopAllFade(_ sender: Any?) { model.stopAll(immediate: false) }
    @objc private func stopAllNow(_ sender: Any?) { model.stopAll(immediate: true) }
    @objc private func newPage(_ sender: Any?) { model.newPage() }
    @objc private func duplicatePage(_ sender: Any?) { model.newPage(duplicate: true) }
    @objc private func nextPage(_ sender: Any?) { model.stepPage(by: 1) }
    @objc private func previousPage(_ sender: Any?) { model.stepPage(by: -1) }
    @objc private func togglePages(_ sender: Any?) { model.showPages.toggle() }

    @objc private func toggleVideoOutput(_ sender: Any?) { model.video.isShowing ? model.video.hide() : model.video.show() }
    @objc private func reloadMedia(_ sender: Any?) { model.reloadAll() }

    // MARK: - メニュー

    private func buildMenu() {
        let main = NSMenu()

        func submenu(_ title: String, _ items: [NSMenuItem]) {
            let item = NSMenuItem()
            let menu = NSMenu(title: title)
            items.forEach(menu.addItem)
            item.submenu = menu
            main.addItem(item)
        }
        func item(_ title: String, _ action: Selector, _ key: String = "", _ mods: NSEvent.ModifierFlags = [.command], target: AnyObject? = nil) -> NSMenuItem {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
            i.keyEquivalentModifierMask = mods
            i.target = target
            return i
        }

        submenu("KeyCue", [
            item("KeyCue について", #selector(NSApplication.orderFrontStandardAboutPanel(_:))),
            .separator(),
            item("設定…", #selector(showSettings(_:)), ",", target: self),
            .separator(),
            item("アップデートを確認…", #selector(Updater.checkNow(_:)), "", target: Updater.shared),
            .separator(),
            item("新しいインスタンスを起動", #selector(newInstance(_:)), "n", [.command, .option], target: self),
            .separator(),
            item("KeyCue を隠す", #selector(NSApplication.hide(_:)), "h"),
            item("ほかを隠す", #selector(NSApplication.hideOtherApplications(_:)), "h", [.command, .option]),
            .separator(),
            item("KeyCue を終了", #selector(NSApplication.terminate(_:)), "q"),
        ])
        submenu("ファイル", [
            item("新規キーマップ", #selector(newDocument(_:)), "n", target: self),
            item("開く…", #selector(openDocument(_:)), "o", target: self),
            .separator(),
            item("保存", #selector(save(_:)), "s", target: self),
            item("別名で保存…", #selector(saveAs(_:)), "s", [.command, .shift], target: self),
            .separator(),
            item("選択中のキーにファイルを割り当て…", #selector(assignToSelected(_:)), "i", target: self),
            item("選択中のキーにライブ入力を割り当て…", #selector(assignLiveToSelected(_:)), "", target: self),
            item("すべてのファイルを読み込み直す", #selector(reloadMedia(_:)), "r", [.command, .shift], target: self),
        ])
        submenu("編集", [
            item("取り消す", Selector(("undo:")), "z"),
            item("やり直す", Selector(("redo:")), "z", [.command, .shift]),
            .separator(),
            item("カット", #selector(NSText.cut(_:)), "x"),
            item("コピー", #selector(NSText.copy(_:)), "c"),
            item("ペースト", #selector(NSText.paste(_:)), "v"),
            item("すべてを選択", #selector(NSText.selectAll(_:)), "a"),
        ])
        submenu("再生", [
            item("全停止（フェード）", #selector(stopAllFade(_:)), ".", target: self),
            item("全停止（即時）", #selector(stopAllNow(_:)), ".", [.command, .shift], target: self),
            .separator(),
            item("映像出力を開く／閉じる", #selector(toggleVideoOutput(_:)), "f", [.command, .shift], target: self),
            .separator(),
            item("次のページ（プレイリスト）", #selector(nextPage(_:)), String(UnicodeScalar(NSRightArrowFunctionKey)!), target: self),
            item("前のページ（プレイリスト）", #selector(previousPage(_:)), String(UnicodeScalar(NSLeftArrowFunctionKey)!), target: self),
            item("新しいページ", #selector(newPage(_:)), "n", [.command, .shift], target: self),
            item("このページを複製", #selector(duplicatePage(_:)), "d", [.command, .shift], target: self),
            item("プレイリストを表示／隠す", #selector(togglePages(_:)), "l", [.command, .option], target: self),
        ])
        let windowMenu = NSMenu(title: "ウインドウ")
        windowMenu.addItem(item("しまう", #selector(NSWindow.performMiniaturize(_:)), "m"))
        let windowItem = NSMenuItem()
        windowItem.submenu = windowMenu
        main.addItem(windowItem)
        NSApp.windowsMenu = windowMenu

        NSApp.mainMenu = main
    }
}
