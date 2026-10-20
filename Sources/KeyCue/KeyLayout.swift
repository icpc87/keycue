import AppKit
import Carbon.HIToolbox
import SwiftUI

/// 画面上のキー 1 つ分
struct KeySpec: Identifiable, Hashable {
    enum Kind { case key, stopAll, pad, gap }
    let id: String
    let code: UInt16
    let label: String
    let width: CGFloat
    let kind: Kind
}

struct KeyRow: Identifiable {
    let id: Int
    let height: CGFloat
    let keys: [KeySpec]
}

/// キーボード配列（US / JIS）。幅はキー 1 個分を 1 とした単位で、各行の合計は 15。
enum KeyboardLayout {
    static let unitsWide: CGFloat = 15

    private enum E {
        case k(Int, String, CGFloat = 1)
        case esc
        case pad(String, CGFloat)
        case gap(CGFloat)
    }

    /// 設定で US / JIS を選べる（自動なら接続中のキーボードから判断）
    static let isJIS: Bool = {
        switch Prefs.keyboardLayout {
        case "us": return false
        case "jis": return true
        default: return KBGetLayoutType(Int16(LMGetKbdType())) == kKeyboardJIS
        }
    }()

    static let rows: [KeyRow] = isJIS ? jisRows : usRows

    static var unitsHigh: CGFloat { rows.reduce(0) { $0 + $1.height } }

    /// ドロップで複数ファイルを順に割り当てるときの並び
    static let fillOrder: [UInt16] = {
        var seen = Set<UInt16>()
        var order: [UInt16] = []
        for row in rows {
            for key in row.keys where key.kind == .key && !seen.contains(key.code) {
                seen.insert(key.code)
                order.append(key.code)
            }
        }
        return order
    }()

    static let labels: [UInt16: String] = {
        var m: [UInt16: String] = [:]
        for row in rows { for key in row.keys where key.kind == .key || key.kind == .stopAll { m[key.code] = key.label } }
        return m
    }()

    static func label(for code: UInt16) -> String { labels[code] ?? "#\(code)" }

    private static func build(_ spec: [(CGFloat, [E])]) -> [KeyRow] {
        spec.enumerated().map { rowIndex, row in
            let keys: [KeySpec] = row.1.enumerated().map { i, e in
                let id = "\(rowIndex)-\(i)"
                switch e {
                case let .k(code, label, w):
                    return KeySpec(id: id, code: UInt16(code), label: label, width: w, kind: .key)
                case .esc:
                    return KeySpec(id: id, code: UInt16(kVK_Escape), label: "ESC", width: 1, kind: .stopAll)
                case let .pad(label, w):
                    return KeySpec(id: id, code: 0xFFFF, label: label, width: w, kind: .pad)
                case let .gap(w):
                    return KeySpec(id: id, code: 0xFFFF, label: "", width: w, kind: .gap)
                }
            }
            return KeyRow(id: rowIndex, height: row.0, keys: keys)
        }
    }

    private static let functionRow: [E] = [
        .esc, .gap(1),
        .k(kVK_F1, "F1"), .k(kVK_F2, "F2"), .k(kVK_F3, "F3"), .k(kVK_F4, "F4"), .gap(0.5),
        .k(kVK_F5, "F5"), .k(kVK_F6, "F6"), .k(kVK_F7, "F7"), .k(kVK_F8, "F8"), .gap(0.5),
        .k(kVK_F9, "F9"), .k(kVK_F10, "F10"), .k(kVK_F11, "F11"), .k(kVK_F12, "F12"),
    ]

