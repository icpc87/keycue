import AppKit
import SwiftUI

/// 秒数などの数値欄。打ったそばから値に入れる（Enter を押さずにキーボードの図をクリックしたり、キーを押したりしても失われない）
struct NumberField: View {
    @Binding var value: Double
    var width: CGFloat = 48
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField("", text: $text)
            .textFieldStyle(.roundedBorder)
            .labelsHidden()
            .frame(width: width)
            .focused($focused)
            .onAppear { text = Self.format(value) }
            .onChange(of: text) { _, t in
                if let v = Self.parse(t), v != value { value = v }
            }
            .onChange(of: value) { _, v in
                // 入力中は打った文字のまま（範囲で丸められても書き換えない）。外から変わったときだけ表示を合わせる
                if !focused, Self.parse(text) != v { text = Self.format(v) }
            }
            .onChange(of: focused) { _, f in
                if !f { text = Self.format(value) }
            }
            .onSubmit { NSApp.keyWindow?.makeFirstResponder(nil) }
    }

    /// 全角数字や「,」の小数点も受け付ける
    static func parse(_ t: String) -> Double? {
        let half = t.applyingTransform(.fullwidthToHalfwidth, reverse: false) ?? t
        return Double(half.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: "."))
    }

    static func format(_ v: Double) -> String {
        v.formatted(.number.precision(.fractionLength(0...2)).grouping(.never))
    }
}
