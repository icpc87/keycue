; KeyCue for Windows のインストーラー（NSIS）。release-windows.sh から作る:
;   makensis -DVERSION=1.5.1 -DARCH=x64 -DSRC=<publish したフォルダ> -DICON=<KeyCue.ico> -DOUTFILE=<出力> KeyCue.nsi
; 管理者の許可なしで %LOCALAPPDATA%\Programs\KeyCue に入れる（アプリ内のアップデートで入れ替えられるように）。
; スタートメニュー・デスクトップのショートカット、.keycue の関連付け、「アプリと機能」からの削除を用意する。

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user
!include "MUI2.nsh"
!include "x64.nsh"

!define APP "KeyCue"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\KeyCue"

Name "${APP} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\KeyCue"
InstallDirRegKey HKCU "${UNINSTKEY}" "InstallLocation"
BrandingText "KeyCue ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "KeyCue"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "KeyCue インストーラー"
VIAddVersionKey "LegalCopyright" "© 2026 Hiroki Ichiyanagi (GPL-3.0)"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\KeyCue.exe"
!define MUI_FINISHPAGE_RUN_TEXT "KeyCue を起動する"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${SRC}/LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Japanese"

Function .onInit
!if "${ARCH}" == "x64"
  ; ARM64 の PC には ARM64 版を案内する（x64 版もエミュレーションで動くので止めはしない）
  ${If} ${IsNativeARM64}
    MessageBox MB_YESNO|MB_ICONQUESTION "この PC は ARM64 です。ARM64 版のほうが速く動きます。このまま x64 版を入れますか？" IDYES +2
    Abort
  ${EndIf}
!else
  ${IfNot} ${IsNativeARM64}
    MessageBox MB_ICONSTOP "この PC は ARM64 ではありません。x64 版のインストーラーを使ってください。"
    Abort
  ${EndIf}
!endif
FunctionEnd

Section "KeyCue"
  SetOutPath "$INSTDIR"
  File /r "${SRC}/*.*"
  WriteUninstaller "$INSTDIR\Uninstall KeyCue.exe"

  CreateShortcut "$SMPROGRAMS\KeyCue.lnk" "$INSTDIR\KeyCue.exe"
  CreateShortcut "$DESKTOP\KeyCue.lnk" "$INSTDIR\KeyCue.exe"

  ; .keycue（キーマップ）をダブルクリックで開く
  WriteRegStr HKCU "Software\Classes\.keycue" "" "KeyCue.Keymap"
  WriteRegStr HKCU "Software\Classes\KeyCue.Keymap" "" "KeyCue キーマップ"
  WriteRegStr HKCU "Software\Classes\KeyCue.Keymap\DefaultIcon" "" "$INSTDIR\KeyCue.exe,0"
  WriteRegStr HKCU "Software\Classes\KeyCue.Keymap\shell\open\command" "" '"$INSTDIR\KeyCue.exe" "%1"'

  ; 「アプリと機能」に出す
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "KeyCue"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "ic2"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://keycue.ic2.jp"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\KeyCue.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall KeyCue.exe"'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\KeyCue.lnk"
  Delete "$DESKTOP\KeyCue.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "Software\Classes\KeyCue.Keymap"
  ReadRegStr $1 HKCU "Software\Classes\.keycue" ""
  StrCmp $1 "KeyCue.Keymap" 0 +2
  DeleteRegKey HKCU "Software\Classes\.keycue"
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd
