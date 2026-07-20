# CaptureIt

[English](README.md)

누구나 쉽게 쓸 수 있는 화면 캡처 프로그램입니다.
Windows용 풀 에디션(.NET 8 + WPF)과 Linux/macOS용 크로스 플랫폼 에디션(Avalonia)을 제공합니다.
Made by **Heechan Jeong** · MIT License

![Windows editor](docs/screenshot-editor.png)

## 다운로드 · 설치

**[⬇ 최신 버전 다운로드](https://github.com/th00tames1/CaptureIt/releases/latest)**

| OS | 파일 | 설치 방법 |
|---|---|---|
| **Windows 10/11** | [`CaptureIt-Setup-win-x64.exe`](https://github.com/th00tames1/CaptureIt/releases/latest/download/CaptureIt-Setup-win-x64.exe) | 실행하면 끝 (관리자 권한 불필요) |
| Windows (무설치) | [`CaptureIt-Portable-win-x64.zip`](https://github.com/th00tames1/CaptureIt/releases/latest/download/CaptureIt-Portable-win-x64.zip) | 압축 풀고 exe 실행 |
| **Linux x64** | [`CaptureIt-linux-x64.tar.gz`](https://github.com/th00tames1/CaptureIt/releases/latest/download/CaptureIt-linux-x64.tar.gz) | 압축 해제 후 `./install.sh` |
| **macOS (Apple Silicon)** | [`CaptureIt-macos-apple-silicon.zip`](https://github.com/th00tames1/CaptureIt/releases/latest/download/CaptureIt-macos-apple-silicon.zip) | 압축 해제 → **우클릭 → 열기** |
| macOS (Intel) | [`CaptureIt-macos-intel.zip`](https://github.com/th00tames1/CaptureIt/releases/latest/download/CaptureIt-macos-intel.zip) | 압축 해제 → **우클릭 → 열기** |

> **macOS**: 서명되지 않은 앱이라 첫 실행은 우클릭 → 열기로 해야 하며, 화면 기록 권한을 허용해야 합니다.
> **Linux**: `gnome-screenshot` / `spectacle` / `grim`+`slurp` / `scrot` / ImageMagick 중 하나가 필요합니다
> (대부분의 배포판에 기본 포함). 클립보드 복사는 `wl-clipboard`(Wayland) 또는 `xclip`(X11).

## Windows 에디션 주요 기능

![Windows main toolbar](docs/screenshot-main.png)

### 캡처

| 기능 | 설명 | 기본 단축키 |
|------|------|------------|
| 영역 캡처 | 화면이 잠시 멈추고, 원하는 부분을 드래그로 선택 | `Ctrl+Shift+A` |
| 단위 영역 | 버튼·이미지 같은 화면 요소를 하이라이트해 클릭 한 번으로 | `Ctrl+Shift+E` |
| 창 캡처 | 마우스를 올린 창이 강조되고, 클릭 한 번으로 캡처 | `Ctrl+Shift+W` |
| 전체 캡처 | 모든 모니터 화면 전체를 한 번에 캡처 | `Ctrl+Shift+F` |
| 스크롤 캡처 | 영역을 드래그하면 자동 스크롤하며 긴 페이지를 이어붙임 | `Ctrl+Shift+S` |
| 고정 크기 | 화면에 놓인 틀을 모서리로 조절하거나 숫자 입력 → Enter/더블클릭 캡처 | `Ctrl+Shift+D` |
| 같은 영역 다시 캡처 | 직전 영역을 오버레이 없이 즉시 재캡처 | `Ctrl+Shift+R` |
| 지연 캡처 | 3초/5초/10초 뒤 자동 캡처 (메뉴·팝업 캡처용) | — |

> 선호 단축키가 다른 프로그램에 이미 사용 중이면 `Ctrl+Alt+…` → `Alt+Shift+…`
> 순으로 자동 대체 등록되고, 실제 등록된 키가 버튼과 설정에 표시됩니다.

### 최근 캡처 목록

- 모든 캡처가 자동으로 목록에 쌓입니다 (최대 100장, 앱을 껐다 켜도 유지)
- 편집기 오른쪽 사이드바에서 썸네일로 보고, 클릭하면 언제든 다시 편집
- 편집 중 다른 항목으로 이동해도 그리던 내용이 자동 보존(해당 항목에 합성)
- 목록에서 바로 `Ctrl+C` → 어디든 `Ctrl+V` (채팅·문서에는 이미지로, 탐색기·메신저에는 파일로)
- 합성·자르기·저장으로 구워진 편집도 `Ctrl+Z`/`Ctrl+Y`로 단계별 되돌리기 (항목별 8단계)
- 항목별 우클릭: 복사 / 저장 폴더에 저장 / 삭제 · 하단: 모두 저장 / 모두 삭제

### 편집기

- 펜, 형광펜, 직선, 화살표, 사각형, 원, 텍스트, 번호 스탬프 ①②③
- 모자이크(개인정보 가리기) · 지우개 · 자르기 · 초기화
- 8가지 색상 + 4단계 굵기 · `Ctrl+휠` 확대/축소
- 저장(`Ctrl+S`) · 복사(`Ctrl+C`) · 다른 이름으로 저장 (PNG/JPG/BMP)

### 편의 기능

- 한국어/영어 전환 · Windows 시작 시 자동 실행 · 트레이 상주
- 캡처 시 자동 클립보드 복사(이미지+PNG+파일 동시) · 창 겹침 방지 배치
- 메인 툴바 항상 위 고정(핀) · 창 위치 기억

## 크로스 플랫폼 에디션 (Linux · macOS)

![Cross-platform edition](docs/screenshot-cross.png)

Avalonia 기반의 가벼운 에디션으로, 캡처는 각 OS의 네이티브 스크린샷 도구에 위임합니다
(macOS `screencapture`, Linux `gnome-screenshot`/`grim` 등 — Wayland·멀티모니터·권한을
OS가 가장 잘 처리하기 때문입니다).

| 기능 | 지원 |
|---|---|
| 영역/전체 캡처 | ✅ (macOS·Linux는 OS 네이티브 영역 선택 UI) |
| 주석: 펜·사각형·화살표 | ✅ |
| 자르기 · 실행 취소 | ✅ |
| 캡처 목록 (100장) | ✅ |
| 클립보드 복사 · PNG 저장 | ✅ |
| 한국어/영어 | ✅ |
| 창/요소/스크롤/고정크기 캡처, 전역 단축키, 트레이 | Windows 에디션 전용 |

## 소스에서 빌드

```bash
# Windows 풀 에디션 (Windows에서만)
dotnet run --project CaptureIt

# 크로스 플랫폼 에디션 (어디서나)
dotnet run --project CaptureIt.Cross
```

릴리스 빌드는 [.github/workflows/release.yml](.github/workflows/release.yml)이 태그 푸시 시 자동 생성합니다.

## 알려진 제한

- Windows: 모니터별 배율(DPI)이 다른 환경에서 영역 선택 좌표가 약간 어긋날 수 있습니다.
- Windows 스크롤 캡처: 선택 영역 안에 고정 헤더가 있으면 일찍 멈출 수 있습니다 — 실제로 스크롤되는 본문만 드래그하세요.
- macOS/Linux 에디션은 CI에서 빌드되지만 상시 테스트 환경은 Windows입니다. 문제가 있으면 [이슈](https://github.com/th00tames1/CaptureIt/issues)로 알려주세요.

## 라이선스

MIT © 2026 [Heechan Jeong](https://github.com/th00tames1)