    private static let digits: [E] = [
        .k(kVK_ANSI_1, "1"), .k(kVK_ANSI_2, "2"), .k(kVK_ANSI_3, "3"), .k(kVK_ANSI_4, "4"), .k(kVK_ANSI_5, "5"),
        .k(kVK_ANSI_6, "6"), .k(kVK_ANSI_7, "7"), .k(kVK_ANSI_8, "8"), .k(kVK_ANSI_9, "9"), .k(kVK_ANSI_0, "0"),
    ]
    private static let qRow: [E] = [
        .k(kVK_ANSI_Q, "Q"), .k(kVK_ANSI_W, "W"), .k(kVK_ANSI_E, "E"), .k(kVK_ANSI_R, "R"), .k(kVK_ANSI_T, "T"),
        .k(kVK_ANSI_Y, "Y"), .k(kVK_ANSI_U, "U"), .k(kVK_ANSI_I, "I"), .k(kVK_ANSI_O, "O"), .k(kVK_ANSI_P, "P"),
    ]
    private static let aRow: [E] = [
        .k(kVK_ANSI_A, "A"), .k(kVK_ANSI_S, "S"), .k(kVK_ANSI_D, "D"), .k(kVK_ANSI_F, "F"), .k(kVK_ANSI_G, "G"),
        .k(kVK_ANSI_H, "H"), .k(kVK_ANSI_J, "J"), .k(kVK_ANSI_K, "K"), .k(kVK_ANSI_L, "L"),
    ]
    private static let zRow: [E] = [
        .k(kVK_ANSI_Z, "Z"), .k(kVK_ANSI_X, "X"), .k(kVK_ANSI_C, "C"), .k(kVK_ANSI_V, "V"), .k(kVK_ANSI_B, "B"),
        .k(kVK_ANSI_N, "N"), .k(kVK_ANSI_M, "M"), .k(kVK_ANSI_Comma, ","), .k(kVK_ANSI_Period, "."), .k(kVK_ANSI_Slash, "/"),
    ]
    private static let arrows: [E] = [.k(kVK_LeftArrow, "◀"), .k(kVK_DownArrow, "▼"), .k(kVK_RightArrow, "▶")]

    private static let usRows: [KeyRow] = build([
        (0.75, functionRow),
        (1, [.k(kVK_ANSI_Grave, "`")] + digits + [.k(kVK_ANSI_Minus, "-"), .k(kVK_ANSI_Equal, "="), .k(kVK_Delete, "DELETE", 2)]),
        (1, [.k(kVK_Tab, "TAB", 1.5)] + qRow + [.k(kVK_ANSI_LeftBracket, "["), .k(kVK_ANSI_RightBracket, "]"), .k(kVK_ANSI_Backslash, "\\", 1.5)]),
        (1, [.pad("caps", 1.75)] + aRow + [.k(kVK_ANSI_Semicolon, ";"), .k(kVK_ANSI_Quote, "'"), .k(kVK_Return, "ENTER", 2.25)]),
        (1, [.pad("shift", 2.25)] + zRow + [.pad("shift", 0.75), .k(kVK_UpArrow, "▲"), .gap(1)]),
        (1, [.pad("fn", 1), .pad("control", 1), .pad("option", 1), .pad("command", 1.25), .k(kVK_Space, "SPACE", 5.5),
             .pad("command", 1.25), .pad("option", 1)] + arrows),
    ])

    private static let jisRows: [KeyRow] = build([
        (0.75, functionRow),
        (1, digits + [.k(kVK_ANSI_Minus, "-"), .k(kVK_ANSI_Equal, "^"), .k(kVK_JIS_Yen, "¥"), .k(kVK_Delete, "DELETE", 2)]),
        (1, [.k(kVK_Tab, "TAB", 1.5)] + qRow + [.k(kVK_ANSI_LeftBracket, "@"), .k(kVK_ANSI_RightBracket, "["), .k(kVK_Return, "ENTER", 1.5)]),
        (1, [.pad("control", 1.75)] + aRow + [.k(kVK_ANSI_Semicolon, ";"), .k(kVK_ANSI_Quote, ":"), .k(kVK_ANSI_Backslash, "]"), .k(kVK_Return, "ENTER", 1.25)]),
        (1, [.pad("shift", 2)] + zRow + [.k(kVK_JIS_Underscore, "_"), .k(kVK_UpArrow, "▲"), .gap(1)]),
        (1, [.pad("caps", 1), .pad("option", 1), .pad("command", 1.25), .pad("英数", 1), .k(kVK_Space, "SPACE", 4.5),
             .pad("かな", 1), .pad("command", 1.25), .pad("fn", 1)] + arrows),
    ])
}

enum KeyPalette {
    static let colors: [Color] = [
        Color(red: 0.22, green: 0.38, blue: 0.58),
        Color(red: 0.46, green: 0.49, blue: 0.26),
        Color(red: 0.80, green: 0.42, blue: 0.38),
        Color(red: 0.55, green: 0.66, blue: 0.77),
        Color(red: 0.28, green: 0.55, blue: 0.47),
        Color(red: 0.55, green: 0.40, blue: 0.64),
        Color(red: 0.78, green: 0.60, blue: 0.28),
        Color(red: 0.64, green: 0.30, blue: 0.40),
        Color(red: 0.36, green: 0.60, blue: 0.30),
        Color(red: 0.82, green: 0.52, blue: 0.66),
        Color(red: 0.40, green: 0.45, blue: 0.72),
        Color(red: 0.66, green: 0.66, blue: 0.40),
    ]

    static func color(_ index: Int) -> Color { colors[((index % colors.count) + colors.count) % colors.count] }
}
