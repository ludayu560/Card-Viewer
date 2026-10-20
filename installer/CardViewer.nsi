Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

Name "Card Viewer"
OutFile "${SETUP_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Card Viewer"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "${APP_ICON}"
UninstallIcon "${APP_ICON}"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "Card Viewer"
VIAddVersionKey "FileDescription" "Card Viewer installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" ""
!define MUI_ABORTWARNING
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CardViewer"
Var Portable

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Card Viewer requires 64-bit Windows."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "Card Viewer requires Windows 10 or later."
    Abort
  ${EndIf}
  SetShellVarContext current
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/PORTABLE" $1
  StrCpy $Portable "0"
  ${IfNot} ${Errors}
    StrCpy $Portable "1"
  ${EndIf}
FunctionEnd

Section "Card Viewer (required)" Main
  SectionIn RO
  SetOutPath "$INSTDIR"
  !include "${INSTALL_MANIFEST}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} $Portable != "1"
    CreateDirectory "$SMPROGRAMS\Card Viewer"
    CreateShortcut "$SMPROGRAMS\Card Viewer\Card Viewer.lnk" "$INSTDIR\CardViewer.exe"
    CreateShortcut "$SMPROGRAMS\Card Viewer\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Card Viewer"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\CardViewer.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${Else}
    FileOpen $0 "$INSTDIR\portable-install.marker" w
    FileClose $0
  ${EndIf}
SectionEnd

Section /o "Desktop shortcut" Desktop
  ${If} $Portable != "1"
    CreateShortcut "$DESKTOP\Card Viewer.lnk" "$INSTDIR\CardViewer.exe"
  ${EndIf}
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  IfFileExists "$INSTDIR\portable-install.marker" skipRegistration
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    Delete "$DESKTOP\Card Viewer.lnk"
    Delete "$SMPROGRAMS\Card Viewer\Card Viewer.lnk"
    Delete "$SMPROGRAMS\Card Viewer\Uninstall.lnk"
    RMDir "$SMPROGRAMS\Card Viewer"
  ${EndIf}
  skipRegistration:
  !include "${UNINSTALL_MANIFEST}"
  Delete "$INSTDIR\portable-install.marker"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
